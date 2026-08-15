using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护失守事件 + 健康度
///
/// - POST /api/guard-events — 家长端 App 批量上传守护事件（1-100 条，按设备归属账号隔离）
/// - GET  /api/guard-events — 失守/恢复/健康快照列表（按 ReceivedAt 倒序；家长仅本账号设备，admin 全量）
/// - GET  /api/guard-events/health — 最近一条含 health 的健康度快照（无数据返回 health/updatedAt 双 null）
///
/// 鉴权/审计/限速风格与 LogsController 一致；账号隔离遵循 [SEC-K2]：
/// 家长仅能操作自己绑定（OwnerUserId 匹配）的设备，admin 不受归属限制（设备须存在）。
/// </summary>
[ApiController]
[Route("api/guard-events")]
[Authorize(Policy = "ParentOrAdmin")]
public class GuardEventsController : ControllerBase
{
    private const int BatchMax = 100;
    private const int MaxDeviceIdLen = 128;
    private const int MaxEventTypeLen = 64;
    private const int MaxReasonLen = 128;
    private const int MaxHealthJsonLen = 64 * 1024;
    private const int MaxListLimit = 200;

    private readonly AppDbContext _db;
    private readonly ILogger<GuardEventsController> _logger;

    public GuardEventsController(AppDbContext db, ILogger<GuardEventsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/guard-events — 批量上传守护事件（批上限 100 条）
    /// 校验：events 1-100 条；event 必填；startTs/endTs/durationSec 可选数字（负值拒绝）；
    /// health 可选 JSON 对象（字符串存储，非对象/超限忽略）。
    /// [SEC-K2] 设备必须存在（404）且属于当前账号（403）；admin 仅要求设备存在。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] GuardEventUploadRequest? request)
    {
        if (request?.Events == null || request.Events.Count == 0)
            return BadRequest(new { error = "events 不能为空" });
        if (request.Events.Count > BatchMax)
            return BadRequest(new { error = $"单次最多 {BatchMax} 条" });

        var deviceId = request.DeviceId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });
        if (deviceId.Length > MaxDeviceIdLen)
            return BadRequest(new { error = "deviceId 过长" });

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "登录已过期，请重新登录" });

        // [SEC-P2] 上传限速：每账号每 IP 每小时 60 次（健康快照 + 失守事件批量上报）
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"guard-events:{userId}:{clientIp}", 60, 3600))
            return StatusCode(429, new { error = "上传过于频繁，请稍后再试" });

        // [SEC-K2] 设备归属校验：不存在 404；非本账号家长 403；admin 仅要求存在
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });
        if (!User.IsInRole("admin") && device.OwnerUserId != userId.Value.ToString())
            return StatusCode(403, new { error = "无权操作该设备" });

        var now = DateTime.UtcNow;
        var entries = new List<GuardEvent>(request.Events.Count);
        foreach (var item in request.Events)
        {
            // [SEC-K9] 字段收敛：event 必填；时间戳/时长为可选数字（负值拒绝）；文本截断
            var eventType = item.Event?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(eventType) || eventType.Length > MaxEventTypeLen)
                return BadRequest(new { error = "event 字段必须存在且长度合法" });
            if (item.StartTs is < 0 || item.EndTs is < 0 || item.DurationSec is < 0)
                return BadRequest(new { error = "时间戳/时长不能为负数" });

            entries.Add(new GuardEvent
            {
                DeviceId = deviceId,
                EventType = eventType,
                StartedAt = item.StartTs,
                EndedAt = item.EndTs,
                DurationSeconds = item.DurationSec,
                Reason = Truncate(item.Reason, MaxReasonLen),
                RestoredReason = Truncate(item.RestoredReason, MaxReasonLen),
                WasEnforcing = item.WasEnforcing,
                HealthJson = SerializeHealth(item.Health, MaxHealthJsonLen),
                ReceivedAt = now,
            });
        }

        _db.GuardEvents.AddRange(entries);
        await _db.SaveChangesAsync();

        // [SEC-K10] 上传审计（仅条数，不落事件内容）
        await AuditAsync("guard-events.upload", null,
            $"{{\"deviceId\":\"{deviceId}\",\"count\":{entries.Count}}}");

        _logger.LogInformation("[GuardEvents] 已接收 {Count} 条守护事件 deviceId={DeviceId}", entries.Count, deviceId);
        return Ok(new { accepted = entries.Count });
    }

    /// <summary>
    /// GET /api/guard-events?deviceId=&amp;limit=50 — 守护事件列表（按 ReceivedAt 倒序）
    /// 家长：deviceId 必填且须为本账号设备（强制隔离）；admin：deviceId 可选（无则全量）。
    /// 响应 healthJson 还原为 JSON 对象（无则为 null）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? deviceId, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var isAdmin = User.IsInRole("admin");
        var query = _db.GuardEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var targetId = deviceId.Trim();
            // [SEC-K2] 设备归属校验：不存在 404；非本账号家长 403
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == targetId);
            if (device == null)
                return NotFound(new { error = "设备不存在" });
            var currentUserId = GetUserId()?.ToString();
            if (!isAdmin && (currentUserId == null || device.OwnerUserId != currentUserId))
                return StatusCode(403, new { error = "无权访问该设备" });

            query = query.Where(g => g.DeviceId == targetId);
        }
        else if (!isAdmin)
        {
            // 家长未指定设备：强制限定本账号名下设备的守卫事件（杜绝跨账号数据泄露）
            var currentUserId = GetUserId()?.ToString();
            if (currentUserId == null)
                return Ok(new { events = Array.Empty<object>() });
            var ownedDeviceIds = _db.Devices
                .Where(d => d.OwnerUserId == currentUserId)
                .Select(d => d.DeviceId);
            query = query.Where(g => ownedDeviceIds.Contains(g.DeviceId));
        }

        var rows = await query
            .OrderByDescending(g => g.ReceivedAt)
            .ThenByDescending(g => g.Id)
            .Take(limit)
            .Select(g => new
            {
                id = g.Id,
                deviceId = g.DeviceId,
                eventType = g.EventType,
                startedAt = g.StartedAt,
                endedAt = g.EndedAt,
                durationSeconds = g.DurationSeconds,
                reason = g.Reason,
                restoredReason = g.RestoredReason,
                wasEnforcing = g.WasEnforcing,
                healthJson = g.HealthJson,
                receivedAt = g.ReceivedAt,
            })
            .ToListAsync();

        var events = rows.Select(r => new
        {
            r.id,
            r.deviceId,
            r.eventType,
            r.startedAt,
            r.endedAt,
            r.durationSeconds,
            r.reason,
            r.restoredReason,
            r.wasEnforcing,
            healthJson = ParseHealth(r.healthJson),
            r.receivedAt,
        }).ToList();

        return Ok(new { events });
    }

    /// <summary>
    /// GET /api/guard-events/health?deviceId=xxx — 最近一条含 health 的健康度快照
    /// 无数据返回 { health: null, updatedAt: null }
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> LatestHealth([FromQuery] string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        var targetId = deviceId.Trim();
        // [SEC-K2] 设备归属校验：不存在 404；非本账号家长 403
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == targetId);
        if (device == null)
            return NotFound(new { error = "设备不存在" });
        var currentUserId = GetUserId()?.ToString();
        if (!User.IsInRole("admin") && (currentUserId == null || device.OwnerUserId != currentUserId))
            return StatusCode(403, new { error = "无权访问该设备" });

        var latest = await _db.GuardEvents
            .Where(g => g.DeviceId == targetId && g.HealthJson != null && g.HealthJson != "")
            .OrderByDescending(g => g.ReceivedAt)
            .ThenByDescending(g => g.Id)
            .FirstOrDefaultAsync();

        if (latest == null)
            return Ok(new { health = (object?)null, updatedAt = (string?)null });

        return Ok(new { health = ParseHealth(latest.HealthJson), updatedAt = latest.ReceivedAt.ToString("O") });
    }

    // ========== 辅助 ==========

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;

    /// <summary>健康 JSON 序列化（字符串存储；非对象/超限返回 null，不影响整批入库）</summary>
    private static string? SerializeHealth(JsonElement? health, int maxLen)
    {
        if (health == null || health.Value.ValueKind != JsonValueKind.Object)
            return null;
        var json = JsonSerializer.Serialize(health.Value);
        return json.Length > maxLen ? null : json;
    }

    /// <summary>健康 JSON 解析（响应时还原为对象；损坏数据返回 null）</summary>
    private static object? ParseHealth(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<object>(json);
        }
        catch (JsonException)
        {
            return null;
        }
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
/// 守护事件批量上传请求（家长端 App → Web）
/// </summary>
public class GuardEventUploadRequest
{
    /// <summary>儿童端设备 deviceId</summary>
    public string? DeviceId { get; set; }

    /// <summary>守护事件（1-100 条）</summary>
    public List<GuardEventItemRequest>? Events { get; set; }
}

/// <summary>
/// 单条守护事件
/// </summary>
public class GuardEventItemRequest
{
    /// <summary>事件类型：guard_down | guard_restored | health_snapshot</summary>
    public string? Event { get; set; }

    /// <summary>事件开始时间（epoch 秒，可选）</summary>
    public long? StartTs { get; set; }

    /// <summary>事件结束时间（epoch 秒，可选）</summary>
    public long? EndTs { get; set; }

    /// <summary>失守时长（秒，可选）</summary>
    public long? DurationSec { get; set; }

    /// <summary>失守原因（可选）：process_killed | swipe_killed | accessibility_disabled | ...</summary>
    public string? Reason { get; set; }

    /// <summary>恢复方式（可选）：auto_recovered | swipe_recovery | accessibility_reenabled | ...</summary>
    public string? RestoredReason { get; set; }

    /// <summary>事件发生时守护是否仍处于强制拦截状态</summary>
    public bool WasEnforcing { get; set; }

    /// <summary>健康度快照（可选 JSON 对象，字符串存储）</summary>
    public JsonElement? Health { get; set; }
}
