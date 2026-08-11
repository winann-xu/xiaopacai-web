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
        return Ok(new
        {
            notification = new
            {
                usageWarn = ParseBool(configs, "notification_usage_warn", true),
                deviceOffline = ParseBool(configs, "notification_device_offline", true),
                timeoutAlert = ParseBool(configs, "notification_timeout_alert", true),
                announcementPush = ParseBool(configs, "notification_announcement_push", false),
            },
            server = new
            {
                webPort = ParseInt(configs, "web_port", 5000),
                p2pPort = ParseInt(configs, "p2p_port", 9527),
                bindAddress = configs.GetValueOrDefault("bind_address", "127.0.0.1"),
            },
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
        if (request.Server != null)
        {
            if (request.Server.WebPort > 0) await SetConfig("web_port", request.Server.WebPort.ToString());
            if (request.Server.P2pPort > 0) await SetConfig("p2p_port", request.Server.P2pPort.ToString());
            if (!string.IsNullOrEmpty(request.Server.BindAddress)) await SetConfig("bind_address", request.Server.BindAddress);
        }
        if (request.DataRetentionDays > 0)
            await SetConfig("data_retention_days", request.DataRetentionDays.ToString());
        if (!string.IsNullOrEmpty(request.BackupDir))
            await SetConfig("backup_dir", request.BackupDir);

        return Ok(new { message = "设置已保存" });
    }

    /// <summary>
    /// POST /api/settings/backup — 导出备份（JSON）
    /// </summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup()
    {
        var devices = await _db.Devices.AsNoTracking().ToListAsync();
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
            configs,
            users,
            devices,
            policies,
            announcements,
        };

        return Ok(payload);
    }

    /// <summary>
    /// POST /api/settings/restore — 从备份 JSON 恢复（仅导入非冲突数据）
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "请选择备份文件" });

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
        return Ok(new { message = "数据已恢复", restored });
    }

    /// <summary>
    /// POST /api/settings/clear-data — 清除使用数据（保留账号与设备）
    /// </summary>
    [HttpPost("clear-data")]
    public async Task<IActionResult> ClearData()
    {
        var usage = await _db.UsageRecords.CountAsync();
        var summaries = await _db.DailySummaries.CountAsync();

        await _db.UsageRecords.ExecuteDeleteAsync();
        await _db.DailySummaries.ExecuteDeleteAsync();

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
