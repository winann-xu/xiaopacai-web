using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
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
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, IPasswordHasher hasher, IJwtService jwt, ILogger<AuthController> logger)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
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

        // 查找用户
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);

        if (user == null)
        {
            _logger.LogWarning("[Auth] 登录失败 — 用户不存在: {U}", request.Username);
            return Unauthorized(new { error = "用户名或密码错误" });
        }

        // 验证密码
        if (!_hasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("[Auth] 登录失败 — 密码错误: {U}", request.Username);
            return Unauthorized(new { error = "用户名或密码错误" });
        }

        // 签发 Token
        var (accessToken, refreshToken, accessExpiry, refreshExpiry) =
            _jwt.GenerateTokens(user.Id, user.Username, user.Role);

        // 存储 Refresh Token
        await _jwt.StoreRefreshToken(user.Id, refreshToken, refreshExpiry);

        // 更新最后登录时间
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Auth] 登录成功: {U} (role={R})", user.Username, user.Role);

        return Ok(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = accessExpiry,
            TokenType = "Bearer",
            Profile = new UserProfile
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Role = user.Role,
                Email = user.Email,
                AvatarUrl = user.AvatarUrl,
                LastLoginAt = user.LastLoginAt,
            },
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

        _logger.LogInformation("[Auth] 密码已修改: userId={U}", userId);
        return Ok(new { message = "密码已修改，请重新登录" });
    }

    /// <summary>
    /// GET /api/auth/me — 获取当前用户信息
    /// </summary>
    [HttpGet("me")]
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

    // ========== helpers ==========

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

