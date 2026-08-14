using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 策略配置 API — 查询 / 保存 / 推送（P2P 实时下发）
/// </summary>
[ApiController]
[Route("api/policies")]
[Authorize(Policy = "ParentOrAdmin")]
public class PoliciesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly P2pMessageHandler _messageHandler;
    private readonly P2pListenerService _p2p;
    private readonly ILogger<PoliciesController> _logger;

    public PoliciesController(
        AppDbContext db,
        P2pMessageHandler messageHandler,
        P2pListenerService p2p,
        ILogger<PoliciesController> logger)
    {
        _db = db;
        _messageHandler = messageHandler;
        _p2p = p2p;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/policies/{deviceId} — 获取设备策略
    /// [SEC-K2] 设备归属校验：家长仅可访问自己绑定的设备，越权一律 403
    /// </summary>
    [HttpGet("{deviceId:int}")]
    public async Task<IActionResult> Get(int deviceId)
    {
        var (access, device) = await DeviceAccess.CheckAsync(_db, deviceId, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        return Ok(ToDto(policy ?? new Policy { DeviceId = deviceId }));
    }

    /// <summary>
    /// PUT /api/policies/{deviceId} — 保存策略（保存后立即推送）
    /// [SEC-K2] 设备归属校验：越权一律 403
    /// </summary>
    [HttpPut("{deviceId:int}")]
    public async Task<IActionResult> Save(int deviceId, [FromBody] PolicySaveRequest request)
    {
        var (access, device) = await DeviceAccess.CheckAsync(_db, deviceId, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        // [SEC-K7] 输入白名单校验：非法值直接 400 拒绝，杜绝 ISO 时间戳等脏数据落库
        if (!TryValidatePolicy(request, out var validationError))
        {
            _logger.LogWarning("[Policy] 保存被拒（输入校验失败）: deviceId={D}, reason={R}", deviceId, validationError);
            return BadRequest(new { error = validationError });
        }

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        if (policy == null)
        {
            policy = new Policy { DeviceId = deviceId };
            _db.Policies.Add(policy);
        }

        policy.DailyLimitMinutes = Math.Clamp(request.DailyLimitMinutes, 30, 480);
        policy.BedtimeStart = request.BedtimeStart;
        policy.BedtimeEnd = request.BedtimeEnd;
        policy.OvertimeAction = request.TimeoutAction ?? "full_lock";
        policy.WhitelistApps = JsonSerializer.Serialize(request.Whitelist ?? new List<string>());
        policy.BlacklistApps = JsonSerializer.Serialize(request.Blacklist ?? new List<string>());

        // [TASK-PRELAUNCH-P1] 分类限额暂不可用：忽略前端提交，强制 -1（不限），不下发分类限额策略项
        // （Android 端分类累计闭环尚未完成，避免家长误以为已生效）
        policy.CategoryGameLimit = -1;
        policy.CategorySocialLimit = -1;
        policy.CategoryVideoLimit = -1;
        policy.CategoryLearningLimit = -1;
        policy.IsActive = true;
        policy.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // 立即推送（设备在线时）
        var pushed = await TryPush(device!, policy);

        await AuditAsync("policy.save", "Device", deviceId, $"{{\"pushed\":{pushed}}}");

        return Ok(new { message = "策略已保存", pushed, policy = ToDto(policy) });
    }

    /// <summary>
    /// POST /api/policies/{deviceId}/push — 手动推送策略到在线设备
    /// [SEC-K2] 设备归属校验：越权一律 403
    /// </summary>
    [HttpPost("{deviceId:int}/push")]
    public async Task<IActionResult> Push(int deviceId)
    {
        var (access, device) = await DeviceAccess.CheckAsync(_db, deviceId, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        var pushed = await TryPush(device!, policy);

        await AuditAsync("policy.push", "Device", deviceId, $"{{\"pushed\":{pushed}}}");

        return pushed
            ? Ok(new { message = "策略已推送" })
            : BadRequest(new { error = "设备不在线，策略已保存，将在设备重连后自动下发" });
    }

    /// <summary>
    /// POST /api/policies/{deviceId}/reset-limit — 重置当日使用限额（重新开始计时）
    /// 说明：仅重置“今日已用”的计时基准；重置前已产生的使用记录仍保留，报告照常统计
    /// [TASK-PRELAUNCH-P4] 落库重置偏移：立即以服务端原始累计作为偏移估计值（设备页立刻归零显示），
    /// 儿童端下次 usage_report 带回自算偏移后覆盖精修（需求 7 第 1 条）
    /// </summary>
    [HttpPost("{deviceId:int}/reset-limit")]
    public async Task<IActionResult> ResetLimit(int deviceId)
    {
        // [SEC-K2] 设备归属校验：越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, deviceId, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var resetAt = DateTime.UtcNow;
        var resetAtUnix = new DateTimeOffset(resetAt).ToUnixTimeSeconds();
        var today = AppClock.TodayShanghai();

        // [TASK-PRELAUNCH-P4] 重置偏移 = 当前原始累计（估计值），落库后设备页/仪表盘立即按调整后口径显示
        var summary = await _db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.SummaryDate == today);
        device!.LastResetDate = today;
        device.LastResetOffsetMinutes = Math.Max(0, summary?.TotalMinutes ?? 0);
        // [FIX-100] 重置后儿童端上报值归零：立即按 0 显示，儿童端下次上报以自算值覆盖
        device.TodayAdjustedMinutes = 0;

        // 先挂起待发标记（设备离线时保留，重连握手补推）
        device.PendingResetAt = resetAt;
        await _db.SaveChangesAsync();

        // 设备在线则立即推送
        var json = _messageHandler.BuildLimitResetMessage(device.DeviceId, resetAtUnix);
        var pushed = await _p2p.SendToDevice(device.DeviceId, json);
        if (pushed)
        {
            device.PendingResetAt = null;
            await _db.SaveChangesAsync();
        }

        await AuditAsync("policy.reset_limit", "Device", deviceId,
            $"{{\"pushed\":{pushed},\"resetAt\":{resetAtUnix},\"offsetMin\":{device.LastResetOffsetMinutes}}}");

        var limit = (await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId))
            ?.DailyLimitMinutes ?? 120;

        return Ok(new
        {
            message = pushed
                ? "当日限额已重置，儿童端已重新开始计时"
                : "设备离线，重置指令已挂起，儿童端重连后自动生效",
            pushed,
            resetAt = resetAtUnix,
            todayUsageMinutes = 0,
            todayRemainingMinutes = limit,
            lastResetOffsetMinutes = device.LastResetOffsetMinutes,
        });
    }

    // ========== helpers ==========

    /// <summary>
    /// [SEC-K7] 策略输入白名单校验：就寝时间 HH:mm、超时动作枚举、应用包名格式
    /// </summary>
    private static bool TryValidatePolicy(PolicySaveRequest request, out string error)
    {
        error = "";

        // 就寝时间：可空或成对出现，格式 HH:mm（24 小时制）
        if (string.IsNullOrEmpty(request.BedtimeStart) != string.IsNullOrEmpty(request.BedtimeEnd))
        {
            error = "就寝开始/结束时间必须成对设置";
            return false;
        }
        foreach (var t in new[] { request.BedtimeStart, request.BedtimeEnd })
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (!TimeOnly.TryParseExact(t, "HH:mm", out _))
            {
                error = $"就寝时间格式无效（应为 HH:mm）：{t}";
                return false;
            }
        }

        // 超时动作白名单（与 Android PolicyConfig：full_lock/partial_lock/warn_only 对齐）
        if (!string.IsNullOrEmpty(request.TimeoutAction)
            && request.TimeoutAction is not ("full_lock" or "partial_lock" or "warn_only"))
        {
            error = $"超时动作无效：{request.TimeoutAction}";
            return false;
        }

        // 应用包名：Android 包名格式白名单，列表上限 200 条
        foreach (var (list, label) in new[] { (request.Whitelist, "白名单"), (request.Blacklist, "黑名单") })
        {
            if (list == null) continue;
            if (list.Count > 200)
            {
                error = $"{label}最多 200 个应用";
                return false;
            }
            foreach (var pkg in list)
            {
                if (!IsValidPackageName(pkg))
                {
                    error = $"{label}包含非法包名：{pkg}";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// [SEC-K7] Android 应用包名校验：com.example.app_1 形式，点分段，
    /// 每段以字母开头、仅含字母/数字/下划线，总长 ≤ 200
    /// </summary>
    private static bool IsValidPackageName(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg) || pkg.Length > 200) return false;
        var segments = pkg.Split('.');
        if (segments.Length < 2) return false;
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || !char.IsAsciiLetter(seg[0])) return false;
            foreach (var c in seg)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
            }
        }
        return true;
    }

    private async Task<bool> TryPush(Device device, Policy? policy)
    {
        // [TASK-OPT-12-P4-DEEPEN] 推送时携带设备应用分类（app_categories）
        var json = _messageHandler.BuildPolicyPushMessage(device.DeviceId, policy, device.AppCategories);
        return await _p2p.SendToDevice(device.DeviceId, json);
    }

    private static object ToDto(Policy policy)
    {
        return new
        {
            deviceId = policy.DeviceId,
            dailyLimitMinutes = policy.DailyLimitMinutes,
            // [SEC-K7] 历史脏数据（ISO 时间戳等）按未设置返回，避免前端/儿童端解析异常
            bedtimeStart = NormalizeBedtime(policy.BedtimeStart),
            bedtimeEnd = NormalizeBedtime(policy.BedtimeEnd),
            categoryLimits = new[]
            {
                new { category = "game", label = "游戏", minutes = policy.CategoryGameLimit, enabled = policy.CategoryGameLimit >= 0 },
                new { category = "social", label = "社交", minutes = policy.CategorySocialLimit, enabled = policy.CategorySocialLimit >= 0 },
                new { category = "video", label = "视频", minutes = policy.CategoryVideoLimit, enabled = policy.CategoryVideoLimit >= 0 },
                // [TASK-PRELAUNCH-P2] 分类口径统一 learning（兼容旧 study）
                new { category = "learning", label = "学习", minutes = policy.CategoryLearningLimit, enabled = policy.CategoryLearningLimit >= 0 },
            },
            whitelist = DeserializeList(policy.WhitelistApps),
            blacklist = DeserializeList(policy.BlacklistApps),
            timeoutAction = policy.OvertimeAction,
            updatedAt = policy.UpdatedAt,
        };
    }

    /// <summary>
    /// [SEC-K7] 就寝时间归一化：仅合法 HH:mm 原样返回，其余（历史 ISO 时间戳等）视为未设置
    /// </summary>
    private static string? NormalizeBedtime(string? value)
        => string.IsNullOrEmpty(value) ? null
           : TimeOnly.TryParseExact(value, "HH:mm", out _) ? value : null;

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task AuditAsync(string action, string targetType, int? targetId, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>
/// 策略保存请求（前端 Policy 结构）
/// </summary>
public class PolicySaveRequest
{
    public int DailyLimitMinutes { get; set; } = 120;
    public string? BedtimeStart { get; set; }
    public string? BedtimeEnd { get; set; }
    public List<CategoryLimitItem>? CategoryLimits { get; set; }
    public List<string>? Whitelist { get; set; }
    public List<string>? Blacklist { get; set; }
    public string? TimeoutAction { get; set; }
}

/// <summary>
/// 分类限额条目
/// </summary>
public class CategoryLimitItem
{
    public string Category { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Minutes { get; set; }
    public bool Enabled { get; set; }
}
