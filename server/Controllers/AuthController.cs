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
    private readonly int _refreshTokenExpiryDays;

    public AuthController(AppDbContext db, IPasswordHasher hasher, IJwtService jwt, TicketStore tickets,
        ILogger<AuthController> logger, IConfiguration config)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _tickets = tickets;
        _logger = logger;
        _refreshTokenExpiryDays = config.GetValue<int>("Jwt:RefreshTokenExpiryDays", 7);
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

        // 查找用户（支持用户名或邮箱登录）
        var loginName = request.Username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.IsActive && (u.Username == loginName ||
                (loginName.Contains("@") && u.Email != null && u.Email.ToLower() == loginName.ToLower())));

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

        // [SEC-K5] 浏览器端会话走 httpOnly Cookie（防 XSS 窃取 localStorage token）；
        // Body 仍返回 token 供 Android/Windows 原生客户端（无法用 httpOnly Cookie）使用
        SetAuthCookies(accessToken, refreshToken, accessExpiry, refreshExpiry);

        // 同时返回 profile 与 user 字段，兼容新旧前端调用
        // [SEC-P1] mustChangePassword：前端据此强制跳转改密页（默认口令/管理员重置后）
        return Ok(new
        {
            accessToken,
            refreshToken,
            expiresAt = accessExpiry,
            tokenType = "Bearer",
            mustChangePassword = user.MustChangePassword,
            profile,
            user = profile,
        });
    }

    /// <summary>
    /// POST /api/auth/register — 家长邮箱注册（个人唯一账号，无需管理员预置）
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var email = request.Email.Trim().ToLower();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "邮箱不能为空" });
        if (request.Password.Length < 6)
            return BadRequest(new { error = "密码至少 6 位" });

        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"register:ip:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "注册过于频繁，请稍后再试" });

        // 邮箱唯一：注册账号的 Username 即邮箱，Email 冗余存储
        var exists = await _db.Users.AnyAsync(u =>
            u.Username == email || (u.Email != null && u.Email.ToLower() == email));
        if (exists)
            return BadRequest(new { error = "该邮箱已注册" });

        var (hash, salt) = _hasher.HashPassword(request.Password);
        var user = new User
        {
            Username = email,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = "parent",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await AuditAsync("register", user.Id, null, null, null,
            $"{{\"email\":\"{email}\"}}");
        _logger.LogInformation("[Auth] 家长账号已注册: {Email}", email);

        // 注册即登录：直接签发 Token
        var (accessToken, refreshToken, accessExpiry, refreshExpiry) =
            _jwt.GenerateTokens(user.Id, user.Username, user.Role);
        await _jwt.StoreRefreshToken(user.Id, refreshToken, refreshExpiry);

        var profile = new UserProfile
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = user.Role,
            Email = user.Email,
        };
        // [SEC-K5] 注册即登录：同样设置 httpOnly Cookie
        SetAuthCookies(accessToken, refreshToken, accessExpiry, refreshExpiry);
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
    /// [SEC-K5] 允许匿名调用：access_token 已过期的浏览器会话也能清除 Cookie；
    /// 吊销凭据本身安全（refresh token 只有请求方自己持有）
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request)
    {
        // 吊销当前 Refresh Token（原生客户端走 body；浏览器会话走 httpOnly Cookie）
        var refreshToken = request?.RefreshToken ?? GetRefreshTokenCookie();
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await _jwt.RevokeToken(refreshToken);
        }

        // [SEC-K10] 登出安全事件审计（不含 token 内容）
        await AuditAsync("logout", GetUserId(), null, null, null,
            $"{{\"revoked\":{!string.IsNullOrEmpty(refreshToken)}}}");

        // [SEC-K5] 清除浏览器会话 Cookie
        ClearAuthCookies();

        _logger.LogInformation("[Auth] 登出: userId={U}", GetUserId());
        return Ok(new { message = "已登出" });
    }

    /// <summary>
    /// POST /api/auth/refresh — 刷新 Access Token
    /// [SEC-K5] 浏览器会话可从 httpOnly refresh_token Cookie 刷新（body 可空）；
    /// 原生客户端仍走 body（Bearer 流程不变）
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request)
    {
        if (request != null && !ModelState.IsValid)
            return BadRequest(ModelState);

        var refreshToken = request?.RefreshToken ?? GetRefreshTokenCookie();
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("[Auth] Token 刷新缺少凭据");
            return Unauthorized(new { error = "Refresh Token 无效或已过期" });
        }

        var result = await _jwt.RefreshTokens(refreshToken);
        if (result == null)
        {
            _logger.LogWarning("[Auth] Token 刷新失败");
            return Unauthorized(new { error = "Refresh Token 无效或已过期" });
        }

        // [SEC-K5] 刷新轮换后同步更新浏览器会话 Cookie
        var refreshExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);
        SetAuthCookies(result.AccessToken, result.RefreshToken, result.ExpiresAt, refreshExpiry);

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
        // [SEC-P1] 改密成功清除强制改密标记（红线 R4.2）
        user.MustChangePassword = false;
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
        // [SEC-P1] 匿名端点限速：防批量生成撑爆 TicketStore
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"login-ticket:ip:{clientIp}", 10, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        var entry = _tickets.CreateLoginTicket(request?.ClientId);
        if (entry == null)
            return StatusCode(429, new { error = "系统繁忙，请稍后重试" });

        // [SEC-P1] 日志打码：不落完整 Ticket
        _logger.LogInformation("[Auth] 扫码登录 Ticket 已生成: {Ticket}", MaskTicket(entry.Ticket));

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
        // [SEC-P1] 匿名轮询限速（正常轮询 ~2s/次，60/分钟足够宽裕）
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"login-ticket-poll:ip:{clientIp}", 60, 60))
            return StatusCode(429, new { error = "操作过于频繁，请稍后再试" });

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

                // [SEC-K5] 浏览器轮询方（Web 管理端）同时写入 httpOnly Cookie 会话；
                // Body 中的 token 保留仅作兼容，前端不再持久化到 localStorage。
                SetAuthCookies(accessToken, refreshToken, accessExpiry, refreshExpiry);

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

        _logger.LogInformation("[Auth] 扫码登录 Ticket 已确认: {Ticket} by userId={U}", MaskTicket(ticket), userId);
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

        // [SEC-P1] 匿名端点限速：防批量生成/账号枚举（账号不存在也返回相同 Ticket 流程）
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"reset-ticket:ip:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        // 校验账号是否存在（仅记录日志，不向调用方泄露）
        var exists = await _db.Users.AnyAsync(u => u.Username == request.Username && u.IsActive);
        if (!exists)
        {
            _logger.LogWarning("[Auth] 重置 Ticket 生成 — 目标账号不存在: {U}", request.Username);
        }

        var entry = _tickets.CreateResetTicket(request.Username);
        if (entry == null)
            return StatusCode(429, new { error = "系统繁忙，请稍后重试" });

        // [SEC-P1] 日志打码：不落完整 Ticket
        _logger.LogInformation("[Auth] 重置 Ticket 已生成: {Ticket} (target={U})", MaskTicket(entry.Ticket), request.Username);

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
        // [SEC-P1] 匿名轮询限速
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"reset-ticket-poll:ip:{clientIp}", 60, 60))
            return StatusCode(429, new { error = "操作过于频繁，请稍后再试" });

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

        _logger.LogInformation("[Auth] 重置 Ticket 已确认: {Ticket} by userId={U}", MaskTicket(ticket), userId);
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
        // [SEC-P1] 用户自设新密码，清除强制改密标记
        user.MustChangePassword = false;
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

    // [SEC-K5] ========== 浏览器会话 Cookie（httpOnly，防 XSS 窃取；SameSite=Strict 防 CSRF；HTTPS 下 Secure） ==========

    /// <summary>
    /// 写入浏览器会话 Cookie：
    /// - access_token：httpOnly，Path=/（API 全局可用，JwtBearer 从 Cookie 读取）
    /// - refresh_token：httpOnly，Path=/api/auth（仅刷新/登出接口可见，缩小暴露面）
    /// - logged_in：非敏感标记，JS 可读（路由守卫判断登录态，不含任何凭据）
    /// </summary>
    private void SetAuthCookies(string accessToken, string refreshToken, DateTime accessExpiry, DateTime refreshExpiry)
    {
        // 单元测试直接调用 Action 时无 HttpContext，跳过 Cookie 写入
        if (ControllerContext?.HttpContext == null) return;

        var secure = Request.IsHttps;
        Response.Cookies.Append("access_token", accessToken, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = secure, Expires = accessExpiry, Path = "/" });
        Response.Cookies.Append("refresh_token", refreshToken, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = secure, Expires = refreshExpiry, Path = "/api/auth" });
        Response.Cookies.Append("logged_in", "1", new CookieOptions
            { HttpOnly = false, SameSite = SameSiteMode.Strict, Secure = secure, Expires = accessExpiry, Path = "/" });
    }

    /// <summary>
    /// 清除浏览器会话 Cookie（登出 / 登录失败兜底）
    /// </summary>
    private void ClearAuthCookies()
    {
        // 单元测试直接调用 Action 时无 HttpContext，跳过 Cookie 清除
        if (ControllerContext?.HttpContext == null) return;

        Response.Cookies.Delete("access_token");
        Response.Cookies.Delete("refresh_token", new CookieOptions { Path = "/api/auth" });
        Response.Cookies.Delete("logged_in");
    }

    /// <summary>
    /// 读取 refresh_token Cookie（浏览器会话）；无 HttpContext（单元测试直调）时返回 null
    /// </summary>
    private string? GetRefreshTokenCookie()
        => ControllerContext?.HttpContext == null ? null : Request.Cookies["refresh_token"];

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

