using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 管理后端 API — 账号管理 / 审计日志 / 系统设置 / 数据管理（仅管理员）
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ISqlCipherService _sqlCipher;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AppDbContext db,
        IPasswordHasher hasher,
        ISqlCipherService sqlCipher,
        ILogger<AdminController> logger)
    {
        _db = db;
        _hasher = hasher;
        _sqlCipher = sqlCipher;
        _logger = logger;
    }

    // ==================== 账号管理 ====================

    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts()
    {
        var accounts = await _db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Role,
                u.Email,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt,
            })
            .ToListAsync();
        return Ok(accounts);
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] AccountCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "用户名和密码不能为空" });
        // [SEC-P2] 密码策略（红线 R4.2）
        var policyError = PasswordPolicy.Validate(request.Password);
        if (policyError != null)
            return BadRequest(new { error = policyError });
        if (await _db.Users.AnyAsync(u => u.Username == request.Username))
            return BadRequest(new { error = "用户名已存在" });

        var (hash, salt) = _hasher.HashPassword(request.Password);
        var user = new User
        {
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName ?? request.Username.Trim(),
            Role = request.Role == "admin" ? "admin" : "parent",
            Email = request.Email,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await AuditAsync("admin.account.create", "User", user.Id, $"{{\"username\":\"{user.Username}\"}}");
        _logger.LogInformation("[Admin] 账号已创建: {Username}", user.Username);

        return Ok(new
        {
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role,
            user.Email,
            user.CreatedAt,
        });
    }

    [HttpPut("accounts/{id:int}")]
    public async Task<IActionResult> UpdateAccount(int id, [FromBody] AccountUpdateRequest request)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { error = "账号不存在" });

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            if (await _db.Users.AnyAsync(u => u.Username == request.Username && u.Id != id))
                return BadRequest(new { error = "用户名已存在" });
            user.Username = request.Username.Trim();
        }
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
            user.DisplayName = request.DisplayName;
        if (!string.IsNullOrEmpty(request.Role))
            user.Role = request.Role == "admin" ? "admin" : "parent";
        if (!string.IsNullOrEmpty(request.Email))
            user.Email = request.Email;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("admin.account.update", "User", id, $"{{\"username\":\"{user.Username}\"}}");
        return Ok(new { message = "账号已更新" });
    }

    [HttpDelete("accounts/{id:int}")]
    public async Task<IActionResult> DeleteAccount(int id)
    {
        if (id == GetUserId())
            return BadRequest(new { error = "不能删除当前登录账号" });

        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { error = "账号不存在" });

        // [SEC-P1] 级联清理（防 Restrict FK 失败 / 遗留孤儿数据）：
        // 1) 公告 CreatedBy 外键 Restrict → 转移给当前管理员（公告历史保留，审计可追溯）
        // 2) 设备 owner_user_id 无外键 → 置空（设备待新家长重新认领，重绑有归属校验）
        // 3) RefreshTokens / AuditLogs 由 FK Cascade / SetNull 自动处理
        var adminId = GetUserId()!.Value;
        var userAnnouncements = await _db.Announcements
            .Where(a => a.CreatedBy == id)
            .ToListAsync();
        foreach (var a in userAnnouncements)
        {
            a.CreatedBy = adminId;
            a.UpdatedAt = DateTime.UtcNow;
        }

        var ownedDevices = await _db.Devices
            .Where(d => d.OwnerUserId == id.ToString())
            .ToListAsync();
        foreach (var d in ownedDevices)
        {
            d.OwnerUserId = null;
            d.UpdatedAt = DateTime.UtcNow;
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        await AuditAsync("admin.account.delete", "User", id,
            $"{{\"username\":\"{user.Username}\",\"announcementsTransferred\":{userAnnouncements.Count},\"devicesReleased\":{ownedDevices.Count}}}");
        return Ok(new { message = "账号已删除" });
    }

    [HttpPost("accounts/{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest? request)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { error = "账号不存在" });

        var newPassword = string.IsNullOrWhiteSpace(request?.NewPassword)
            ? GeneratePassword()
            : request!.NewPassword;
        // [SEC-P2] 密码策略（红线 R4.2）
        var policyError = PasswordPolicy.Validate(newPassword);
        if (policyError != null)
            return BadRequest(new { error = policyError });

        var (hash, salt) = _hasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        // [SEC-P1] 管理员重置的口令视为临时口令，强制用户下次登录后修改（红线 R4.2）
        user.MustChangePassword = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("admin.account.reset-password", "User", id, $"{{\"username\":\"{user.Username}\"}}");
        return Ok(new { message = "密码已重置", newPassword = string.IsNullOrWhiteSpace(request?.NewPassword) ? newPassword : null });
    }

    // ==================== 审计日志 ====================

    [HttpGet("audit-logs")]
    public async Task<IActionResult> ListAuditLogs(
        string? action = null, string? username = null,
        DateTime? from = null, DateTime? to = null,
        int page = 1, int pageSize = 20)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action))
            query = query.Where(l => l.Action.Contains(action));
        if (!string.IsNullOrEmpty(username))
            query = query.Where(l => l.User != null && l.User.Username.Contains(username));
        if (from.HasValue)
            query = query.Where(l => l.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.CreatedAt <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                username = l.User != null ? l.User.Username : "system",
                action = l.Action,
                resource = l.TargetType,
                detail = l.Detail,
                ipAddress = l.IpAddress,
                timestamp = l.CreatedAt,
            })
            .ToListAsync();

        return Ok(new { total, items });
    }

    [HttpGet("audit-logs/export")]
    public async Task<IActionResult> ExportAuditLogs(string format = "json")
    {
        var items = await _db.AuditLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(2000)
            .Select(l => new
            {
                l.Id,
                username = l.User != null ? l.User.Username : "system",
                action = l.Action,
                resource = l.TargetType,
                detail = l.Detail,
                ipAddress = l.IpAddress,
                timestamp = l.CreatedAt,
            })
            .ToListAsync();

        format = (format ?? "json").ToLowerInvariant();
        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,username,action,resource,detail,ip,timestamp");
            foreach (var l in items)
                sb.AppendLine($"{l.Id},\"{l.username}\",\"{l.action}\",\"{l.resource}\",\"{l.detail}\",\"{l.ipAddress}\",\"{l.timestamp:o}\"");
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "audit-logs.csv");
        }

        return File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })),
            "application/json; charset=utf-8", "audit-logs.json");
    }

    // ==================== 系统设置 ====================

    [HttpGet("system")]
    public async Task<IActionResult> GetSystemConfig()
    {
        var configs = await _db.SystemConfigs.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value);
        return Ok(new
        {
            webPort = ParseInt(configs, "web_port", 5000),
            p2pPort = ParseInt(configs, "p2p_port", 9527),
            bindAddress = configs.GetValueOrDefault("bind_address", "127.0.0.1"),
            httpsEnabled = ParseBool(configs, "https_enabled", false),
            backupDir = configs.GetValueOrDefault("backup_dir", "backups"),
            dataRetentionDays = ParseInt(configs, "data_retention_days", 90),
            maxLoginAttempts = ParseInt(configs, "max_login_attempts", 5),
            sessionTimeoutMinutes = ParseInt(configs, "session_timeout_minutes", 60),
        });
    }

    [HttpPut("system")]
    public async Task<IActionResult> SaveSystemConfig([FromBody] SystemConfigSaveRequest request)
    {
        // [SEC-P2] 值域校验：非法配置值直接拒绝入库
        if (request.WebPort is < 1 or > 65535)
            return BadRequest(new { error = "Web 端口超出范围（1-65535）" });
        if (request.P2pPort is < 1 or > 65535)
            return BadRequest(new { error = "P2P 端口超出范围（1-65535）" });
        if (request.DataRetentionDays is < 1 or > 3650)
            return BadRequest(new { error = "数据保留天数超出范围（1-3650）" });
        if (request.MaxLoginAttempts is < 3 or > 20)
            return BadRequest(new { error = "登录失败上限超出范围（3-20）" });
        if (request.SessionTimeoutMinutes is < 5 or > 1440)
            return BadRequest(new { error = "会话超时超出范围（5-1440 分钟）" });
        if (!string.IsNullOrEmpty(request.BindAddress) && !IsValidHostOrIp(request.BindAddress))
            return BadRequest(new { error = "绑定地址格式无效" });
        if (!string.IsNullOrEmpty(request.BackupDir) &&
            (request.BackupDir.Contains("..") || request.BackupDir.StartsWith('/') ||
             request.BackupDir.StartsWith('\\') || request.BackupDir.Contains(':') || request.BackupDir.Length > 128))
            return BadRequest(new { error = "备份目录格式无效" });

        await SetConfig("web_port", request.WebPort.ToString());
        await SetConfig("p2p_port", request.P2pPort.ToString());
        if (!string.IsNullOrEmpty(request.BindAddress))
            await SetConfig("bind_address", request.BindAddress.Trim());
        await SetConfig("https_enabled", request.HttpsEnabled.ToString());
        if (!string.IsNullOrEmpty(request.BackupDir))
            await SetConfig("backup_dir", request.BackupDir.Trim());
        await SetConfig("data_retention_days", request.DataRetentionDays.ToString());
        await SetConfig("max_login_attempts", request.MaxLoginAttempts.ToString());
        await SetConfig("session_timeout_minutes", request.SessionTimeoutMinutes.ToString());

        await AuditAsync("admin.system.save", "System", null, null);
        return Ok(new { message = "系统配置已保存" });
    }

    /// <summary>[SEC] 主机名/IP 白名单校验：字母数字/点/连字符/下划线；含冒号时按 IPv6 解析</summary>
    private static bool IsValidHostOrIp(string value)
    {
        var v = value.Trim();
        if (v.Length is < 1 or > 255) return false;
        if (v.Contains(':'))
            return System.Net.IPAddress.TryParse(v.Trim('[', ']'), out _);
        return v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    // ==================== 数据管理 ====================

    [HttpGet("data/status")]
    public async Task<IActionResult> DataStatus()
    {
        var dbPath = _sqlCipher.GetDatabasePath();
        long fileSize = 0;
        if (System.IO.File.Exists(dbPath))
            fileSize = new System.IO.FileInfo(dbPath).Length;

        return Ok(new
        {
            databasePath = dbPath,
            databaseSizeBytes = fileSize,
            deviceCount = await _db.Devices.CountAsync(),
            usageRecordCount = await _db.UsageRecords.CountAsync(),
            summaryCount = await _db.DailySummaries.CountAsync(),
            announcementCount = await _db.Announcements.CountAsync(),
            auditLogCount = await _db.AuditLogs.CountAsync(),
            userCount = await _db.Users.CountAsync(),
            encryption = "SQLCipher",
            encrypted = true,
        });
    }

    [HttpPost("data/backup")]
    public async Task<IActionResult> BackupData()
    {
        // [SEC-P1] 备份剥离凭据字段：口令哈希/盐、设备令牌/配对码/证书指纹不入备份（红线 R4.3）
        var backup = new
        {
            version = "3.0",
            exportedAt = DateTime.UtcNow,
            users = await _db.Users.AsNoTracking()
                .Select(u => new
                {
                    u.Id, u.Username, u.DisplayName, u.Role, u.Email, u.IsActive,
                    u.MustChangePassword, u.CreatedAt, u.UpdatedAt, u.LastLoginAt,
                })
                .ToListAsync(),
            devices = await _db.Devices.AsNoTracking()
                .Select(d => new
                {
                    d.Id, d.DeviceId, d.DeviceName, d.Platform, d.MacAddress, d.IpAddress,
                    d.PairStatus, d.OnlineStatus, d.OwnerUserId, d.AppCategories,
                    d.LastResetOffsetMinutes, d.LastResetDate, d.TodayAdjustedMinutes, d.IsActive,
                })
                .ToListAsync(),
            policies = await _db.Policies.AsNoTracking().ToListAsync(),
            announcements = await _db.Announcements.AsNoTracking().ToListAsync(),
            usageRecords = await _db.UsageRecords.AsNoTracking().ToListAsync(),
            dailySummaries = await _db.DailySummaries.AsNoTracking().ToListAsync(),
        };

        await AuditAsync("admin.data.backup", "Data", null, null);
        return File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true })),
            "application/json; charset=utf-8",
            $"xiaopacai-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpPost("data/restore")]
    public async Task<IActionResult> RestoreData(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "请选择备份文件" });
        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "备份文件过大（上限 10MB）" });
        if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "仅支持 .json 备份文件" });

        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync();
        AdminBackupPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AdminBackupPayload>(json);
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
                if (await _db.Devices.AnyAsync(x => x.DeviceId == d.DeviceId)) continue;
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                restored++;
            }
        }
        await _db.SaveChangesAsync();

        await AuditAsync("admin.data.restore", "Data", null, $"{{\"restored\":{restored}}}");
        return Ok(new { message = "数据已恢复", restored });
    }

    [HttpPost("data/clear")]
    public async Task<IActionResult> ClearData()
    {
        var usage = await _db.UsageRecords.CountAsync();
        var summaries = await _db.DailySummaries.CountAsync();
        await _db.UsageRecords.ExecuteDeleteAsync();
        await _db.DailySummaries.ExecuteDeleteAsync();

        await AuditAsync("admin.data.clear", "Data", null, $"{{\"usageRecords\":{usage},\"summaries\":{summaries}}}");
        return Ok(new { message = "数据已清除", usageRecords = usage, summaries });
    }

    [HttpPost("data/rotate-keys")]
    public async Task<IActionResult> RotateKeys()
    {
        // 在线旋转 SQLCipher 密钥需要对整库重加密，风险高。
        // 此处实现为：验证密钥可用性并记录审计，真正轮换由运维在离线维护模式下执行。
        var dbPath = _sqlCipher.GetDatabasePath();
        var keyOk = await _db.Database.CanConnectAsync();

        await AuditAsync("admin.data.rotate-keys", "Data", null, $"{{\"keyValid\":{keyOk}}}");

        return Ok(new
        {
            message = keyOk
                ? "密钥校验通过。在线重加密需停机维护，请使用离线维护脚本执行轮换。"
                : "数据库密钥校验失败！",
            keyValid = keyOk,
            databasePath = dbPath,
        });
    }

    // ==================== helpers ====================

    private async Task SetConfig(string key, string value)
    {
        var config = await _db.SystemConfigs.FindAsync(key);
        if (config == null)
            _db.SystemConfigs.Add(new SystemConfig { Key = key, Value = value });
        else
        {
            config.Value = value;
            config.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    private static string GeneratePassword()
    {
        // [SEC-P2] 保证含字母与数字（满足密码策略）；其余位随机填充（易读字符集）
        const string letters = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        var random = new byte[8];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(random);
        var sb = new StringBuilder();
        sb.Append(letters[random[0] % letters.Length]);
        sb.Append(digits[random[1] % digits.Length]);
        for (var i = 2; i < random.Length; i++)
            sb.Append(letters[random[i] % letters.Length]);
        return sb.ToString();
    }

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

    private static bool ParseBool(Dictionary<string, string> configs, string key, bool fallback)
        => bool.TryParse(configs.GetValueOrDefault(key), out var v) ? v : fallback;

    private static int ParseInt(Dictionary<string, string> configs, string key, int fallback)
        => int.TryParse(configs.GetValueOrDefault(key), out var v) ? v : fallback;
}

// ==================== DTOs ====================

public class AccountCreateRequest
{
    [Required]
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string Username { get; set; } = string.Empty;

    [Required]
    [System.ComponentModel.DataAnnotations.MaxLength(128)] public string Password { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string? DisplayName { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(16)] public string Role { get; set; } = "parent";
    [System.ComponentModel.DataAnnotations.MaxLength(128)] public string? Email { get; set; }
}

public class AccountUpdateRequest
{
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string? Username { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(64)] public string? DisplayName { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(16)] public string? Role { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(128)] public string? Email { get; set; }
}

public class ResetPasswordRequest
{
    [System.ComponentModel.DataAnnotations.MaxLength(128)] public string? NewPassword { get; set; }
}

public class SystemConfigSaveRequest
{
    public int WebPort { get; set; } = 5000;
    public int P2pPort { get; set; } = 9527;
    public string? BindAddress { get; set; }
    public bool HttpsEnabled { get; set; }
    public string? BackupDir { get; set; }
    public int DataRetentionDays { get; set; } = 90;
    public int MaxLoginAttempts { get; set; } = 5;
    public int SessionTimeoutMinutes { get; set; } = 60;
}

public class AdminBackupPayload
{
    public List<AdminBackupDevice>? Devices { get; set; }
    public List<AdminBackupAnnouncement>? Announcements { get; set; }
}

public class AdminBackupDevice
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
    public string? IpAddress { get; set; }
    public string? CertFingerprint { get; set; }
    public string? PairStatus { get; set; }
    public string? OnlineStatus { get; set; }
}

public class AdminBackupAnnouncement
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Priority { get; set; }
}
