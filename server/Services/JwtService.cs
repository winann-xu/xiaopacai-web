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
            Token = refreshToken,
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

        // 存储新 refresh token
        var newTokenHash = HashToken(newRefreshToken);
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = stored.UserId,
            Token = newRefreshToken,
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
