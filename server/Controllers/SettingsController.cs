using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 用户设置 API — 通知偏好 / 备份 / 恢复 / 清除数据
/// </summary>
[ApiController]
[Route("api/settings")]
[Authorize(Policy = "ParentOrAdmin")]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SettingsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/settings — 读取用户设置
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var configs = await _db.SystemConfigs.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value);
        var isAdmin = User.IsInRole("admin");
        return Ok(new
        {
            notification = new
            {
                usageWarn = ParseBool(configs, "notification_usage_warn", true),
                deviceOffline = ParseBool(configs, "notification_device_offline", true),
                timeoutAlert = ParseBool(configs, "notification_timeout_alert", true),
                announcementPush = ParseBool(configs, "notification_announcement_push", false),
            },
            // [TASK-PRELAUNCH-P1-FIX] server 字段仅管理员可见（parent 不返回服务配置信息）
            server = isAdmin ? new
            {
                webPort = ParseInt(configs, "web_port", 5000),
                p2pPort = ParseInt(configs, "p2p_port", 9527),
                bindAddress = configs.GetValueOrDefault("bind_address", "127.0.0.1"),
                relayHost = configs.GetValueOrDefault("relay_host", ""),
            } : null,
            dataRetentionDays = ParseInt(configs, "data_retention_days", 90),
            backupDir = configs.GetValueOrDefault("backup_dir", "backups"),
        });
    }

    /// <summary>
    /// PUT /api/settings — 保存用户设置
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SettingsSaveRequest request)
    {
        if (request.Notification != null)
        {
            await SetConfig("notification_usage_warn", request.Notification.UsageWarn?.ToString() ?? "true");
            await SetConfig("notification_device_offline", request.Notification.DeviceOffline?.ToString() ?? "true");
            await SetConfig("notification_timeout_alert", request.Notification.TimeoutAlert?.ToString() ?? "true");
            await SetConfig("notification_announcement_push", request.Notification.AnnouncementPush?.ToString() ?? "false");
        }
        // [TASK-PRELAUNCH-P1-FIX] 服务配置仅管理员可保存（UI 已隐藏，接口同样拦截）
        if (request.Server != null)
        {
            if (!User.IsInRole("admin"))
                return Forbid();

            // [SEC-P1] 值域校验：端口范围、地址格式白名单，防非法配置值入库
            if (request.Server.WebPort is > 0 and <= 65535)
                await SetConfig("web_port", request.Server.WebPort.Value.ToString());
            else if (request.Server.WebPort is not null)
                return BadRequest(new { error = "Web 端口超出范围（1-65535）" });
            if (request.Server.P2pPort is > 0 and <= 65535)
                await SetConfig("p2p_port", request.Server.P2pPort.Value.ToString());
            else if (request.Server.P2pPort is not null)
                return BadRequest(new { error = "P2P 端口超出范围（1-65535）" });
            if (!string.IsNullOrEmpty(request.Server.BindAddress))
            {
                if (!IsValidHostOrIp(request.Server.BindAddress))
                    return BadRequest(new { error = "绑定地址格式无效" });
                await SetConfig("bind_address", request.Server.BindAddress.Trim());
            }
            if (!string.IsNullOrEmpty(request.Server.RelayHost))
            {
                if (!IsValidHostOrIp(request.Server.RelayHost.Trim()))
                    return BadRequest(new { error = "中继地址格式无效" });
                await SetConfig("relay_host", request.Server.RelayHost.Trim());
            }
        }

        // [SEC-P1] 数据保留/备份目录为全局配置，仅管理员可设置（防家长改全局数据生命周期）
        if (request.DataRetentionDays is > 0 || !string.IsNullOrEmpty(request.BackupDir))
        {
            if (!User.IsInRole("admin"))
                return Forbid();

            if (request.DataRetentionDays is > 0)
            {
                if (request.DataRetentionDays > 3650)
                    return BadRequest(new { error = "数据保留天数超出范围（1-3650）" });
                await SetConfig("data_retention_days", request.DataRetentionDays.Value.ToString());
            }
            if (!string.IsNullOrEmpty(request.BackupDir))
            {
                if (!IsValidBackupDir(request.BackupDir))
                    return BadRequest(new { error = "备份目录格式无效" });
                await SetConfig("backup_dir", request.BackupDir.Trim());
            }
        }

        // [SEC-K10] 设置保存审计（不记录具体值）
        await AuditAsync("settings.save", "Settings", null, null);

        return Ok(new { message = "设置已保存" });
    }

    /// <summary>
    /// POST /api/settings/backup — 导出备份（JSON）
    /// [SEC-P1] 仅管理员可导出；设备导出剥离凭据字段（证书指纹/配对码/设备令牌），
    /// 防止备份文件泄露成为身份锚点/令牌的侧信道。恢复后设备需重新配对（红线 R4.3）。
    /// </summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup()
    {
        // [SEC-P1] 备份含全量设备/用户/配置，仅管理员可导出
        if (!User.IsInRole("admin"))
            return Forbid();

        var devices = await _db.Devices.AsNoTracking()
            .Select(d => new
            {
                d.Id,
                d.DeviceId,
                d.DeviceName,
                d.Platform,
                d.MacAddress,
                d.IpAddress,
                d.PairStatus,
                d.OnlineStatus,
                d.OwnerUserId,
                d.AppCategories,
                d.LastResetOffsetMinutes,
                d.LastResetDate,
                d.TodayAdjustedMinutes,
                d.IsActive,
            })
            .ToListAsync();
        var policies = await _db.Policies.AsNoTracking().ToListAsync();
        var announcements = await _db.Announcements.AsNoTracking().ToListAsync();
        var configs = await _db.SystemConfigs.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value);
        var users = await _db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.Email, u.IsActive })
            .ToListAsync();

        var payload = new
        {
            version = "3.0",
            exportedAt = DateTime.UtcNow,
            // [SEC-P1] 备份中不含凭据字段（CertFingerprint/PairCode/DeviceToken/PasswordHash）
            configs,
            users,
            devices,
            policies,
            announcements,
        };

        // [SEC-K10] 备份导出审计
        await AuditAsync("settings.backup", "Settings", null, $"{{\"devices\":{devices.Count},\"users\":{users.Count}}}");

        return Ok(payload);
    }

    /// <summary>
    /// POST /api/settings/restore — 从备份 JSON 恢复（仅导入非冲突数据）
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(IFormFile file)
    {
        // [TASK-PRELAUNCH-P1-FIX] 恢复数据仅管理员（家长 UI 已隐藏，接口同样拦截）
        if (!User.IsInRole("admin"))
            return Forbid();

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "请选择备份文件" });
        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "备份文件过大（上限 10MB）" });
        if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "仅支持 .json 备份文件" });

        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync();

        BackupPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BackupPayload>(json);
        }
        catch
        {
            return BadRequest(new { error = "备份文件格式无效" });
        }

        if (payload == null)
            return BadRequest(new { error = "备份文件内容为空" });

        var restored = 0;

        if (payload.Devices != null)
        {
            foreach (var d in payload.Devices)
            {
                if (string.IsNullOrEmpty(d.DeviceId)) continue;
                var exists = await _db.Devices.AnyAsync(x => x.DeviceId == d.DeviceId);
                if (exists) continue;
                _db.Devices.Add(new Device
                {
                    DeviceId = d.DeviceId,
                    DeviceName = d.DeviceName ?? d.DeviceId,
                    Platform = d.Platform ?? "android",
                    IpAddress = d.IpAddress,
                    CertFingerprint = d.CertFingerprint,
                    PairStatus = d.PairStatus ?? "unpaired",
                    OnlineStatus = d.OnlineStatus ?? "offline",
                    IsActive = true,
                });
                restored++;
            }
        }

        if (payload.Announcements != null)
        {
            foreach (var a in payload.Announcements)
            {
                if (string.IsNullOrEmpty(a.Title)) continue;
                _db.Announcements.Add(new Announcement
                {
                    Title = a.Title,
                    Content = a.Content ?? string.Empty,
                    Priority = a.Priority ?? "normal",
                    Status = "draft",
                    CreatedBy = GetUserId() ?? 1,
                    ValidFrom = a.ValidFrom,
                    ValidUntil = a.ValidUntil,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                restored++;
            }
        }

        await _db.SaveChangesAsync();

        // [SEC-K10] 恢复操作审计
        await AuditAsync("settings.restore", "Settings", null, $"{{\"restored\":{restored}}}");

        return Ok(new { message = "数据已恢复", restored });
    }

    /// <summary>
    /// POST /api/settings/clear-data — 清除使用数据（保留账号与设备）
    /// </summary>
    [HttpPost("clear-data")]
    public async Task<IActionResult> ClearData()
    {
        // [TASK-PRELAUNCH-P1-FIX] 清除数据仅管理员（家长 UI 已隐藏，接口同样拦截）
        if (!User.IsInRole("admin"))
            return Forbid();

        var usage = await _db.UsageRecords.CountAsync();
        var summaries = await _db.DailySummaries.CountAsync();

        await _db.UsageRecords.ExecuteDeleteAsync();
        await _db.DailySummaries.ExecuteDeleteAsync();

        // [SEC-K10] 数据清除安全事件审计（审计日志本身保留，保证可追溯）
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = "settings.clear_data",
            TargetType = "Data",
            TargetId = null,
            Detail = $"{{\"usageRecords\":{usage},\"summaries\":{summaries}}}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        return Ok(new { message = "使用数据已清除", removedUsageRecords = usage, removedSummaries = summaries });
    }

    // ========== helpers ==========

    private async Task SetConfig(string key, string value)
    {
        var config = await _db.SystemConfigs.FindAsync(key);
        if (config == null)
        {
            _db.SystemConfigs.Add(new SystemConfig { Key = key, Value = value });
        }
        else
        {
            config.Value = value;
            config.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    private static bool ParseBool(Dictionary<string, string> configs, string key, bool fallback)
        => bool.TryParse(configs.GetValueOrDefault(key), out var v) ? v : fallback;

    private static int ParseInt(Dictionary<string, string> configs, string key, int fallback)
        => int.TryParse(configs.GetValueOrDefault(key), out var v) ? v : fallback;

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    // [SEC-K10] 审计日志
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

    /// <summary>[SEC] 主机名/IP 白名单校验：字母数字/点/连字符/下划线；含冒号时按 IPv6 解析</summary>
    private static bool IsValidHostOrIp(string value)
    {
        var v = value.Trim();
        if (v.Length is < 1 or > 255) return false;
        if (v.Contains(':'))
            return System.Net.IPAddress.TryParse(v.Trim('[', ']'), out _); // 仅允许合法 IPv6
        return v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    /// <summary>[SEC] 备份目录校验：相对简单路径，禁止穿越（..）/绝对路径/盘符</summary>
    private static bool IsValidBackupDir(string value)
    {
        var v = value.Trim();
        if (v.Length is < 1 or > 128) return false;
        if (v.Contains("..") || v.StartsWith('/') || v.StartsWith('\\') || v.Contains(':'))
            return false;
        return v.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '\\' or '-' or '_' or '.');
    }
}

/// <summary>
/// 设置保存请求
/// </summary>
public class SettingsSaveRequest
{
    public NotificationSettingsDto? Notification { get; set; }
    public ServerSettingsDto? Server { get; set; }
    public int? DataRetentionDays { get; set; }
    public string? BackupDir { get; set; }
}

public class NotificationSettingsDto
{
    public bool? UsageWarn { get; set; }
    public bool? DeviceOffline { get; set; }
    public bool? TimeoutAlert { get; set; }
    public bool? AnnouncementPush { get; set; }
}

public class ServerSettingsDto
{
    public int? WebPort { get; set; }
    public int? P2pPort { get; set; }
    public string? BindAddress { get; set; }
    public string? RelayHost { get; set; }
}

/// <summary>
/// 备份 JSON 载荷（恢复用）
/// </summary>
public class BackupPayload
{
    public string? Version { get; set; }
    public List<BackupDevice>? Devices { get; set; }
    public List<BackupAnnouncement>? Announcements { get; set; }
}

public class BackupDevice
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
    public string? IpAddress { get; set; }
    public string? CertFingerprint { get; set; }
    public string? PairStatus { get; set; }
    public string? OnlineStatus { get; set; }
}

public class BackupAnnouncement
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Priority { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
}
