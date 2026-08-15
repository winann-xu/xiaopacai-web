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
    private readonly VerificationCodeStore _codes;
    private readonly ActionTokenStore _actionTokens;
    private readonly IMailSender _mail;
    private readonly ILogger<AuthController> _logger;
    private readonly int _refreshTokenExpiryDays;

    // [SEC-P2] 虚拟哈希凭据：账号不存在时执行一次等价的 Argon2 校验，
    // 消除"用户不存在"与"密码错误"的响应时间差（防账号枚举）
    private static readonly (string Hash, string Salt) DummyCredential =
        new PasswordHasher().HashPassword(Guid.NewGuid().ToString("N"));

    public AuthController(AppDbContext db, IPasswordHasher hasher, IJwtService jwt, TicketStore tickets,
        VerificationCodeStore codes, ActionTokenStore actionTokens, IMailSender mail,
        ILogger<AuthController> logger, IConfiguration config)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _tickets = tickets;
        _codes = codes;
        _actionTokens = actionTokens;
        _mail = mail;
        _logger = logger;
        _refreshTokenExpiryDays = config.GetValue<int>("Jwt:RefreshTokenExpiryDays", 7);
    }

    /// <summary>
    /// POST /api/auth/login — 用户登录（[TASK-ACCOUNT-V1] 仅接受邮箱）
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // [SEC-P2] Origin/Host 一致性校验（CSRF 纵深防御，原生客户端无 Origin 头放行）
        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        // [TASK-OPT-12-P4-DEEPEN] 登录失败限速：5 次/小时，按邮箱 + IP 双维度封锁
        const int maxLoginFailures = 5;
        const int loginWindowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

        // [TASK-ACCOUNT-V1] 账号名即邮箱（小写归一）；旧数据 Username 非邮箱时兜底匹配 Email 列
        var loginName = request.Username.Trim().ToLower();
        if (RequestRateLimiter.IsBlocked($"login:user:{loginName}", maxLoginFailures, loginWindowSeconds) ||
            RequestRateLimiter.IsBlocked($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds))
        {
            _logger.LogWarning("[Auth] 登录被限速: {U} @ {Ip}", loginName, clientIp);
            return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.IsActive && (u.Username == loginName ||
                (loginName.Contains("@") && u.Email != null && u.Email.ToLower() == loginName)));

        if (user == null)
        {
            // [SEC-P2] 虚拟哈希：耗时与真实校验对齐，防账号枚举
            _ = _hasher.VerifyPassword(request.Password, DummyCredential.Hash, DummyCredential.Salt);
            _logger.LogWarning("[Auth] 登录失败 — 用户不存在: {U}", loginName);
            await AuditAsync("login_failed", null, null, null, null,
                $"{{\"username\":\"{loginName}\",\"reason\":\"user_not_found\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"login:user:{loginName}", maxLoginFailures, loginWindowSeconds) |
                            RequestRateLimiter.RecordFailure($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
            return Unauthorized(new { error = "邮箱或密码错误" });
        }

        // 验证密码
        if (!_hasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("[Auth] 登录失败 — 密码错误: {U}", loginName);
            await AuditAsync("login_failed", user.Id, null, null, null,
                $"{{\"username\":\"{user.Username}\",\"reason\":\"password_wrong\"}}");
            var overLimit = RequestRateLimiter.RecordFailure($"login:user:{loginName}", maxLoginFailures, loginWindowSeconds) |
                            RequestRateLimiter.RecordFailure($"login:ip:{clientIp}", maxLoginFailures, loginWindowSeconds);
            if (overLimit)
                return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
            return Unauthorized(new { error = "邮箱或密码错误" });
        }

        // [TASK-OPT-12-P4-DEEPEN] 登录成功释放失败计数
        RequestRateLimiter.Clear($"login:user:{loginName}");
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
    /// [TASK-ACCOUNT-V1] POST /api/auth/email-code — 发送邮箱验证码
    /// purpose ∈ register | login | reset_password；6 位码 5 分钟有效、单码单用。
    /// 邮件未配置 → 503 明确报错（不阻断密码登录）。
    /// 防枚举：login/reset 用途下目标邮箱未注册时不发信，但统一应答成功文案。
    /// </summary>
    [HttpPost("email-code")]
    [AllowAnonymous]
    public async Task<IActionResult> SendEmailCode([FromBody] EmailCodeRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // [SEC-P2] Origin/Host 一致性校验（CSRF 纵深防御）
        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        var email = request.Email.Trim().ToLower();
        var purpose = request.Purpose.Trim().ToLower();

        // 邮件服务未配置 → 明确报错（A7：不阻断登录，但发码流程必须可解释）
        if (!_mail.IsConfigured)
        {
            await AuditAsync("email_code_unconfigured", null, null, null, null,
                $"{{\"email\":\"{email}\",\"purpose\":\"{purpose}\"}}");
            return StatusCode(503, new { error = "邮件服务未配置，请联系管理员完成邮件设置" });
        }

        // 发码限速：IP 10/小时 + 邮箱 5/小时
        const int maxPerHour = 10;
        const int maxPerHourEmail = 5;
        const int windowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"email-code:ip:{clientIp}", maxPerHour, windowSeconds) ||
            RequestRateLimiter.IsBlocked($"email-code:email:{email}", maxPerHourEmail, windowSeconds))
        {
            await AuditAsync("email_code_rate_limited", null, null, null, null,
                $"{{\"email\":\"{email}\",\"purpose\":\"{purpose}\"}}");
            return StatusCode(429, new { error = "发送过于频繁，请 1 小时后再试" });
        }

        // 防枚举 + 减少无效发信：register 已存在 / login、reset 不存在 → 不发信，统一应答
        var exists = await _db.Users.AnyAsync(u => u.IsActive &&
            (u.Username == email || (u.Email != null && u.Email.ToLower() == email)));
        var suppress = purpose switch
        {
            "register" => exists,
            "login" or "reset_password" => !exists,
            _ => false,
        };
        if (suppress)
        {
            _logger.LogInformation("[Auth] 发码抑制（防枚举）: {Email} purpose={P}", email, purpose);
            return Ok(new { message = "验证码已发送，5 分钟内有效" });
        }

        var code = _codes.Issue(email, purpose);
        var subject = purpose switch
        {
            "register" => "【小趴菜】注册验证码",
            "login" => "【小趴菜】登录验证码",
            _ => "【小趴菜】重置密码验证码",
        };
        var (ok, sendError) = await _mail.SendAsync(email, subject, BuildCodeEmailHtml(code, purpose));
        if (!ok)
        {
            _logger.LogError("[Auth] 验证码邮件发送失败: {Email} ({Err})", email, sendError);
            await AuditAsync("email_code_send_failed", null, null, null, null,
                $"{{\"email\":\"{email}\",\"purpose\":\"{purpose}\"}}");
            return StatusCode(502, new { error = "验证码发送失败，请稍后重试" });
        }

        await AuditAsync("email_code_sent", null, null, null, null,
            $"{{\"email\":\"{email}\",\"purpose\":\"{purpose}\"}}");
        _logger.LogInformation("[Auth] 验证码已发送: {Email} purpose={P}", email, purpose);
        return Ok(new { message = "验证码已发送，5 分钟内有效" });
    }

    /// <summary>
    /// [TASK-ACCOUNT-V1] POST /api/auth/register — 家长邮箱注册（需邮箱验证码，个人唯一账号）
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // [SEC-P2] Origin/Host 一致性校验（CSRF 纵深防御）
        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        var email = request.Email.Trim().ToLower();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "邮箱不能为空" });
        // [SEC-P2] 密码策略：≥8 位且含字母与数字（红线 R4.2）
        var policyError = PasswordPolicy.Validate(request.Password);
        if (policyError != null)
            return BadRequest(new { error = policyError });

        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"register:ip:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "注册过于频繁，请稍后再试" });

        // 邮箱唯一：注册账号的 Username 即邮箱，Email 冗余存储
        var exists = await _db.Users.AnyAsync(u =>
            u.Username == email || (u.Email != null && u.Email.ToLower() == email));
        if (exists)
            return BadRequest(new { error = "该邮箱已注册" });

        // [TASK-ACCOUNT-V1] 验证码校验（单码单用）；验证失败限速：10 次/小时按邮箱
        const int maxVerifyFailures = 10;
        const int verifyWindowSeconds = 3600;
        if (RequestRateLimiter.IsBlocked($"verify:email:{email}", maxVerifyFailures, verifyWindowSeconds))
        {
            await AuditAsync("register_code_verify_rate_limited", null, null, null, null,
                $"{{\"email\":\"{email}\"}}");
            return StatusCode(429, new { error = "验证码尝试次数过多，请 1 小时后再试" });
        }
        if (!_codes.VerifyAndConsume(email, "register", request.Code))
        {
            await AuditAsync("register_code_verify_failed", null, null, null, null,
                $"{{\"email\":\"{email}\"}}");
            var over = RequestRateLimiter.RecordFailure($"verify:email:{email}", maxVerifyFailures, verifyWindowSeconds);
            if (over)
                return StatusCode(429, new { error = "验证码尝试次数过多，请 1 小时后再试" });
            return BadRequest(new { error = "验证码错误或已过期" });
        }
        RequestRateLimiter.Clear($"verify:email:{email}");

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
    /// [TASK-ACCOUNT-V1] POST /api/auth/login/code — 验证码登录（辅助登录方式）
    /// 验证失败限速 5 次/小时（邮箱 + IP 双维度），成功后释放计数。
    /// </summary>
    [HttpPost("login/code")]
    [AllowAnonymous]
    public async Task<IActionResult> CodeLogin([FromBody] CodeLoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        var email = request.Email.Trim().ToLower();
        const int maxFailures = 5;
        const int windowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"codelogin:user:{email}", maxFailures, windowSeconds) ||
            RequestRateLimiter.IsBlocked($"codelogin:ip:{clientIp}", maxFailures, windowSeconds))
        {
            return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.IsActive && (u.Username == email || (u.Email != null && u.Email.ToLower() == email)));
        if (user == null || !_codes.VerifyAndConsume(email, "login", request.Code))
        {
            _logger.LogWarning("[Auth] 验证码登录失败: {E}", email);
            await AuditAsync("code_login_failed", user?.Id, null, null, null,
                $"{{\"email\":\"{email}\",\"reason\":\"{(user == null ? "user_not_found" : "code_wrong")}\"}}");
            var over = RequestRateLimiter.RecordFailure($"codelogin:user:{email}", maxFailures, windowSeconds) |
                       RequestRateLimiter.RecordFailure($"codelogin:ip:{clientIp}", maxFailures, windowSeconds);
            if (over)
                return StatusCode(429, new { error = "登录失败次数过多，请 1 小时后再试" });
            return Unauthorized(new { error = "验证码错误或已过期" });
        }

        RequestRateLimiter.Clear($"codelogin:user:{email}");
        RequestRateLimiter.Clear($"codelogin:ip:{clientIp}");

        var (accessToken, refreshToken, accessExpiry, refreshExpiry) =
            _jwt.GenerateTokens(user.Id, user.Username, user.Role);
        await _jwt.StoreRefreshToken(user.Id, refreshToken, refreshExpiry);

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("code_login_success", user.Id, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");
        _logger.LogInformation("[Auth] 验证码登录成功: {U}", user.Username);

        var profile = BuildProfile(user);
        SetAuthCookies(accessToken, refreshToken, accessExpiry, refreshExpiry);
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
    /// [TASK-ACCOUNT-V1] POST /api/auth/password-reset — 找回密码（邮箱验证码 + 新密码）
    /// 成功后吊销该账号全部 Refresh Token（所有设备强制重新登录）。
    /// </summary>
    [HttpPost("password-reset")]
    [AllowAnonymous]
    public async Task<IActionResult> PasswordReset([FromBody] PasswordResetRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        var email = request.Email.Trim().ToLower();
        const int maxFailures = 5;
        const int windowSeconds = 3600;
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (RequestRateLimiter.IsBlocked($"pwreset:user:{email}", maxFailures, windowSeconds) ||
            RequestRateLimiter.IsBlocked($"pwreset:ip:{clientIp}", maxFailures, windowSeconds))
        {
            return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.IsActive && (u.Username == email || (u.Email != null && u.Email.ToLower() == email)));
        if (user == null || !_codes.VerifyAndConsume(email, "reset_password", request.Code))
        {
            _logger.LogWarning("[Auth] 找回密码失败: {E}", email);
            await AuditAsync("password_reset_failed", user?.Id, null, null, null,
                $"{{\"email\":\"{email}\",\"reason\":\"{(user == null ? "user_not_found" : "code_wrong")}\"}}");
            var over = RequestRateLimiter.RecordFailure($"pwreset:user:{email}", maxFailures, windowSeconds) |
                       RequestRateLimiter.RecordFailure($"pwreset:ip:{clientIp}", maxFailures, windowSeconds);
            if (over)
                return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });
            return BadRequest(new { error = "验证码错误或已过期" });
        }

        var policyError = PasswordPolicy.Validate(request.NewPassword);
        if (policyError != null)
            return BadRequest(new { error = policyError });

        var (newHash, newSalt) = _hasher.HashPassword(request.NewPassword);
        user.PasswordHash = newHash;
        user.PasswordSalt = newSalt;
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;

        // 吊销全部 Refresh Token（所有设备强制重新登录）
        await _jwt.RevokeAllUserTokens(user.Id);
        await _db.SaveChangesAsync();

        await AuditAsync("password_reset", user.Id, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");
        _logger.LogInformation("[Auth] 找回密码完成: {U}", user.Username);
        return Ok(new { message = "密码已重置，请重新登录" });
    }

    /// <summary>
    /// [TASK-ACCOUNT-V1] POST /api/auth/verify-password — 登录态密码二次验证
    /// 用于解绑/换绑前置确认；成功签发 5 分钟一次性 Action Token（绑定 userId）。
    /// </summary>
    [HttpPost("verify-password")]
    [Authorize]
    public async Task<IActionResult> VerifyPassword([FromBody] VerifyPasswordRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null)
            return NotFound(new { error = "用户不存在" });

        const int maxFailures = 5;
        const int windowSeconds = 3600;
        if (RequestRateLimiter.IsBlocked($"verifypw:user:{userId}", maxFailures, windowSeconds))
        {
            return StatusCode(429, new { error = "尝试次数过多，请 1 小时后再试" });
        }

        if (!_hasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("[Auth] 二次验证失败: userId={U}", userId);
            await AuditAsync("verify_password_failed", userId, null, null, null,
                $"{{\"username\":\"{user.Username}\",\"reason\":\"password_wrong\"}}");
            var over = RequestRateLimiter.RecordFailure($"verifypw:user:{userId}", maxFailures, windowSeconds);
            if (over)
                return StatusCode(429, new { error = "尝试次数过多，请 1 小时后再试" });
            return BadRequest(new { error = "密码错误" });
        }

        RequestRateLimiter.Clear($"verifypw:user:{userId}");

        var token = _actionTokens.Issue(userId.Value);
        await AuditAsync("verify_password_success", userId, null, null, null,
            $"{{\"username\":\"{user.Username}\"}}");
        return Ok(new
        {
            actionToken = token,
            expiresInSeconds = ActionTokenStore.LifetimeSeconds,
            message = "身份已确认，5 分钟内完成操作",
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
        // [SEC-P2] Origin/Host 一致性校验（防跨站登出 CSRF）
        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

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

        // [SEC-P2] Origin/Host 一致性校验（防跨站刷新 CSRF）
        if (!IsSameOriginRequest())
            return StatusCode(403, new { error = "跨站请求被拒绝" });

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

        // [SEC-P2] 新密码策略校验（红线 R4.2）
        var policyError = PasswordPolicy.Validate(request.NewPassword);
        if (policyError != null)
            return BadRequest(new { error = policyError });

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
    /// [SEC-P2] Origin/Host 一致性校验（CSRF 纵深防御）：
    /// 浏览器跨站请求带 Origin，与请求 Host 不一致直接拒绝；
    /// 原生客户端不携带 Origin 头，放行。仅比较主机名（不比较端口），
    /// 兼容 vite 开发代理（同为本机回环）与 Nginx 反代（Host 已由 $host 覆盖）。
    /// </summary>
    private bool IsSameOriginRequest()
    {
        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;

        var host = Request.Host.Host;
        if (string.Equals(originUri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
        // 开发场景：前端 dev server 与后端均在本机回环，允许端口差异
        return IsLoopbackHost(originUri.Host) && IsLoopbackHost(host);
    }

    private static bool IsLoopbackHost(string h)
        => h == "localhost" || h == "127.0.0.1" || h == "::1";

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

    /// <summary>
    /// [TASK-ACCOUNT-V1] 验证码邮件 HTML 正文（纯展示用途，不含链接，防钓鱼混淆）
    /// </summary>
    private static string BuildCodeEmailHtml(string code, string purpose)
    {
        var title = purpose switch
        {
            "register" => "注册小趴菜账号",
            "login" => "登录小趴菜账号",
            _ => "重置小趴菜密码",
        };
        var usage = purpose == "register" ? "注册" : purpose == "login" ? "登录" : "重置密码";
        return $"""
            <div style="max-width:480px;margin:0 auto;font-family:'Microsoft YaHei',sans-serif;color:#303133">
              <h2 style="color:#409EFF">{title}</h2>
              <p>您正在{usage}，验证码如下（5 分钟内有效，单次使用）：</p>
              <div style="font-size:32px;letter-spacing:8px;font-weight:bold;color:#409EFF;
                          background:#ecf5ff;border-radius:8px;padding:16px;text-align:center">{code}</div>
              <p style="color:#909399;font-size:13px">若非本人操作，请忽略本邮件。验证码请勿转发他人。</p>
            </div>
            """;
    }
}

