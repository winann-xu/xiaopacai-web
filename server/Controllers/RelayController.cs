using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 云端中继 REST API（OPT12 需求 3）
///
/// 管理端查看中继会话（在线中继设备）+ 家长端/管理端注册中继。
/// relay_sessions 记录由 P2pMessageHandler 在握手（TASK-OPT-12-P4-DEEPEN：握手写入 / 断线更新）时维护；
/// usage_report / announcement_ack 的中继转发由 P2pMessageHandler.RelayMessageToParent 完成。
/// </summary>
[ApiController]
[Route("api/relay")]
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
    /// POST /api/relay/register — 家长端/管理端注册中继会话（OPT12 需求 3）
    ///
    /// 家长端 APP 连接 Web 3.0 中继前调用，注册自身为 relay_sessions role=parent。
    /// 如果携带 pairingCode，则同时将设备与当前家长账号绑定（写入 devices.owner_user_id）。
    ///
    /// 鉴权：ParentOrAdmin（JWT Bearer Token 需包含 admin 或 parent 角色）。
    /// </summary>
    [HttpPost("register")]
    [Authorize(Policy = "ParentOrAdmin")]
    public async Task<IActionResult> Register([FromBody] RelayRegisterRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // 限速：每个 IP 每分钟最多 10 次注册
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"relay-register:{clientIp}", 10, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        // [SEC-K2][SEC-K7] 指纹必填且格式校验：64 位小写十六进制（SHA-256），
        // 它是 P2P 握手时与 TLS 对端证书比对的身份锚点，缺失/格式错误直接拒绝
        var fingerprint = request.Fingerprint?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(fingerprint) ||
            fingerprint.Length != 64 || !fingerprint.All(c => Uri.IsHexDigit(c)))
        {
            return BadRequest(new { error = "客户端证书指纹缺失或格式错误（需 64 位十六进制）" });
        }

        var userId = GetUserId();
        var now = DateTime.UtcNow;

        // 1. 如果提供了配对码，查找对应设备并绑定 owner_user_id
        if (!string.IsNullOrWhiteSpace(request.PairingCode) && userId != null)
        {
            var pairingInfo = await _db.PairingInfos
                .Where(p => p.PairCode == request.PairingCode)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (pairingInfo?.DeviceId is > 0)
            {
                var device = await _db.Devices.FindAsync(pairingInfo.DeviceId.Value);
                if (device != null && string.IsNullOrEmpty(device.OwnerUserId))
                {
                    device.OwnerUserId = userId.ToString();
                    device.UpdatedAt = now;
                    _logger.LogInformation("[Relay] 设备 {DeviceId} 绑定家长 userId={UserId}",
                        device.DeviceId, userId);
                }
            }
        }

        // 2. 查找是否已有该设备的中继会话（合并而非创建重复记录）
        var existingSession = await _db.RelaySessions
            .Where(s => s.DeviceId == request.DeviceId && s.Role == request.Role)
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        // [SEC-K2] 签发会话令牌：家长端后续 P2P 握手凭据（每次注册轮换，防止冒充家长端接收儿童数据，红线 R2.3）
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        if (existingSession != null)
        {
            // 更新已有会话
            existingSession.Status = "connected";
            existingSession.ConnectedAt = now;
            existingSession.DisconnectedAt = null;
            existingSession.IpAddress = clientIp;
            existingSession.SessionToken = sessionToken;
            existingSession.Fingerprint = fingerprint;
            if (userId != null && existingSession.Role == "parent")
                existingSession.UserId = userId.Value;
            _logger.LogInformation("[Relay] 更新中继会话: device={DeviceId}, role={Role}, id={Id}",
                request.DeviceId, request.Role, existingSession.Id);
        }
        else
        {
            // 新建中继会话
            _db.RelaySessions.Add(new RelaySession
            {
                DeviceId = request.DeviceId,
                Role = request.Role,
                UserId = (request.Role == "parent" && userId != null) ? userId.Value : null,
                IpAddress = clientIp,
                Status = "connected",
                ConnectedAt = now,
                SessionToken = sessionToken,
                Fingerprint = fingerprint,
            });
            _logger.LogInformation("[Relay] 新建中继会话: device={DeviceId}, role={Role}, userId={UserId}",
                request.DeviceId, request.Role, userId);
        }

        await _db.SaveChangesAsync();

        // 3. 返回注册结果（含绑定的设备信息）
        int? boundDeviceId = null;
        if (!string.IsNullOrWhiteSpace(request.PairingCode) && userId != null)
        {
            var device = await _db.Devices
                .FirstOrDefaultAsync(d => d.OwnerUserId == userId.ToString() && d.PairStatus == "paired");
            if (device != null)
                boundDeviceId = device.Id;
        }

        // [SEC-K10] 中继注册安全事件审计（只记设备/角色/绑定结果，绝不记录 sessionToken）
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "relay.register",
            TargetType = "RelaySession",
            TargetId = (request.Role == "parent" && userId != null) ? userId.Value : null,
            Detail = $"{{\"deviceId\":\"{request.DeviceId}\",\"role\":\"{request.Role}\",\"boundDeviceId\":{(boundDeviceId?.ToString() ?? "null")}}}",
            IpAddress = clientIp,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // [SEC-K2] sessionToken 只在此响应中出现一次（家长端持久化保存），服务端仅存于 relay_sessions，
        // 不写入日志、不参与列表接口返回，防止令牌泄露（红线 R8.3）
        return Ok(new
        {
            deviceId = request.DeviceId,
            role = request.Role,
            status = "connected",
            connectedAt = now,
            boundDeviceId,
            sessionToken,
            message = "中继注册成功",
        });
    }

    /// <summary>
    /// GET /api/relay/sessions — 管理端查看中继会话列表
    /// 查询参数：status（connected | disconnected，默认全部）、role（child | parent，可选）、limit（默认 50，上限 200）
    /// </summary>
    [HttpGet("sessions")]
    [Authorize(Policy = "AdminOnly")]
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

    // ========== 辅助 ==========

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

// ========== DTO ==========

/// <summary>
/// 中继注册请求（家长端 APP → Web 3.0 中继）
/// </summary>
public class RelayRegisterRequest
{
    /// <summary>设备唯一标识（家长端用 "parent-" + ANDROID_ID 前 8 位）</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>角色：parent（家长端）| child（儿童端）</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "parent";

    /// <summary>TLS 证书 SHA-256 指纹</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    /// <summary>配对码（6 位，用于绑定对应儿童设备）</summary>
    [JsonPropertyName("pairingCode")]
    public string? PairingCode { get; set; }

    /// <summary>家长端 P2P 监听端口（默认 9527）</summary>
    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; } = 9527;
}
