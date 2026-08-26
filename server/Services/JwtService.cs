using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Services;

/// <summary>
/// JWT Token 签发与验证 — Access 60min + Refresh 7d
/// </summary>
public class JwtService : IJwtService
{
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;

    public JwtService(IConfiguration config, AppDbContext db)
    {
        _config = config;
        _db = db;
    }

    public (string accessToken, string refreshToken, DateTime accessExpiry, DateTime refreshExpiry)
        GenerateTokens(int userId, string username, string role)
    {
        // 注：密钥必须 ≥ 32 字节（HS256 最低 256 位），见 appsettings.Development.json 修复记录
        var secretKey = _config["Jwt:SecretKey"] ?? "CHANGE-ME-IN-PRODUCTION-32CHARS-MIN!";
        var issuer = _config["Jwt:Issuer"] ?? "xiaopacai-web";
        var audience = _config["Jwt:Audience"] ?? "xiaopacai-client";
        var accessMinutes = int.Parse(_config["Jwt:AccessTokenExpiryMinutes"] ?? "60");
        var refreshDays = int.Parse(_config["Jwt:RefreshTokenExpiryDays"] ?? "7");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var accessExpiry = now.AddMinutes(accessMinutes);
        var refreshExpiry = now.AddDays(refreshDays);

        // Access Token
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var tokenDescriptor = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: accessExpiry,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);

        // Refresh Token (random opaque token)
        var refreshTokenBytes = RandomNumberGenerator.GetBytes(64);
        var refreshToken = Convert.ToBase64String(refreshTokenBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        return (accessToken, refreshToken, accessExpiry, refreshExpiry);
    }

    public async Task StoreRefreshToken(int userId, string refreshToken, DateTime expiresAt)
    {
        var tokenHash = HashToken(refreshToken);
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            // [SEC-P1] 不落明文 Token（红线 R4.3）：验证仅凭 TokenHash，
            // 库文件泄露时无法直接使用 refresh token 提权
            Token = string.Empty,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task<AuthResponse?> RefreshTokens(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt =>
                rt.TokenHash == tokenHash &&
                !rt.IsRevoked &&
                rt.ExpiresAt > DateTime.UtcNow);

        if (stored?.User == null || !stored.User.IsActive)
            return null;

        // 吊销旧 token
        stored.IsRevoked = true;

        // 签发新 token 对
        var (accessToken, newRefreshToken, accessExpiry, refreshExpiry) =
            GenerateTokens(stored.UserId, stored.User.Username, stored.User.Role);

        // 存储新 refresh token（[SEC-P1] 不落明文，仅存哈希）
        var newTokenHash = HashToken(newRefreshToken);
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = stored.UserId,
            Token = string.Empty,
            TokenHash = newTokenHash,
            ExpiresAt = refreshExpiry,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,
            ExpiresAt = accessExpiry,
            TokenType = "Bearer",
            Profile = MapProfile(stored.User),
        };
    }

    public async Task RevokeAllUserTokens(int userId)
    {
        var tokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync();

        foreach (var t in tokens)
            t.IsRevoked = true;

        await _db.SaveChangesAsync();
    }

    public async Task RevokeToken(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);
        var stored = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (stored != null)
        {
            stored.IsRevoked = true;
            await _db.SaveChangesAsync();
        }
    }

    // [TASK-OPT-12-P4-DEEPEN] ========== 设备级 Token ==========

    /// <summary>
    /// 生成设备级 JWT（限定 scope：diagnostics + usage_report，24 小时有效）
    /// 儿童端随 POST /api/diagnostics 以 Authorization: Bearer 携带
    /// </summary>
    public (string token, DateTime expiresAt) GenerateDeviceToken(string deviceId)
    {
        var secretKey = _config["Jwt:SecretKey"] ?? "CHANGE-ME-IN-PRODUCTION-32CHARS-MIN!";
        var issuer = _config["Jwt:Issuer"] ?? "xiaopacai-web";
        var audience = _config["Jwt:Audience"] ?? "xiaopacai-client";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTime.UtcNow.AddHours(24);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId),
            new Claim(ClaimTypes.Role, "device"),
            new Claim("scope", "diagnostics usage_report device_api"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials));

        return (token, expiresAt);
    }

    /// <summary>
    /// 校验设备级 JWT：签名/有效期合法 + role=device + scope 包含所需权限 + sub 与设备 ID 一致
    /// </summary>
    public bool TryValidateDeviceToken(string token, string expectedDeviceId, string requiredScope)
    {
        var secretKey = _config["Jwt:SecretKey"] ?? "CHANGE-ME-IN-PRODUCTION-32CHARS-MIN!";
        var issuer = _config["Jwt:Issuer"] ?? "xiaopacai-web";
        var audience = _config["Jwt:Audience"] ?? "xiaopacai-client";

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                }, out _);

            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (sub != expectedDeviceId)
                return false;

            // role 必须是 device（拒绝用户 Token 冒充设备上报）
            if (principal.FindFirst(ClaimTypes.Role)?.Value != "device")
                return false;

            // scope 必须包含所需权限（空格分隔的 scope 列表）
            var scopes = principal.FindAll("scope").Select(c => c.Value).ToList();
            return scopes.Any(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requiredScope));
        }
        catch
        {
            return false;
        }
    }

    // ========== helpers ==========

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hash);
    }

    private static UserProfile MapProfile(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        Role = u.Role,
        Email = u.Email,
        AvatarUrl = u.AvatarUrl,
        LastLoginAt = u.LastLoginAt,
    };
}
