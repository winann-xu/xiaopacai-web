using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 公告管理 API — 新建 / 编辑 / 发布 / 撤回 / 删除（发布与撤回实时 P2P 推送）
/// </summary>
[ApiController]
[Route("api/announcements")]
[Authorize(Policy = "ParentOrAdmin")]
public class AnnouncementsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly P2pMessageHandler _messageHandler;
    private readonly P2pListenerService _p2p;
    private readonly ILogger<AnnouncementsController> _logger;

    public AnnouncementsController(
        AppDbContext db,
        P2pMessageHandler messageHandler,
        P2pListenerService p2p,
        ILogger<AnnouncementsController> logger)
    {
        _db = db;
        _messageHandler = messageHandler;
        _p2p = p2p;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/announcements — 公告列表（新→旧）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await _db.Announcements
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync();

        return Ok(items.Select(ToDto));
    }

    /// <summary>
    /// GET /api/announcements/{id} — 公告详情
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });
        return Ok(ToDto(item));
    }

    /// <summary>
    /// POST /api/announcements — 新建公告（草稿）
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AnnouncementSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "标题和内容不能为空" });

        // [SEC-P1] 定向公告的目标设备必须归属当前用户（红线 R2.1），防向他人设备定向推送
        if (request.TargetDeviceId is > 0)
        {
            var (access, _) = await DeviceAccess.CheckAsync(_db, request.TargetDeviceId.Value, User);
            if (access == DeviceAccessResult.NotFound)
                return NotFound(new { error = "目标设备不存在" });
            if (access == DeviceAccessResult.Forbidden)
                return StatusCode(403, new { error = "无权向该设备推送公告" });
        }

        var item = new Announcement
        {
            Title = request.Title.Trim(),
            Content = request.Content,
            Priority = request.Priority ?? "normal",
            Status = request.Status ?? "draft",
            TargetDeviceId = request.TargetDeviceId,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            CreatedBy = GetUserId() ?? 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Announcements.Add(item);
        await _db.SaveChangesAsync();

        await AuditAsync("announcement.create", "Announcement", item.Id, $"{{\"title\":\"{item.Title}\"}}");

        return Ok(ToDto(item));
    }

    /// <summary>
    /// PUT /api/announcements/{id} — 编辑公告（仅草稿/已撤回可编辑）
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AnnouncementSaveRequest request)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        // [SEC-P1] 仅创建者或管理员可编辑他人公告（红线 R2.1）
        if (!CanManage(item))
            return StatusCode(403, new { error = "无权操作该公告" });

        if (item.Status == "published")
            return BadRequest(new { error = "已发布的公告不可编辑，请先撤回" });

        // [SEC-P1] 定向目标变更时重新校验新设备的归属
        if (request.TargetDeviceId is > 0 && request.TargetDeviceId != item.TargetDeviceId)
        {
            var (access, _) = await DeviceAccess.CheckAsync(_db, request.TargetDeviceId.Value, User);
            if (access == DeviceAccessResult.NotFound)
                return NotFound(new { error = "目标设备不存在" });
            if (access == DeviceAccessResult.Forbidden)
                return StatusCode(403, new { error = "无权向该设备推送公告" });
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
            item.Title = request.Title.Trim();
        if (!string.IsNullOrWhiteSpace(request.Content))
            item.Content = request.Content;
        item.Priority = request.Priority ?? item.Priority;
        item.TargetDeviceId = request.TargetDeviceId ?? item.TargetDeviceId;
        item.ValidFrom = request.ValidFrom ?? item.ValidFrom;
        item.ValidUntil = request.ValidUntil ?? item.ValidUntil;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("announcement.update", "Announcement", item.Id, $"{{\"title\":\"{item.Title}\"}}");
        return Ok(ToDto(item));
    }

    /// <summary>
    /// DELETE /api/announcements/{id} — 删除公告
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        // [SEC-P1] 仅创建者或管理员可删除（红线 R2.1）
        if (!CanManage(item))
            return StatusCode(403, new { error = "无权操作该公告" });

        _db.Announcements.Remove(item);
        await _db.SaveChangesAsync();

        await AuditAsync("announcement.delete", "Announcement", id, $"{{\"title\":\"{item.Title}\"}}");
        return Ok(new { message = "公告已删除" });
    }

    /// <summary>
    /// POST /api/announcements/{id}/publish — 发布并实时推送儿童端
    /// [TASK-PRELAUNCH-P3] 发布时递增版本并计算内容哈希（终端去重依据，见 docs/adr/0004）
    /// </summary>
    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        // [SEC-P1] 发布会向全量儿童端推送，仅创建者或管理员可操作（红线 R2.1）
        if (!CanManage(item))
            return StatusCode(403, new { error = "无权操作该公告" });

        item.Status = "published";
        item.PublishedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        // [TASK-PRELAUNCH-P3] 版本递增 + 内容哈希（撤回后重新发布视为新代数；内容未变哈希不变）
        item.Version++;
        item.ContentHash = P2pMessageHandler.ComputeContentHash(item.Title, item.Content, item.Priority);
        await _db.SaveChangesAsync();

        await _messageHandler.PushAnnouncement(item, "publish", _p2p);
        await AuditAsync("announcement.publish", "Announcement", item.Id,
            $"{{\"title\":\"{item.Title}\",\"version\":{item.Version}}}");

        _logger.LogInformation("[Announcements] 公告已发布并推送 v{Version}: {Title}", item.Version, item.Title);
        return Ok(ToDto(item));
    }

    /// <summary>
    /// POST /api/announcements/{id}/revoke — 撤回并通知儿童端
    /// </summary>
    [HttpPost("{id:int}/revoke")]
    public async Task<IActionResult> Revoke(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        // [SEC-P1] 仅创建者或管理员可撤回（红线 R2.1）
        if (!CanManage(item))
            return StatusCode(403, new { error = "无权操作该公告" });

        item.Status = "revoked";
        item.RevokedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _messageHandler.PushAnnouncement(item, "revoke", _p2p);
        await AuditAsync("announcement.revoke", "Announcement", item.Id, $"{{\"title\":\"{item.Title}\"}}");

        return Ok(ToDto(item));
    }

    // ========== helpers ==========

    /// <summary>
    /// [TASK-PRELAUNCH-P3] GET /api/announcements/{id}/deliveries — 送达与回执明细
    /// 按设备返回：推送次数/最近推送/终端显示/确认时间（见 docs/adr/0004）
    /// </summary>
    [HttpGet("{id:int}/deliveries")]
    public async Task<IActionResult> Deliveries(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        var devices = await _db.Devices.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.DeviceName);

        var rows = await _db.AnnouncementDeliveries.AsNoTracking()
            .Where(d => d.AnnouncementId == id)
            .ToListAsync();

        return Ok(new
        {
            announcementId = id,
            version = item.Version,
            contentHash = item.ContentHash,
            deliveries = rows
                .OrderByDescending(r => r.UpdatedAt)
                .Select(r => new
                {
                    deviceId = r.DeviceId,
                    deviceName = devices.TryGetValue(r.DeviceId, out var name) ? name : $"设备#{r.DeviceId}",
                    pushCount = r.PushCount,
                    lastPushedAt = r.LastPushedAt,
                    displayedAt = r.DisplayedAt,
                    acknowledgedAt = r.AcknowledgedAt,
                })
                .ToList(),
        });
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] GET /api/announcements/urgent-stats — 紧急公告未确认统计
    /// 口径：已发布紧急公告 ×（已配对激活设备中未确认数），供仪表盘“未确认紧急公告”卡片
    /// </summary>
    [HttpGet("urgent-stats")]
    public async Task<IActionResult> UrgentStats()
    {
        var urgentIds = await _db.Announcements.AsNoTracking()
            .Where(a => a.Priority == "urgent" && a.Status == "published")
            .Select(a => a.Id)
            .ToListAsync();

        var activeDeviceIds = await _db.Devices.AsNoTracking()
            .Where(d => d.PairStatus == "paired" && d.IsActive)
            .Select(d => d.Id)
            .ToListAsync();

        var acked = await _db.AnnouncementDeliveries.AsNoTracking()
            .Where(d => urgentIds.Contains(d.AnnouncementId) && d.AcknowledgedAt != null)
            .Select(d => new { d.AnnouncementId, d.DeviceId })
            .ToListAsync();

        // 未确认数 = 紧急公告数 × 激活设备数 − 已确认（公告×设备）组合数
        var totalPairs = urgentIds.Count * activeDeviceIds.Count;
        var ackedPairs = acked.Count(p => activeDeviceIds.Contains(p.DeviceId));
        var unacknowledged = Math.Max(0, totalPairs - ackedPairs);

        return Ok(new
        {
            publishedUrgent = urgentIds.Count,
            activeDevices = activeDeviceIds.Count,
            unacknowledged,
        });
    }

    /// <summary>
    /// [SEC-P1] 公告归属校验：仅创建者或管理员可管理（改/删/发布/撤回）
    /// </summary>
    private bool CanManage(Announcement item)
    {
        if (User.IsInRole("admin")) return true;
        var userId = GetUserId();
        return userId != null && item.CreatedBy == userId;
    }

    private static object ToDto(Announcement a)
    {
        return new
        {
            id = a.Id,
            title = a.Title,
            content = a.Content,
            priority = a.Priority,
            status = a.Status,
            targetDeviceId = a.TargetDeviceId,
            validFrom = a.ValidFrom,
            validUntil = a.ValidUntil,
            createdAt = a.CreatedAt,
            publishedAt = a.PublishedAt,
            revokedAt = a.RevokedAt,
            // [TASK-PRELAUNCH-P3] 去重字段透出（发布代数/内容哈希）
            version = a.Version,
            contentHash = a.ContentHash,
        };
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
/// 公告保存请求
/// </summary>
public class AnnouncementSaveRequest
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Priority { get; set; }
    public string? Status { get; set; }
    public int? TargetDeviceId { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
}
