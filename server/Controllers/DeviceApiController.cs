using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

[ApiController]
[Route("api/v1/device")]
public class DeviceApiController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IJwtService _jwt;
    private readonly ILogger<DeviceApiController> _logger;

    public DeviceApiController(AppDbContext db, IJwtService jwt, ILogger<DeviceApiController> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] DeviceRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var existing = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == request.DeviceId);
        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(request.ExistingToken))
                return Conflict(new { error = "设备已注册，需提供既有令牌方可重新获取", code = "device_already_registered" });

            if (!_jwt.TryValidateDeviceToken(request.ExistingToken, existing.DeviceId, "device_api"))
                return StatusCode(403, new { error = "既有令牌无效或不匹配", code = "token_mismatch" });

            var (existingToken, _) = _jwt.GenerateDeviceToken(existing.DeviceId);
            return Ok(new { token = existingToken });
        }

        var device = new Device
        {
            DeviceId = request.DeviceId,
            DeviceName = $"设备-{request.DeviceId[^6..]}",
            Platform = request.Platform ?? "harmonyos",
            PairCode = null,
            PairStatus = "unpaired",
            OnlineStatus = "offline",
            OwnerUserId = null,
            IsActive = true,
        };

        _db.Devices.Add(device);
        await _db.SaveChangesAsync();

        _db.Policies.Add(new Policy
        {
            DeviceId = device.Id,
            DailyLimitMinutes = 120,
            OvertimeAction = "full_lock",
        });
        await _db.SaveChangesAsync();

        var (token, _) = _jwt.GenerateDeviceToken(device.DeviceId);

        _logger.LogInformation("[DeviceApi] 设备注册: {DeviceId}", device.DeviceId);

        return Ok(new { token });
    }

    [HttpGet("policies")]
    [Authorize]
    public async Task<IActionResult> GetPolicies()
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var device = await _db.Devices
            .Include(d => d.Policy)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId);

        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var policy = device.Policy;
        if (policy == null)
            return Ok(new { policies = Array.Empty<object>() });

        return Ok(new
        {
            policies = new[]
            {
                new
                {
                    dailyLimitMinutes = policy.DailyLimitMinutes,
                    bedtimeStart = policy.BedtimeStart,
                    bedtimeEnd = policy.BedtimeEnd,
                    categoryGameLimit = policy.CategoryGameLimit,
                    categorySocialLimit = policy.CategorySocialLimit,
                    categoryVideoLimit = policy.CategoryVideoLimit,
                    categoryLearningLimit = policy.CategoryLearningLimit,
                    overtimeAction = policy.OvertimeAction,
                    version = policy.Version,
                    appCategories = device.AppCategories,
                },
            },
        });
    }

    [HttpPost("usage-report")]
    [Authorize]
    public async Task<IActionResult> UsageReport([FromBody] DeviceUsageReportRequest request)
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        if (deviceId != request.DeviceId)
            return StatusCode(403, new { error = "设备 ID 与令牌不匹配" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"usage-report:{deviceId}:{clientIp}", 60, 3600))
            return StatusCode(429, new { error = "上报过于频繁，请稍后再试" });

        var validCategories = new HashSet<string> { "game", "social", "video", "learning", "other" };

        foreach (var record in request.Records)
        {
            var category = validCategories.Contains(record.Category.ToLowerInvariant())
                ? record.Category.ToLowerInvariant()
                : "other";

            _db.UsageRecords.Add(new UsageRecord
            {
                DeviceId = device.Id,
                AppPackage = record.AppPackage,
                AppName = record.AppName,
                Category = category,
                StartTime = DateTime.TryParse(request.Date, out var parsedDate)
                    ? parsedDate.Date : DateTime.UtcNow.Date,
                DurationSeconds = record.DurationSeconds,
                IsBlocked = record.IsBlocked,
            });
        }

        device.LastReportAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint") == true)
        {
            _logger.LogWarning("[DeviceApi] 使用上报重复记录已忽略: {DeviceId}", deviceId);
        }

        _logger.LogInformation("[DeviceApi] 使用上报: {DeviceId}, {Count} 条记录",
            deviceId, request.Records.Count);

        return Ok(new { success = true });
    }

    [HttpPost("heartbeat")]
    [Authorize]
    public async Task<IActionResult> Heartbeat([FromBody] DeviceHeartbeatRequest request)
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        if (deviceId != request.DeviceId)
            return StatusCode(403, new { error = "设备 ID 与令牌不匹配" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        device.OnlineStatus = "online";
        device.LastSeenAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var commands = new List<object>();

        if (device.PairStatus == "unpaired" && !string.IsNullOrEmpty(device.PairCode))
        {
            commands.Add(new { type = "wait_bind", bindCode = device.PairCode });
        }

        if (!string.IsNullOrEmpty(device.PendingResetAt?.ToString()))
        {
            commands.Add(new
            {
                type = "reset_daily_usage",
                resetAt = device.PendingResetAt,
            });
            device.PendingResetAt = null;
            await _db.SaveChangesAsync();
        }

        if (request.EmergencyActive)
        {
            _logger.LogWarning("[DeviceApi] 设备 {DeviceId} 紧急模式激活中", deviceId);
        }

        return Ok(new { success = true, commands });
    }

    [HttpGet("emergency-status")]
    [Authorize]
    public async Task<IActionResult> EmergencyStatus()
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var shouldRelease = await _db.SystemConfigs
            .Where(c => c.Key == $"emergency_release:{deviceId}")
            .AnyAsync();

        return Ok(new { shouldRelease });
    }

    [HttpGet("upgrade-check")]
    [Authorize]
    public async Task<IActionResult> UpgradeCheck()
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var platform = device.Platform ?? "harmonyos";

        var update = await _db.AppUpdates.AsNoTracking()
            .Where(u => u.Platform == platform && u.Status == "published")
            .OrderByDescending(u => u.VersionCode)
            .FirstOrDefaultAsync();

        if (update == null)
        {
            return Ok(new
            {
                version = "1.0.0",
                downloadUrl = "",
                forceUpdate = false,
                changelog = "",
            });
        }

        var abiUrls = UpdatesController.ParseAbiMap(update.AbiUrls);
        var defaultUrl = abiUrls.Values.FirstOrDefault() ?? "";

        return Ok(new
        {
            version = update.VersionName,
            downloadUrl = defaultUrl,
            forceUpdate = false,
            changelog = update.Changelog,
        });
    }

    [HttpPost("guard-event")]
    [Authorize]
    public async Task<IActionResult> GuardEvent([FromBody] System.Text.Json.JsonElement raw)
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var reqDeviceId = raw.TryGetProperty("deviceId", out var didProp) ? didProp.GetString() : null;
        if (reqDeviceId != deviceId)
            return StatusCode(403, new { error = "设备 ID 与令牌不匹配" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        _db.GuardEvents.Add(new GuardEvent
        {
            DeviceId = deviceId,
            EventType = raw.TryGetProperty("eventType", out var et) ? et.GetString() ?? "unknown" : "unknown",
            StartedAt = raw.TryGetProperty("startedAt", out var sa) ? sa.GetInt64() : null,
            EndedAt = raw.TryGetProperty("endedAt", out var ea) ? ea.GetInt64() : null,
            DurationSeconds = raw.TryGetProperty("durationSeconds", out var ds) ? ds.GetInt64() : null,
            Reason = raw.TryGetProperty("reason", out var r) ? r.GetString() : null,
            RestoredReason = raw.TryGetProperty("restoredReason", out var rr) ? rr.GetString() : null,
            WasEnforcing = raw.TryGetProperty("wasEnforcing", out var we) && we.GetBoolean(),
            HealthJson = raw.TryGetProperty("healthJson", out var hj) ? hj.GetRawText()
                : raw.TryGetProperty("health", out var h) ? h.GetRawText() : null,
            ReceivedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation("[DeviceApi] 守护事件: {DeviceId}, {EventType}", deviceId,
            raw.TryGetProperty("eventType", out var et2) ? et2.GetString() : "unknown");
        return Ok(new { success = true });
    }

    [HttpPost("announcement-ack")]
    [Authorize]
    public async Task<IActionResult> AnnouncementAck([FromBody] System.Text.Json.JsonElement raw)
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var reqDeviceId = raw.TryGetProperty("deviceId", out var didProp) ? didProp.GetString() : null;
        if (reqDeviceId != deviceId)
            return StatusCode(403, new { error = "设备 ID 与令牌不匹配" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var annId = raw.TryGetProperty("announcementId", out var aid) ? aid.GetInt32() : 0;
        if (annId <= 0)
            return BadRequest(new { error = "announcementId 必填" });

        var announcement = await _db.Announcements.FindAsync(annId);
        if (announcement == null)
            return NotFound(new { error = "公告不存在" });

        var delivery = await _db.AnnouncementDeliveries
            .FirstOrDefaultAsync(d => d.AnnouncementId == annId && d.DeviceId == device.Id);

        if (delivery != null)
        {
            delivery.AcknowledgedAt = DateTime.UtcNow;
            delivery.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.AnnouncementDeliveries.Add(new AnnouncementDelivery
            {
                AnnouncementId = annId,
                DeviceId = device.Id,
                AcknowledgedAt = DateTime.UtcNow,
                PushCount = 0,
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("[DeviceApi] 公告回执: {DeviceId}, announcement={AnnId}", deviceId, annId);
        return Ok(new { success = true });
    }

    [HttpPost("diagnostics-report")]
    [Authorize]
    public async Task<IActionResult> DiagnosticsReport([FromBody] System.Text.Json.JsonElement raw)
    {
        var deviceId = GetDeviceIdFromToken();
        if (deviceId == null)
            return Unauthorized(new { error = "无效的设备令牌" });

        var reqDeviceId = raw.TryGetProperty("deviceId", out var didProp) ? didProp.GetString() : null;
        if (reqDeviceId != deviceId)
            return StatusCode(403, new { error = "设备 ID 与令牌不匹配" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var record = new DiagnosticRecord
        {
            DeviceId = deviceId,
            AppVersion = raw.TryGetProperty("appVersion", out var av) ? av.GetString() : null,
            AndroidVersion = raw.TryGetProperty("osVersion", out var ov) ? ov.GetString()
                : raw.TryGetProperty("androidVersion", out var av2) ? av2.GetString() : null,
            DeviceModel = raw.TryGetProperty("deviceModel", out var dm) ? dm.GetString() : null,
            Manufacturer = raw.TryGetProperty("manufacturer", out var mf) ? mf.GetString() : null,
            PermissionStatus = raw.TryGetProperty("permissionStatus", out var ps) ? ps.GetRawText() : null,
            ServiceStatus = raw.TryGetProperty("serviceStatus", out var ss) ? ss.GetRawText() : null,
            RecentCrashes = raw.TryGetProperty("recentCrashes", out var rc) ? rc.GetRawText() : null,
            DbSizeBytes = raw.TryGetProperty("dbSizeBytes", out var dbs) ? dbs.GetInt64() : null,
            NetworkType = raw.TryGetProperty("networkType", out var nt) ? nt.GetString() : null,
            ReportedAt = DateTime.UtcNow,
        };

        _db.Diagnostics.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("[DeviceApi] 诊断上报: {DeviceId}", deviceId);
        return Ok(new { success = true });
    }

    [HttpPost("emergency-release")]
    [Authorize(Policy = "ParentOrAdmin")]
    public async Task<IActionResult> EmergencyRelease([FromBody] DeviceEmergencyReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == request.DeviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var userId = GetUserIdFromToken();
        if (userId == null)
            return Unauthorized(new { error = "无效的用户令牌" });

        if (device.OwnerUserId != userId.Value.ToString())
        {
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;
            if (userRole != "admin")
                return StatusCode(403, new { error = "仅设备归属家长或管理员可操作" });
        }

        const int maxDurationMinutes = 480;
        var durationMinutes = request.DurationMinutes ?? 60;
        if (durationMinutes <= 0 || durationMinutes > maxDurationMinutes)
            return BadRequest(new { error = $"时长须在 1~{maxDurationMinutes} 分钟之间" });

        var configKey = $"emergency_release:{device.DeviceId}";

        var existing = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.Key == configKey);
        var until = DateTime.UtcNow.AddMinutes(durationMinutes);
        var valueStr = $"active;until={until:O};reason={request.Reason ?? "parent_initiated"};by={userId.Value}";

        if (existing != null)
        {
            existing.Value = valueStr;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.SystemConfigs.Add(new SystemConfig
            {
                Key = configKey,
                Value = valueStr,
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("[DeviceApi] 紧急解除: {DeviceId}, duration={Dur}min, by={By}, reason={Reason}",
            device.DeviceId, durationMinutes, userId.Value, request.Reason);
        return Ok(new { success = true, durationMinutes, until });
    }

    [HttpGet("update-check")]
    [Authorize]
    public Task<IActionResult> UpdateCheck()
    {
        return UpgradeCheck();
    }

    private string? GetDeviceIdFromToken()
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role == "device" && !string.IsNullOrEmpty(sub))
            return sub;
        return null;
    }

    private int? GetUserIdFromToken()
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if ((role == "parent" || role == "admin") && int.TryParse(sub, out var id))
            return id;
        return null;
    }
}
