using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// [TASK-MILESTONE-V3] 需求 14：客户端运行日志上传与查看
///
/// - POST /api/logs — 家长端 App 批量上传本机日志（账号级归属，增量按批）
/// - GET  /api/logs — 列表查看：普通家长仅本账号，admin 全部（可按账号/级别/时间筛选）
/// - 保留策略（D6）：最近 7 天，上传/查询时内联清理（按 ReceivedAt）
/// - 脱敏：入库前二次打码（AppLogSanitizer，客户端已打码为第一层）
/// </summary>
[ApiController]
[Route("api/logs")]
[Authorize(Policy = "ParentOrAdmin")]
public class LogsController : ControllerBase
{
    private const int BatchMax = 500;
    private const int RetentionDays = 7;
    private const int MaxMessageLen = 1000;
    private const int MaxTagLen = 64;
    private const int MaxClientLen = 64;

    private readonly AppDbContext _db;
    private readonly ILogger<LogsController> _logger;

    public LogsController(AppDbContext db, ILogger<LogsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/logs — 批量上传日志（批上限 500 条，超过分多批）
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] LogUploadRequest? request)
    {
        if (request?.Logs == null || request.Logs.Count == 0)
            return BadRequest(new { error = "logs 不能为空" });
        if (request.Logs.Count > BatchMax)
            return BadRequest(new { error = $"单次最多 {BatchMax} 条" });

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "登录已过期，请重新登录" });

        // [SEC-P2] 上传限速：每账号每 IP 每小时 30 次（自动 6 小时一次 + 手动补传，防灌库）
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"logs:{userId}:{clientIp}", 30, 3600))
            return StatusCode(429, new { error = "上传过于频繁，请稍后再试" });

        var now = DateTime.UtcNow;
        var client = AppLogSanitizer.Truncate(request.Client, MaxClientLen);
        var entries = new List<AppLogEntry>(request.Logs.Count);
        foreach (var item in request.Logs)
        {
            // [SEC-K9] 字段收敛 + 二次脱敏：级别白名单、tag/client 截断、内容打码后截断
            var message = AppLogSanitizer.Truncate(AppLogSanitizer.MaskSecrets(item.Msg ?? string.Empty), MaxMessageLen);
            if (string.IsNullOrEmpty(message))
                continue; // 空内容条目跳过

            entries.Add(new AppLogEntry
            {
                AccountId = userId.Value,
                Level = NormalizeLevel(item.Level),
                Tag = AppLogSanitizer.Truncate(item.Tag, MaxTagLen) ?? "App",
                Message = message,
                Client = client,
                CreatedAt = ClampClientTime(item.T, now),
                ReceivedAt = now,
            });
        }

        _db.AppLogEntries.AddRange(entries);
        await _db.SaveChangesAsync();

        // 保留策略：最近 7 天（上传时内联清理，低成本维持窗口）
        await CleanupExpiredAsync(now);

        // [SEC-K10] 上传审计（仅条数，不落日志内容）
        await AuditAsync("logs.upload", null,
            $"{{\"count\":{entries.Count},\"client\":\"{client ?? ""}\"}}");

        return Ok(new
        {
            accepted = entries.Count,
            lastT = entries.Count > 0 ? entries[^1].CreatedAt : (DateTime?)null,
        });
    }

    /// <summary>
    /// GET /api/logs — 列表查看（新→旧）
    /// 普通家长：仅本账号；admin：全部 + 可按 accountId/level/时间范围筛选
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? accountId,
        [FromQuery] string? level,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int limit = 200,
        [FromQuery] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);

        var isAdmin = User.IsInRole("admin");
        var query = _db.AppLogEntries.AsQueryable();

        if (!isAdmin)
        {
            var userId = GetUserId();
            if (userId == null)
                return Ok(new { total = 0, limit, offset, items = Array.Empty<object>() });
            query = query.Where(l => l.AccountId == userId.Value);
        }
        else if (accountId is > 0)
        {
            query = query.Where(l => l.AccountId == accountId.Value);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalized = NormalizeLevel(level);
            query = query.Where(l => l.Level == normalized);
        }
        if (DateTime.TryParse(from, out var fromTime))
            query = query.Where(l => l.CreatedAt >= fromTime);
        if (DateTime.TryParse(to, out var toTime))
            query = query.Where(l => l.CreatedAt <= toTime);

        var total = await query.CountAsync();

        var rows = await query
            .OrderByDescending(l => l.ReceivedAt)
            .ThenByDescending(l => l.Id)
            .Skip(offset)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.AccountId,
                l.Level,
                l.Tag,
                l.Message,
                l.Client,
                l.CreatedAt,
                l.ReceivedAt,
            })
            .ToListAsync();

        // admin 视图补账号标识（家长仅本账号，无需联表）
        object[] items;
        if (isAdmin && rows.Count > 0)
        {
            var ids = rows.Select(r => r.AccountId).Distinct().ToList();
            var accounts = await _db.Users
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.Username);
            items = rows.Select(r => new
            {
                r.Id,
                r.AccountId,
                accountEmail = accounts.GetValueOrDefault(r.AccountId, $"#{r.AccountId}"),
                r.Level,
                r.Tag,
                r.Message,
                r.Client,
                r.CreatedAt,
                r.ReceivedAt,
            }).Cast<object>().ToArray();
        }
        else
        {
            items = rows.Cast<object>().ToArray();
        }

        // 保留策略：查询时同样内联清理（账号长期不上传也能维持 7 天窗口）
        await CleanupExpiredAsync(DateTime.UtcNow);

        return Ok(new { total, limit, offset, items });
    }

    // ========== 辅助 ==========

    private int? GetUserId() =>
        int.TryParse(DeviceAccess.GetUserId(User), out var uid) ? uid : null;

    /// <summary>级别白名单归一：D/debug→debug、I/info→info、W/warn→warn、E/error→error，未知→info</summary>
    private static string NormalizeLevel(string? level) =>
        level?.ToLowerInvariant() switch
        {
            "d" or "debug" => "debug",
            "w" or "warn" or "warning" => "warn",
            "e" or "error" or "fatal" => "error",
            _ => "info",
        };

    /// <summary>客户端时间钳制：合法区间 [2020-01-01, now+1d]，否则回落服务端时间</summary>
    private static DateTime ClampClientTime(long? t, DateTime now)
    {
        if (t is not > 0) return now;
        try
        {
            var client = DateTimeOffset.FromUnixTimeMilliseconds(t.Value).UtcDateTime;
            var min = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return client >= min && client <= now.AddDays(1) ? client : now;
        }
        catch (ArgumentOutOfRangeException)
        {
            return now;
        }
    }

    /// <summary>保留最近 7 天（按 ReceivedAt，防客户端时间伪造绕过清理）</summary>
    private async Task CleanupExpiredAsync(DateTime now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        var expired = await _db.AppLogEntries
            .Where(l => l.ReceivedAt < cutoff)
            .ToListAsync();
        if (expired.Count == 0) return;
        _db.AppLogEntries.RemoveRange(expired);
        await _db.SaveChangesAsync();
        _logger.LogInformation("[Logs] 已清理过期日志 {Count} 条（保留 {Days} 天）", expired.Count, RetentionDays);
    }

    /// <summary>[SEC-K10] 审计日志落库</summary>
    private async Task AuditAsync(string action, string? targetType, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = action,
            TargetType = targetType,
            TargetId = null,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }
}

// ========== DTOs ==========

/// <summary>
/// 日志批量上传请求（家长端 App → Web）
/// </summary>
public class LogUploadRequest
{
    /// <summary>客户端标识（机型/系统版本），可选</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? Client { get; set; }

    /// <summary>日志条目（升序，批上限 500）</summary>
    public List<LogItemRequest>? Logs { get; set; }
}

/// <summary>
/// 单条日志
/// </summary>
public class LogItemRequest
{
    /// <summary>客户端时间戳（epoch 毫秒，服务端钳制）</summary>
    public long? T { get; set; }

    /// <summary>级别：D/I/W/E 或 debug/info/warn/error</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(16)]
    public string? Level { get; set; }

    /// <summary>模块 tag</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? Tag { get; set; }

    /// <summary>日志内容（入库前脱敏 + 截断）</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? Msg { get; set; }
}
