using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;

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
    /// </summary>
    [HttpGet("{deviceId:int}")]
    public async Task<IActionResult> Get(int deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        return Ok(ToDto(policy ?? new Policy { DeviceId = deviceId }));
    }

    /// <summary>
    /// PUT /api/policies/{deviceId} — 保存策略（保存后立即推送）
    /// </summary>
    [HttpPut("{deviceId:int}")]
    public async Task<IActionResult> Save(int deviceId, [FromBody] PolicySaveRequest request)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

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

        // 分类限额映射（默认 -1 = 不限）
        policy.CategoryGameLimit = GetCategoryLimit(request.CategoryLimits, "game");
        policy.CategorySocialLimit = GetCategoryLimit(request.CategoryLimits, "social");
        policy.CategoryVideoLimit = GetCategoryLimit(request.CategoryLimits, "video");
        policy.CategoryLearningLimit = GetCategoryLimit(request.CategoryLimits, "study", "learning");
        policy.IsActive = true;
        policy.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // 立即推送（设备在线时）
        var pushed = await TryPush(device, policy);

        await AuditAsync("policy.save", "Device", deviceId, $"{{\"pushed\":{pushed}}}");

        return Ok(new { message = "策略已保存", pushed, policy = ToDto(policy) });
    }

    /// <summary>
    /// POST /api/policies/{deviceId}/push — 手动推送策略到在线设备
    /// </summary>
    [HttpPost("{deviceId:int}/push")]
    public async Task<IActionResult> Push(int deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        var pushed = await TryPush(device, policy);

        await AuditAsync("policy.push", "Device", deviceId, $"{{\"pushed\":{pushed}}}");

        return pushed
            ? Ok(new { message = "策略已推送" })
            : BadRequest(new { error = "设备不在线，策略已保存，将在设备重连后自动下发" });
    }

    /// <summary>
    /// POST /api/policies/{deviceId}/reset-limit — 重置当日使用限额（重新开始计时）
    /// 说明：仅重置“今日已用”的计时基准；重置前已产生的使用记录仍保留，报告照常统计
    /// </summary>
    [HttpPost("{deviceId:int}/reset-limit")]
    public async Task<IActionResult> ResetLimit(int deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var resetAt = DateTime.UtcNow;
        var resetAtUnix = new DateTimeOffset(resetAt).ToUnixTimeSeconds();

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
            $"{{\"pushed\":{pushed},\"resetAt\":{resetAtUnix}}}");

        return Ok(new
        {
            message = pushed
                ? "当日限额已重置，儿童端已重新开始计时"
                : "设备离线，重置指令已挂起，儿童端重连后自动生效",
            pushed,
            resetAt = resetAtUnix,
        });
    }

    // ========== helpers ==========

    private async Task<bool> TryPush(Device device, Policy? policy)
    {
        // [TASK-OPT-12-P4-DEEPEN] 推送时携带设备应用分类（app_categories）
        var json = _messageHandler.BuildPolicyPushMessage(device.DeviceId, policy, device.AppCategories);
        return await _p2p.SendToDevice(device.DeviceId, json);
    }

    private static int GetCategoryLimit(List<CategoryLimitItem>? limits, string category, string? altCategory = null)
    {
        var item = limits?.FirstOrDefault(l =>
            string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase) ||
            (altCategory != null && string.Equals(l.Category, altCategory, StringComparison.OrdinalIgnoreCase)));

        if (item == null || !item.Enabled)
            return -1;
        return Math.Max(0, item.Minutes);
    }

    private static object ToDto(Policy policy)
    {
        return new
        {
            deviceId = policy.DeviceId,
            dailyLimitMinutes = policy.DailyLimitMinutes,
            bedtimeStart = policy.BedtimeStart,
            bedtimeEnd = policy.BedtimeEnd,
            categoryLimits = new[]
            {
                new { category = "game", label = "游戏", minutes = policy.CategoryGameLimit, enabled = policy.CategoryGameLimit >= 0 },
                new { category = "social", label = "社交", minutes = policy.CategorySocialLimit, enabled = policy.CategorySocialLimit >= 0 },
                new { category = "video", label = "视频", minutes = policy.CategoryVideoLimit, enabled = policy.CategoryVideoLimit >= 0 },
                new { category = "study", label = "学习", minutes = policy.CategoryLearningLimit, enabled = policy.CategoryLearningLimit >= 0 },
            },
            whitelist = DeserializeList(policy.WhitelistApps),
            blacklist = DeserializeList(policy.BlacklistApps),
            timeoutAction = policy.OvertimeAction,
            updatedAt = policy.UpdatedAt,
        };
    }

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
