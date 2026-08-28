using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 设备管理 API — 列表 / 详情 / 解绑 / 配对码生成 / 手动配对
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize(Policy = "ParentOrAdmin")]
public class DevicesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly P2pMessageHandler _messageHandler;
    private readonly P2pListenerService _p2p;
    private readonly IJwtService _jwt;
    private readonly ActionTokenStore _actionTokens;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(
        AppDbContext db,
        P2pMessageHandler messageHandler,
        P2pListenerService p2p,
        IJwtService jwt,
        ActionTokenStore actionTokens,
        ILogger<DevicesController> logger)
    {
        _db = db;
        _messageHandler = messageHandler;
        _p2p = p2p;
        _jwt = jwt;
        _actionTokens = actionTokens;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/devices — 设备列表（含今日使用汇总）
    /// [TASK-PRELAUNCH-P4] 口径统一：todayUsageMinutes = 调整后已用（max(0, 原始累计 − 重置偏移)），
    /// 日期按 Asia/Shanghai；原始累计/剩余/偏移一并返回供 UI 区分标注（需求 7 第 1/2 条）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var today = AppClock.TodayShanghai();

        var query = _db.Devices.AsQueryable();

        // [REQ] 账号隔离：家长只看自己绑定的设备，管理员看全部
        // [SEC-K2] 家长无用户标识时返回空（杜绝 OwnerUserId IS NULL 的孤儿设备泄露）
        var currentUserId = GetUserId()?.ToString();
        var isAdmin = User.IsInRole("admin");
        if (!isAdmin)
        {
            if (currentUserId == null)
                return Ok(new { devices = Array.Empty<object>(), deviceCount = 0 });
            query = query.Where(d => d.OwnerUserId == currentUserId);
        }

        var devices = await query
            .Include(d => d.Policy)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

        // [TASK-PRELAUNCH-FIX-SCAN] 绑定账号显示（跨账号扫码排查）：OwnerUserId → Username 一次查询
        var users = await _db.Users.ToDictionaryAsync(u => u.Id.ToString());

        var summaries = await _db.DailySummaries
            .Where(s => s.SummaryDate == today)
            .ToDictionaryAsync(s => s.DeviceId);

        var result = devices.Select(d =>
        {
            summaries.TryGetValue(d.Id, out var summary);
            var raw = summary?.TotalMinutes ?? 0;
            // [FIX-100] 优先儿童端上报的调整后已用（当日有效），回退服务端计算
            var adjusted = AdjustedUsageCalculator.ResolveTodayUsedMinutes(
                d.TodayAdjustedMinutes, d.LastReportAt, DateTime.UtcNow,
                raw, d.LastResetOffsetMinutes, d.LastResetDate, today);
            var limit = d.Policy?.DailyLimitMinutes ?? 120;
            return new
            {
                id = d.Id,
                name = d.DeviceName,
                deviceId = d.DeviceId,
                ipAddress = d.IpAddress,
                osVersion = d.Platform,
                status = d.OnlineStatus,
                lastSeen = d.LastSeenAt,
                certFingerprint = d.CertFingerprint,
                pairedAt = d.UpdatedAt,
                // [TASK-PRELAUNCH-P4] 调整后口径（设备页/仪表盘/策略页统一显示）
                todayUsageMinutes = adjusted,
                rawTodayUsageMinutes = raw,
                todayRemainingMinutes = Math.Max(0, limit - adjusted),
                todayLimitMinutes = limit,
                lastResetOffsetMinutes = d.LastResetDate == today ? d.LastResetOffsetMinutes : 0,
                lastResetDate = d.LastResetDate,
                lastReportAt = d.LastReportAt,
                pairStatus = d.PairStatus,
                // [TASK-PRELAUNCH-FIX-SCAN] 绑定账号（null=无归属）
                ownerAccount = string.IsNullOrEmpty(d.OwnerUserId) ? null
                    : users.TryGetValue(d.OwnerUserId, out var u) ? u.Username : null,
            };
        }).ToList();

        // [TASK-ACCOUNT-V1] A6：响应带 deviceCount（前端 >10 台预警，不阻断）
        return Ok(new
        {
            devices = result,
            deviceCount = result.Count,
        });
    }

    /// <summary>
    /// GET /api/devices/{id} — 设备详情
    /// [TASK-PRELAUNCH-P4] 补充：调整后已用/剩余、原始累计、重置偏移、最近上报时间（需求 7 第 5 条）
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        // [SEC-K2] 设备归属校验：家长仅可访问自己绑定的设备，越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        // CheckAsync 已跟踪实体，补载策略导航
        await _db.Entry(device!).Reference(d => d.Policy).LoadAsync();

        var today = AppClock.TodayShanghai();
        var summary = await _db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == device.Id && s.SummaryDate == today);

        var raw = summary?.TotalMinutes ?? 0;
        // [FIX-100] 优先儿童端上报的调整后已用（当日有效），回退服务端计算
        var adjusted = AdjustedUsageCalculator.ResolveTodayUsedMinutes(
            device.TodayAdjustedMinutes, device.LastReportAt, DateTime.UtcNow,
            raw, device.LastResetOffsetMinutes, device.LastResetDate, today);
        var limit = device.Policy?.DailyLimitMinutes ?? 120;

        // [TASK-PRELAUNCH-FIX-SCAN] 绑定账号（跨账号扫码排查）；无归属为 null
        var ownerAccount = int.TryParse(device.OwnerUserId, out var ownerId)
            ? (await _db.Users.FindAsync(ownerId))?.Username
            : null;

        return Ok(new
        {
            id = device.Id,
            name = device.DeviceName,
            deviceId = device.DeviceId,
            platform = device.Platform,
            ipAddress = device.IpAddress,
            certFingerprint = device.CertFingerprint,
            pairStatus = device.PairStatus,
            status = device.OnlineStatus,
            lastSeen = device.LastSeenAt,
            isActive = device.IsActive,
            createdAt = device.CreatedAt,
            todayUsageMinutes = adjusted,
            rawTodayUsageMinutes = raw,
            todayRemainingMinutes = Math.Max(0, limit - adjusted),
            todayLimitMinutes = limit,
            lastResetOffsetMinutes = device.LastResetDate == today ? device.LastResetOffsetMinutes : 0,
            lastResetDate = device.LastResetDate,
            lastReportAt = device.LastReportAt,
            // [TASK-PRELAUNCH-FIX-SCAN] 绑定账号（null=无归属）
            ownerAccount = ownerAccount,
        });
    }

    /// <summary>
    /// PUT /api/devices/{id}/name — 重命名/自定义设备名称（家长区分多台设备）
    /// [SEC-K2] 家长仅可重命名自己绑定的设备，管理员可重命名任意设备。
    /// </summary>
    [HttpPut("{id:int}/name")]
    public async Task<IActionResult> Rename(int id, [FromBody] DeviceRenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "设备名称不能为空" });
        var name = request.Name.Trim();
        if (name.Length > 64)
            return BadRequest(new { error = "设备名称过长（最多 64 字）" });

        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var oldName = device!.DeviceName;
        device.DeviceName = name;
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("device.rename", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"old\":\"{oldName}\",\"new\":\"{name}\"}}");

        return Ok(new { id = device.Id, name = device.DeviceName, message = "设备名称已更新" });
    }

    /// <summary>
    /// DELETE /api/devices/{id} — 解绑设备
    /// [TASK-ACCOUNT-V1] A5：必须携带 X-Action-Token（POST /api/auth/verify-password 签发，
    /// 5 分钟单次有效、绑定 userId）；无/过期/跨账号一律 401。
    /// [TASK-MILESTONE-V3] A12/D2：解绑即硬删除设备行 + 策略 + 公告送达 + 使用记录/汇总 +
    /// 中继会话 + 配对信息等全部关联数据；重绑走全新设备身份（儿童端 device_id 一并重置，见需求 4）。
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Unpair(int id)
    {
        // [TASK-ACCOUNT-V1] 解绑前置：登录态密码二次验证一次性令牌
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();
        var actionToken = Request.Headers["X-Action-Token"].ToString();
        if (string.IsNullOrWhiteSpace(actionToken) || !_actionTokens.VerifyAndConsume(actionToken, userId.Value))
        {
            _logger.LogWarning("[Devices] 解绑缺少有效操作令牌: deviceId={Id}", id);
            return Unauthorized(new { error = "操作令牌缺失或已过期，请重新验证密码" });
        }

        // [SEC-K2] 设备归属校验：家长仅可解绑自己绑定的设备，越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var deviceIdStr = device!.DeviceId;
        var deviceName = device.DeviceName;

        // [TASK-MILESTONE-V3] A12 关联数据全清（按删除依赖序）
        // [TASK-MILESTONE-V3-REQ14] 用 RemoveRange 替代 ExecuteDelete：生产 SQLite 等价，
        // 且 EF InMemory 测试提供程序可执行（ExecuteDelete 不被 InMemory 支持，测试长期红灯）
        var deliveries = await _db.AnnouncementDeliveries.Where(d => d.DeviceId == id).ToListAsync();
        var policies = await _db.Policies.Where(p => p.DeviceId == id).ToListAsync();
        var usageRecords = await _db.UsageRecords.Where(r => r.DeviceId == id).ToListAsync();
        var summaries = await _db.DailySummaries.Where(s => s.DeviceId == id).ToListAsync();
        var pairings = await _db.PairingInfos.Where(p => p.DeviceId == id).ToListAsync();
        // 中继会话与诊断记录以 device_id 字符串关联
        var relaySessions = await _db.RelaySessions.Where(r => r.DeviceId == deviceIdStr).ToListAsync();
        var diagnostics = await _db.Diagnostics.Where(d => d.DeviceId == deviceIdStr).ToListAsync();
        _db.AnnouncementDeliveries.RemoveRange(deliveries);
        _db.Policies.RemoveRange(policies);
        _db.UsageRecords.RemoveRange(usageRecords);
        _db.DailySummaries.RemoveRange(summaries);
        _db.PairingInfos.RemoveRange(pairings);
        _db.RelaySessions.RemoveRange(relaySessions);
        _db.Diagnostics.RemoveRange(diagnostics);
        _db.Devices.Remove(device);
        await _db.SaveChangesAsync();

        await AuditAsync("device.unpair", "Device", device.Id,
            $"{{\"deviceId\":\"{deviceIdStr}\",\"name\":\"{deviceName}\",\"wipe\":\"hard_delete\"}}");

        _logger.LogInformation("[Devices] 设备已解绑并清除全部关联数据: {DeviceId}", deviceIdStr);
        return Ok(new { message = "设备已解绑，全部关联数据已清除" });
    }

    /// <summary>
    /// POST /api/devices/pairing-code — 生成 6 位配对码（5 分钟有效）
    /// </summary>
    [HttpPost("pairing-code")]
    public async Task<IActionResult> GeneratePairingCode()
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"pairing-code:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        var pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAt = DateTime.UtcNow.AddMinutes(5);

        _db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = null,
            PairCode = pairCode,
            PairMethod = "manual",
            PairStatus = "pending",
            // [SEC-P1] 记录归属账号（cancel 归属校验依赖此字段）
            OwnerUserId = GetUserId()?.ToString(),
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // [SEC-P2] 审计打码：不落明文配对码（6 位码泄露可直接劫持绑定流程）
        await AuditAsync("device.pairing-code", "PairingInfo", null, $"{{\"code\":\"******\"}}");

        return Ok(new
        {
            pairCode,
            expiresAt,
            expiresInSeconds = 300,
        });
    }

    /// <summary>
    /// POST /api/devices/pair — 手动配对（输入配对码 + 设备 IP）
    /// </summary>
    [HttpPost("pair")]
    public async Task<IActionResult> Pair([FromBody] ManualPairRequest request)
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"pair:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        if (string.IsNullOrWhiteSpace(request.PairingCode))
            return BadRequest(new { error = "配对码不能为空" });

        var pairingInfo = await _db.PairingInfos
            .Where(p => p.PairCode == request.PairingCode && p.PairStatus == "pending")
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        if (pairingInfo == null || pairingInfo.ExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "配对码无效或已过期" });

        // 绑定或创建设备
        // [SEC-K2] 设备归属：已绑定他人的设备禁止认领（越权 403）；新设备归属当前家长
        var currentUserId = GetUserId()?.ToString();
        Device device;
        if (pairingInfo.DeviceId is > 0)
        {
            device = await _db.Devices.FindAsync(pairingInfo.DeviceId);
            if (device == null)
                return NotFound(new { error = "绑定的设备不存在" });
            if (!string.IsNullOrEmpty(device.OwnerUserId)
                && device.OwnerUserId != currentUserId
                && !User.IsInRole("admin"))
            {
                return StatusCode(403, new { error = "无权认领该设备" });
            }
        }
        else
        {
            device = new Device
            {
                DeviceId = $"XP-{Guid.NewGuid():N}"[..14],
                DeviceName = request.DeviceName ?? "未命名设备",
                Platform = request.Platform ?? "android",
                IpAddress = request.IpAddress,
                PairCode = request.PairingCode,
                PairStatus = "paired",
                OnlineStatus = "offline",
                IsActive = true,
                OwnerUserId = currentUserId,
            };
            _db.Devices.Add(device);
            await _db.SaveChangesAsync();

            _db.Policies.Add(new Policy
            {
                DeviceId = device.Id,
                DailyLimitMinutes = 120,
                OvertimeAction = "full_lock",
            });
        }

        pairingInfo.DeviceId = device.Id;
        pairingInfo.PairStatus = "confirmed";
        pairingInfo.ConfirmedAt = DateTime.UtcNow;

        device.PairCode = request.PairingCode;
        device.PairStatus = "paired";
        device.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(request.IpAddress))
            device.IpAddress = request.IpAddress;

        await _db.SaveChangesAsync();

        await AuditAsync("device.pair", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"code\":\"{request.PairingCode}\"}}");

        return Ok(new
        {
            deviceId = device.DeviceId,
            id = device.Id,
            name = device.DeviceName,
            pairStatus = device.PairStatus,
        });
    }

    // ========== helpers ==========

    private async Task AuditAsync(string action, string? targetType, int? targetId, string? detail)
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

    /// <summary>
    /// GET /api/devices/{id}/app-categories — 查看设备应用分类列表（OPT12 需求 1）
    /// </summary>
    [HttpGet("{id:int}/app-categories")]
    public async Task<IActionResult> GetAppCategories(int id)
    {
        // [SEC-K2] 设备归属校验：越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var categories = DeserializeCategories(device!.AppCategories);
        return Ok(new { deviceId = device.DeviceId, categories });
    }

    /// <summary>
    /// PUT /api/devices/{id}/app-categories — 保存设备应用分类（全量覆盖）
    /// </summary>
    [HttpPut("{id:int}/app-categories")]
    public async Task<IActionResult> PutAppCategories(int id, [FromBody] AppCategoriesRequest request)
    {
        // [SEC-K2] 设备归属校验：越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        var valid = new HashSet<string> { "game", "social", "video", "learning", "other" };
        var invalid = request.Categories
            .Where(c => !valid.Contains(c.Category.ToLowerInvariant()))
            .Select(c => c.PackageName)
            .ToList();
        if (invalid.Count > 0)
            return BadRequest(new { error = $"非法分类值: {string.Join(", ", invalid)}" });

        var normalized = request.Categories
            .Select(c => new AppCategoryItem
            {
                PackageName = c.PackageName,
                AppName = c.AppName ?? string.Empty,
                Category = c.Category.ToLowerInvariant(),
            })
            .ToList();

        device!.AppCategories = System.Text.Json.JsonSerializer.Serialize(normalized);
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // [TASK-OPT-12-P4-DEEPEN] 保存后立即推送策略（含 app_categories）到在线设备
        try
        {
            var policy = await _db.Policies.FirstOrDefaultAsync(p => p.DeviceId == device.Id);
            var pushJson = _messageHandler.BuildPolicyPushMessage(device.DeviceId, policy, device.AppCategories);
            var pushed = await _p2p.SendToDevice(device.DeviceId, pushJson);
            _logger.LogInformation("[Devices] 设备 {DeviceId} 应用分类已推送, pushed={Pushed}",
                device.DeviceId, pushed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Devices] 应用分类推送失败: {DeviceId}", device.DeviceId);
        }

        await AuditAsync("device.app-categories", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"count\":{normalized.Count}}}");

        return Ok(new { deviceId = device.DeviceId, categories = normalized, message = "应用分类已保存" });
    }

    /// <summary>
    /// POST /api/devices/{id}/token — 生成/轮换设备级访问令牌（TASK-OPT-12-P4-DEEPEN）
    /// 返回：
    /// - deviceToken：设备令牌（POST /api/diagnostics 请求体 device_token 字段校验）
    /// - jwt：设备级 JWT（限定 scope=diagnostics+usage_report，Authorization: Bearer 头校验）
    /// </summary>
    [HttpPost("{id:int}/token")]
    public async Task<IActionResult> GenerateDeviceToken(int id)
    {
        // [SEC-K2] 设备令牌可访问诊断/上报数据，必须校验设备归属，越权一律 403
        var (access, device) = await DeviceAccess.CheckAsync(_db, id, User);
        if (access == DeviceAccessResult.NotFound)
            return NotFound(new { error = "设备不存在" });
        if (access == DeviceAccessResult.Forbidden)
            return StatusCode(403, new { error = "无权访问该设备" });

        // 生成随机设备令牌（重新生成即轮换旧令牌）
        device!.DeviceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // 生成设备级 JWT（限定 scope：diagnostics + usage_report，24 小时有效）
        var (jwt, expiresAt) = _jwt.GenerateDeviceToken(device.DeviceId);

        await AuditAsync("device.token", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\"}}");

        _logger.LogInformation("[Devices] 设备 {DeviceId} 令牌已生成", device.DeviceId);

        return Ok(new
        {
            deviceToken = device.DeviceToken,
            jwt,
            expiresAt,
            message = "设备令牌已生成",
        });
    }

    /// <summary>
    /// 反序列化应用分类 JSON（容错：损坏数据返回空列表）
    /// </summary>
    private static List<AppCategoryItem> DeserializeCategories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<AppCategoryItem>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<AppCategoryItem>>(json)
                   ?? new List<AppCategoryItem>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<AppCategoryItem>();
        }
    }
}

/// <summary>
/// 手动配对请求
/// </summary>
public class ManualPairRequest
{
    [System.ComponentModel.DataAnnotations.MaxLength(16)] public string PairingCode { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string? IpAddress { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string? DeviceName { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(32)] public string? Platform { get; set; }
}

/// <summary>
/// 设备重命名请求
/// </summary>
public class DeviceRenameRequest
{
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string Name { get; set; } = string.Empty;
}
