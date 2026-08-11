using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;

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

        if (item.Status == "published")
            return BadRequest(new { error = "已发布的公告不可编辑，请先撤回" });

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

        _db.Announcements.Remove(item);
        await _db.SaveChangesAsync();

        await AuditAsync("announcement.delete", "Announcement", id, $"{{\"title\":\"{item.Title}\"}}");
        return Ok(new { message = "公告已删除" });
    }

    /// <summary>
    /// POST /api/announcements/{id}/publish — 发布并实时推送儿童端
    /// </summary>
    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id)
    {
        var item = await _db.Announcements.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "公告不存在" });

        item.Status = "published";
        item.PublishedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _messageHandler.PushAnnouncement(item, "publish", _p2p);
        await AuditAsync("announcement.publish", "Announcement", item.Id, $"{{\"title\":\"{item.Title}\"}}");

        _logger.LogInformation("[Announcements] 公告已发布并推送: {Title}", item.Title);
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

        item.Status = "revoked";
        item.RevokedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _messageHandler.PushAnnouncement(item, "revoke", _p2p);
        await AuditAsync("announcement.revoke", "Announcement", item.Id, $"{{\"title\":\"{item.Title}\"}}");

        return Ok(ToDto(item));
    }

    // ========== helpers ==========

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
