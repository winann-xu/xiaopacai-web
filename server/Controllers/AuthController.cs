using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 认证与鉴权 API
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly TicketStore _tickets;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, IPasswordHasher hasher, IJwtService jwt, TicketStore tickets, ILogger<AuthController> logger)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/login — 用户登录
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // [TASK-OPT-12-P4-DEEPEN] 登录失败限速：5 次/小时，按用户名 + IP 双维度封锁
        const int maxLoginFailures = 5;
        const int loginWindowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

        if (RequestRateLimiter.IsBlocked($"login:user:{request.Username}", maxLoginFailures, loginWindowSeconds) ||
            RequestRateLimiter.IsBlocked($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds))
        {
            _logger.LogWarning("[Auth] 登录被限速: {U} @ {Ip}", request.Username, clientIp);
            return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
        }

        // 查找用户
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);

        if (user == null)
        {
            _logger.LogWarning("[Auth] 登录失败 — 用户不存在: {U}", request.Username);
            await AuditAsync("login_failed", null, null, null, null,
                $"{{\"username\":\"{request.Username}\",\"reason\":\"user_not_found\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"login:user:{request.Username}", maxLoginFailures, loginWindowSeconds) |
                            RequestRateLimiter.RecordFailure($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
            return Unauthorized(new { error = "用户名或密码错误" });
        }

        // 验证密码
        if (!_hasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("[Auth] 登录失败 — 密码错误: {U}", request.Username);
            await AuditAsync("login_failed", user.Id, null, null, null,
                $"{{\"username\":\"{user.Username}\",\"reason\":\"password_wrong\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"login:user:{request.Username}", maxLoginFailures, loginWindowSeconds) |
                            RequestRateLimiter.RecordFailure($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
            return Unauthorized(new { error = "用户名或密码错误" });
        }

        // [TASK-OPT-12-P4-DEEPEN] 登录成功释放失败计数
        RequestRateLimiter.Clear($"login:user:{request.Username}");
        RequestRateLimiter.Clear($"login:ip:{clientIp}");

        // 签发 Token
        var (accessToken, refreshToken, accessExpiry, refreshExpiry) =
            _jwt.GenerateTokens(user.Id, user.Username, user.Role);

        // 存储 Refresh Token
        await _jwt.StoreRefreshToken(user.Id, refreshToken, refreshExpiry);

        // 更新最后登录时间
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("login_success", user.Id, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");

        _logger.LogInformation("[Auth] 登录成功: {U} (role={R})", user.Username, user.Role);

        var profile = new UserProfile
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = user.Role,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            LastLoginAt = user.LastLoginAt,
        };

        // 同时返回 profile 与 user 字段，兼容新旧前端调用
        return Ok(new
        {
            accessToken,
            refreshToken,
            expiresAt = accessExpiry,
            tokenType = "Bearer",
            profile,
            user = profile,
        });
    }

    /// <summary>
    /// POST /api/auth/logout — 用户登出
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request)
    {
        // 吊销当前 Refresh Token
        if (request != null && !string.IsNullOrEmpty(request.RefreshToken))
        {
            await _jwt.RevokeToken(request.RefreshToken);
        }

        _logger.LogInformation("[Auth] 登出: userId={U}", GetUserId());
        return Ok(new { message = "已登出" });
    }

    /// <summary>
    /// POST /api/auth/refresh — 刷新 Access Token
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _jwt.RefreshTokens(request.RefreshToken);
        if (result == null)
        {
            _logger.LogWarning("[Auth] Token 刷新失败");
            return Unauthorized(new { error = "Refresh Token 无效或已过期" });
        }

        _logger.LogInformation("[Auth] Token 刷新成功: userId={U}", result.Profile.Id);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/auth/change-password — 修改密码
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        => await DoChangePassword(request);

    /// <summary>
    /// PUT /api/auth/password — 修改密码（前端兼容路由）
    /// </summary>
    [HttpPut("password")]
    [Authorize]
    public async Task<IActionResult> ChangePasswordCompat([FromBody] ChangePasswordRequest request)
        => await DoChangePassword(request);

    private async Task<IActionResult> DoChangePassword(ChangePasswordRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null)
            return NotFound(new { error = "用户不存在" });

        // 验证旧密码
        if (!_hasher.VerifyPassword(request.OldPassword, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("[Auth] 改密失败 — 旧密码错误: userId={U}", userId);
            // [TASK-OPT-12-P4-DEEPEN] 审计日志：改密失败
            await AuditAsync("change_password_failed", userId, null, null, null,
                $"{{\"username\":\"{user.Username}\",\"reason\":\"old_password_wrong\"}}");
            return BadRequest(new { error = "旧密码错误" });
        }

        // 哈希新密码
        var (newHash, newSalt) = _hasher.HashPassword(request.NewPassword);
        user.PasswordHash = newHash;
        user.PasswordSalt = newSalt;
        user.UpdatedAt = DateTime.UtcNow;

        // 吊销所有 Refresh Token（强制所有设备重新登录）
        await _jwt.RevokeAllUserTokens(userId.Value);

        await _db.SaveChangesAsync();

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：修改密码
        await AuditAsync("change_password", userId, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");

        _logger.LogInformation("[Auth] 密码已修改: userId={U}", userId);
        return Ok(new { message = "密码已修改，请重新登录" });
    }

    /// <summary>
    /// GET /api/auth/me — 获取当前用户信息
    /// </summary>
    [HttpGet("me")]
    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null)
            return NotFound();

        return Ok(new UserProfile
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = user.Role,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            LastLoginAt = user.LastLoginAt,
        });
    }

    // ========== 扫码登录 Ticket（OPT12 需求 10） ==========

    /// <summary>
    /// POST /api/auth/login-ticket — 生成一次性扫码登录 Ticket（90 秒有效，状态 pending）
    /// 未登录可调用；前端展示二维码（内容为 ticket URL），家长端 APP 扫码后确认。
    /// </summary>
    [HttpPost("login-ticket")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateLoginTicket([FromBody] LoginTicketRequest? request)
    {
        var entry = _tickets.CreateLoginTicket(request?.ClientId);
        _logger.LogInformation("[Auth] 扫码登录 Ticket 已生成: {Ticket}", entry.Ticket);

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：生成登录 Ticket（ticket 打码防日志泄露）
        await AuditAsync("login_ticket_generate", null, null, null, null,
            $"{{\"ticket\":\"{MaskTicket(entry.Ticket)}\"}}");

        return Ok(BuildLoginTicketResponse(entry));
    }

    /// <summary>
    /// GET /api/auth/login-ticket/{ticket} — 轮询扫码登录状态
    /// 状态：pending（等待确认）/ confirmed（已确认，首次返回 JWT 并一次性消费）/ expired
    /// </summary>
    [HttpGet("login-ticket/{ticket}")]
    [AllowAnonymous]
    public async Task<IActionResult> PollLoginTicket(string ticket)
    {
        var entry = _tickets.Get(ticket);
        if (entry == null || entry.Kind != "login")
            return Ok(new LoginTicketResponse
            {
                Ticket = ticket,
                Status = TicketStore.StatusExpired,
                ExpiresAt = DateTime.UtcNow,
                ExpiresInSeconds = 0,
            });

        var response = BuildLoginTicketResponse(entry);

        // 已确认且未消费：签发 JWT 并一次性消费
        if (entry.Status == TicketStore.StatusConfirmed
            && entry.ConfirmedByUserId != null
            && !entry.Consumed)
        {
            var user = await _db.Users.FindAsync(entry.ConfirmedByUserId.Value);
            if (user != null && user.IsActive)
            {
                var (accessToken, refreshToken, accessExpiry, refreshExpiry) =
                    _jwt.GenerateTokens(user.Id, user.Username, user.Role);
                await _jwt.StoreRefreshToken(user.Id, refreshToken, refreshExpiry);

                _tickets.Consume(ticket);

                response.Auth = new AuthResponse
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    ExpiresAt = accessExpiry,
                    TokenType = "Bearer",
                    Profile = BuildProfile(user),
                };

                // [TASK-OPT-12-P4-DEEPEN] 审计日志：扫码登录 Ticket 消费（完成登录）
                await AuditAsync("login_ticket_consume", user.Id, null, null, null,
                    $"{{\"username\":\"{user.Username}\"}}");

                _logger.LogInformation("[Auth] 扫码登录成功: {U}", user.Username);
            }
        }

        return Ok(response);
    }

    /// <summary>
    /// POST /api/auth/login-ticket/{ticket}/confirm — 家长端 APP 确认扫码登录（需登录态）
    /// </summary>
    [HttpPost("login-ticket/{ticket}/confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmLoginTicket(string ticket, [FromBody] LoginTicketConfirmRequest? request)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        // [TASK-OPT-12-P4-DEEPEN] 确认失败限速：5 次/小时（按 IP）
        const int maxConfirmFailures = 5;
        const int confirmWindowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"login-ticket-confirm:ip:{clientIp}", maxConfirmFailures, confirmWindowSeconds))
        {
            _logger.LogWarning("[Auth] 扫码确认被限速: {Ticket} @ {Ip}", MaskTicket(ticket), clientIp);
            return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });
        }

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null)
            return NotFound(new { error = "用户不存在" });

        if (!_tickets.Confirm(ticket, userId.Value, user.Username))
        {
            var entry = _tickets.Get(ticket);
            var reason = entry == null || entry.Kind != "login" ? "invalid"
                : entry.Status == TicketStore.StatusExpired ? "expired" : "used";

            // [TASK-OPT-12-P4-DEEPEN] 审计日志：确认失败 + 失败计数
            await AuditAsync("login_ticket_confirm_failed", userId, null, null, null,
                $"{{\"ticket\":\"{MaskTicket(ticket)}\",\"reason\":\"{reason}\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"login-ticket-confirm:ip:{clientIp}",
                maxConfirmFailures, confirmWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });

            if (entry == null || entry.Kind != "login")
                return NotFound(new { error = "Ticket 无效" });
            if (entry.Status == TicketStore.StatusExpired)
                return BadRequest(new { error = "Ticket 已过期" });
            return BadRequest(new { error = "Ticket 已使用" });
        }

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：确认成功
        await AuditAsync("login_ticket_confirm", userId, null, null, null,
            $"{{\"ticket\":\"{MaskTicket(ticket)}\"}}");

        _logger.LogInformation("[Auth] 扫码登录 Ticket 已确认: {Ticket} by userId={U}", ticket, userId);
        return Ok(new { status = TicketStore.StatusConfirmed, message = "已确认，网页端即将自动登录" });
    }

    // ========== 忘记密码重置 Ticket（OPT12 需求 12） ==========

    /// <summary>
    /// POST /api/auth/reset-ticket — 生成一次性重置 Ticket（10 分钟有效，状态 pending）
    /// 未登录可调用；账号不存在时同样返回 Ticket（不泄露账号存在性），确认环节兜底。
    /// </summary>
    [HttpPost("reset-ticket")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateResetTicket([FromBody] ResetTicketRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // 校验账号是否存在（仅记录日志，不向调用方泄露）
        var exists = await _db.Users.AnyAsync(u => u.Username == request.Username && u.IsActive);
        if (!exists)
        {
            _logger.LogWarning("[Auth] 重置 Ticket 生成 — 目标账号不存在: {U}", request.Username);
        }

        var entry = _tickets.CreateResetTicket(request.Username);
        _logger.LogInformation("[Auth] 重置 Ticket 已生成: {Ticket} (target={U})", entry.Ticket, request.Username);

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：生成重置 Ticket
        await AuditAsync("reset_ticket_generate", null, null, null, null,
            $"{{\"ticket\":\"{MaskTicket(entry.Ticket)}\",\"target\":\"{request.Username}\"}}");

        return Ok(BuildResetTicketResponse(entry));
    }

    /// <summary>
    /// GET /api/auth/reset-ticket/{ticket} — 轮询重置 Ticket 状态
    /// 状态：pending / confirmed / expired
    /// </summary>
    [HttpGet("reset-ticket/{ticket}")]
    [AllowAnonymous]
    public IActionResult PollResetTicket(string ticket)
    {
        var entry = _tickets.Get(ticket);
        if (entry == null || entry.Kind != "reset")
            return Ok(new ResetTicketResponse
            {
                Ticket = ticket,
                Status = TicketStore.StatusExpired,
                ExpiresAt = DateTime.UtcNow,
                ExpiresInSeconds = 0,
            });

        return Ok(BuildResetTicketResponse(entry));
    }

    /// <summary>
    /// POST /api/auth/reset-ticket/{ticket}/confirm — 家长端 APP 确认重置身份（需登录态）
    /// 确认者账号必须与 Ticket 目标账号一致。
    /// </summary>
    [HttpPost("reset-ticket/{ticket}/confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmResetTicket(string ticket, [FromBody] ResetTicketConfirmRequest? request)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        // [TASK-OPT-12-P4-DEEPEN] 确认失败限速：5 次/小时（按 IP）
        const int maxConfirmFailures = 5;
        const int confirmWindowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"reset-ticket-confirm:ip:{clientIp}", maxConfirmFailures, confirmWindowSeconds))
        {
            _logger.LogWarning("[Auth] 重置确认被限速: {Ticket} @ {Ip}", MaskTicket(ticket), clientIp);
            return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });
        }

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null)
            return NotFound(new { error = "用户不存在" });

        if (!_tickets.Confirm(ticket, userId.Value, user.Username))
        {
            var entry = _tickets.Get(ticket);
            var reason = entry == null || entry.Kind != "reset" ? "invalid"
                : entry.Status == TicketStore.StatusExpired ? "expired"
                : entry.Status == TicketStore.StatusConfirmed ? "confirmed" : "account_mismatch";

            // [TASK-OPT-12-P4-DEEPEN] 审计日志：确认失败 + 失败计数
            await AuditAsync("reset_ticket_confirm_failed", userId, null, null, null,
                $"{{\"ticket\":\"{MaskTicket(ticket)}\",\"reason\":\"{reason}\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"reset-ticket-confirm:ip:{clientIp}",
                maxConfirmFailures, confirmWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });

            if (entry == null || entry.Kind != "reset")
                return NotFound(new { error = "Ticket 无效" });
            if (entry.Status == TicketStore.StatusExpired)
                return BadRequest(new { error = "Ticket 已过期" });
            if (entry.Status == TicketStore.StatusConfirmed)
                return BadRequest(new { error = "Ticket 已确认" });
            return BadRequest(new { error = "确认账号与目标账号不一致" });
        }

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：确认成功
        await AuditAsync("reset_ticket_confirm", userId, null, null, null,
            $"{{\"ticket\":\"{MaskTicket(ticket)}\"}}");

        _logger.LogInformation("[Auth] 重置 Ticket 已确认: {Ticket} by userId={U}", ticket, userId);
        return Ok(new { status = TicketStore.StatusConfirmed, message = "身份已确认，可设置新密码" });
    }

    /// <summary>
    /// POST /api/auth/reset-ticket/{ticket}/reset — 设置新密码（需 Ticket 已确认）
    /// 成功后吊销该账号全部 Refresh Token，Ticket 一次性消费。
    /// TODO(P5)：失败限速（5 次/小时）与审计日志落库。
    /// </summary>
    [HttpPost("reset-ticket/{ticket}/reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(string ticket, [FromBody] ResetTicketResetRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // [TASK-OPT-12-P4-DEEPEN] 重置密码失败限速：5 次/小时（按 IP）
        const int maxResetFailures = 5;
        const int resetWindowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"ticket-reset:ip:{clientIp}", maxResetFailures, resetWindowSeconds))
        {
            _logger.LogWarning("[Auth] 重置密码被限速: {Ticket} @ {Ip}", MaskTicket(ticket), clientIp);
            return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });
        }

        var entry = _tickets.Get(ticket);
        if (entry == null || entry.Kind != "reset")
        {
            await RecordResetFailureAsync(clientIp, maxResetFailures, resetWindowSeconds, "invalid");
            return BadRequest(new { error = "Ticket 无效" });
        }

        if (entry.Status != TicketStore.StatusConfirmed || entry.Consumed)
        {
            await RecordResetFailureAsync(clientIp, maxResetFailures, resetWindowSeconds, "not_confirmed");
            return BadRequest(new { error = "Ticket 尚未确认或已使用" });
        }

        if (string.IsNullOrWhiteSpace(entry.Username))
        {
            await RecordResetFailureAsync(clientIp, maxResetFailures, resetWindowSeconds, "no_target");
            return BadRequest(new { error = "Ticket 缺少目标账号" });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == entry.Username && u.IsActive);
        if (user == null)
        {
            await RecordResetFailureAsync(clientIp, maxResetFailures, resetWindowSeconds, "user_not_found");
            return NotFound(new { error = "账号不存在或已停用" });
        }

        // 哈希新密码 + 吊销全部 Refresh Token
        var (newHash, newSalt) = _hasher.HashPassword(request.NewPassword);
        user.PasswordHash = newHash;
        user.PasswordSalt = newSalt;
        user.UpdatedAt = DateTime.UtcNow;

        await _jwt.RevokeAllUserTokens(user.Id);
        _tickets.Consume(ticket);
        await _db.SaveChangesAsync();

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：重置密码成功
        await AuditAsync("reset_ticket_reset", user.Id, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");

        _logger.LogInformation("[Auth] 密码已通过重置 Ticket 修改: {U}", user.Username);
        return Ok(new { message = "密码已重置，请重新登录" });
    }

    // ========== helpers ==========

    // [TASK-OPT-12-P4-DEEPEN] ========== 审计日志 + 失败限速辅助 ==========

    /// <summary>
    /// 审计日志落库（登录 / 改密 / Ticket 全生命周期操作；HttpContext 缺失时 IP/UA 留空）
    /// </summary>
    private async Task AuditAsync(string action, int? userId, string? targetType, int? targetId, string? userAgent, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            UserAgent = userAgent ?? HttpContext?.Request?.Headers.UserAgent.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Ticket 打码（仅保留前 8 位，防日志/审计泄露完整 Ticket）
    /// </summary>
    private static string MaskTicket(string ticket)
        => string.IsNullOrEmpty(ticket) ? string.Empty
           : ticket.Length <= 8 ? "****" : ticket[..8] + "****";

    /// <summary>
    /// 记录一次重置密码失败（审计 + 计数），超限时由调用方返回 429
    /// </summary>
    private async Task<bool> RecordResetFailureAsync(string clientIp, int maxFailures, int windowSeconds, string reason)
    {
        await AuditAsync("reset_ticket_reset_failed", null, null, null, null,
            $"{{\"reason\":\"{reason}\"}}");
        return RequestRateLimiter.RecordFailure($"ticket-reset:ip:{clientIp}", maxFailures, windowSeconds);
    }

    /// <summary>
    /// 构建扫码登录 Ticket 轮询响应
    /// </summary>
    private static LoginTicketResponse BuildLoginTicketResponse(TicketEntry entry)
    {
        var now = DateTime.UtcNow;
        return new LoginTicketResponse
        {
            Ticket = entry.Ticket,
            Status = entry.Status,
            ExpiresAt = entry.ExpiresAt,
            ExpiresInSeconds = Math.Max(0, (int)(entry.ExpiresAt - now).TotalSeconds),
        };
    }

    /// <summary>
    /// 构建重置 Ticket 轮询响应
    /// </summary>
    private static ResetTicketResponse BuildResetTicketResponse(TicketEntry entry)
    {
        var now = DateTime.UtcNow;
        return new ResetTicketResponse
        {
            Ticket = entry.Ticket,
            Status = entry.Status,
            ExpiresAt = entry.ExpiresAt,
            ExpiresInSeconds = Math.Max(0, (int)(entry.ExpiresAt - now).TotalSeconds),
        };
    }

    /// <summary>
    /// 构建用户档案
    /// </summary>
    private static UserProfile BuildProfile(User user)
    {
        return new UserProfile
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = user.Role,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            LastLoginAt = user.LastLoginAt,
        };
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

