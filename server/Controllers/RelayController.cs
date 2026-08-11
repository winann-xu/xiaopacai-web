using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 云端中继 REST API（OPT12 需求 3）
///
/// 管理端查看中继会话（在线中继设备）。
/// relay_sessions 记录由 P2pMessageHandler 在握手（TASK-OPT-12-P4-DEEPEN：握手写入 / 断线更新）时维护；
/// usage_report / announcement_ack 的中继转发由 P2pMessageHandler.RelayMessageToParent 完成。
/// </summary>
[ApiController]
[Route("api/relay")]
[Authorize(Policy = "AdminOnly")]
public class RelayController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<RelayController> _logger;

    public RelayController(AppDbContext db, ILogger<RelayController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/relay/sessions — 管理端查看中继会话列表
    /// 查询参数：status（connected | disconnected，默认全部）、role（child | parent，可选）、limit（默认 50，上限 200）
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] string? status,
        [FromQuery] string? role,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.RelaySessions.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(s => s.Status == status);

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(s => s.Role == role);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(s => s.ConnectedAt)
            .Take(limit)
            .Select(s => new
            {
                s.Id,
                s.DeviceId,
                s.Role,
                s.UserId,
                s.IpAddress,
                s.Status,
                s.ConnectedAt,
                s.DisconnectedAt,
            })
            .ToListAsync();

        // 在线会话数（管理端仪表盘用）
        var onlineCount = await _db.RelaySessions.CountAsync(s => s.Status == "connected");

        return Ok(new
        {
            total,
            onlineCount,
            limit,
            items,
        });
    }
}
