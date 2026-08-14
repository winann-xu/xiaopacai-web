using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Services;

/// <summary>
/// JWT 服务测试 — Access Token 签发 / Refresh Token 存储、刷新、吊销
///
/// 覆盖：
/// - GenerateTokens（Access 60min + Refresh 7d、claims、签名）
/// - 签发的 Access Token 可通过真实验证参数校验（issuer/audience/签名/有效期）
/// - StoreRefreshToken（哈希持久化，不落明文可反查）
/// - RefreshTokens（有效/无效/过期/已吊销/用户禁用）
/// - RevokeToken / RevokeAllUserTokens
/// </summary>
public class JwtServiceTests
{
    private const string SecretKey = "test-secret-key-32-chars-minimum!!";
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";

    // ==================== 基础设施 ====================

    private static IConfiguration CreateConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = SecretKey,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:AccessTokenExpiryMinutes"] = "60",
                ["Jwt:RefreshTokenExpiryDays"] = "7",
            })
            .Build();
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static JwtService CreateService(AppDbContext db)
    {
        return new JwtService(CreateConfig(), db);
    }

    /// <summary>
    /// 用与服务端一致的参数验证 Access Token（模拟真实鉴权链路）
    /// </summary>
    private static ClaimsPrincipal ValidateAccessToken(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        return handler.ValidateToken(accessToken, validationParams, out _);
    }

    private static User CreateUser(AppDbContext db, int id = 1, string username = "admin",
        string role = "admin", bool isActive = true)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            DisplayName = "测试用户",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Role = role,
            IsActive = isActive,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    // ==================== GenerateTokens — Access Token ====================

    [Fact]
    public void GenerateTokens_AccessToken_ValidJwtWithCorrectClaims()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var (accessToken, _, _, _) = service.GenerateTokens(1, "admin", "admin");

        Assert.NotEmpty(accessToken);

        // 可通过真实 TokenValidationParameters 校验（签名/issuer/audience/有效期）
        var principal = ValidateAccessToken(accessToken);

        Assert.Equal("admin", principal.Identity!.Name);
        Assert.True(principal.IsInRole("admin"), "角色 claim 应被解析为 admin");
        Assert.Equal("1", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(Issuer, principal.FindFirst("iss")?.Value);
        Assert.Equal(Audience, principal.FindFirst("aud")?.Value);
    }

    [Fact]
    public void GenerateTokens_AccessToken_ExpiryIs60Minutes()
    {
        var db = CreateDb();
        var service = CreateService(db);
        var before = DateTime.UtcNow;

        var (_, _, accessExpiry, _) = service.GenerateTokens(1, "admin", "admin");

        Assert.True(accessExpiry >= before.AddMinutes(59), "Access Token 有效期应为 60 分钟");
        Assert.True(accessExpiry <= before.AddMinutes(61));
    }

    [Fact]
    public void GenerateTokens_SubClaim_MatchesUserId()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var (accessToken, _, _, _) = service.GenerateTokens(42, "parent1", "parent");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        Assert.Equal("42", token.Claims.First(c => c.Type == "sub").Value);
    }

    // ==================== GenerateTokens — Refresh Token ====================

    [Fact]
    public void GenerateTokens_RefreshToken_RandomOpaqueString()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var (_, refresh1, _, refreshExpiry1) = service.GenerateTokens(1, "admin", "admin");
        var (_, refresh2, _, refreshExpiry2) = service.GenerateTokens(1, "admin", "admin");

        // 不是 JWT（不含 . 分隔符），而是随机不透明字符串
        Assert.DoesNotContain(".", refresh1);
        Assert.True(refresh1.Length >= 40, "64 字节随机数 Base64 编码后应较长");

        // 两次生成互不相同
        Assert.NotEqual(refresh1, refresh2);
    }

    [Fact]
    public void GenerateTokens_RefreshToken_ExpiryIs7Days()
    {
        var db = CreateDb();
        var service = CreateService(db);
        var before = DateTime.UtcNow;

        var (_, _, _, refreshExpiry) = service.GenerateTokens(1, "admin", "admin");

        Assert.True(refreshExpiry >= before.AddDays(6.9));
        Assert.True(refreshExpiry <= before.AddDays(7.1));
    }

    // ==================== StoreRefreshToken ====================

    [Fact]
    public async Task StoreRefreshToken_PersistsWithHash()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var refreshToken = "raw-refresh-token-value";
        await service.StoreRefreshToken(1, refreshToken, DateTime.UtcNow.AddDays(7));

        var stored = await db.RefreshTokens.SingleAsync();
        Assert.Equal(1, stored.UserId);
        // [SEC-P1] 不落明文 Token：明文列留空，仅存 SHA-256 TokenHash（红线 R4.3）
        Assert.Equal(string.Empty, stored.Token);
        Assert.NotEqual(refreshToken, stored.TokenHash);
        Assert.False(stored.IsRevoked);
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // TokenHash = SHA-256(raw token) 的 Base64
        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        Assert.Equal(expectedHash, stored.TokenHash);
    }

    // ==================== RefreshTokens ====================

    [Fact]
    public async Task RefreshTokens_ValidToken_ReturnsNewPairAndRevokesOld()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var (_, refreshToken, _, _) = service.GenerateTokens(1, "admin", "admin");
        await service.StoreRefreshToken(1, refreshToken, DateTime.UtcNow.AddDays(7));

        var result = await service.RefreshTokens(refreshToken);

        Assert.NotNull(result);
        Assert.Equal("Bearer", result!.TokenType);
        Assert.Equal(1, result.Profile.Id);
        Assert.Equal("admin", result.Profile.Username);
        Assert.NotEmpty(result.AccessToken);
        Assert.NotEqual(refreshToken, result.RefreshToken);

        // 旧 token 已吊销，新 token 已入库（共 2 条：1 吊销 + 1 有效）
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, t => t.IsRevoked);
        Assert.Single(tokens, t => !t.IsRevoked);

        // 旧 token 再次刷新 → 失败
        var retry = await service.RefreshTokens(refreshToken);
        Assert.Null(retry);
    }

    [Fact]
    public async Task RefreshTokens_UnknownToken_ReturnsNull()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var result = await service.RefreshTokens("never-stored-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokens_ExpiredToken_ReturnsNull()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var (_, refreshToken, _, _) = service.GenerateTokens(1, "admin", "admin");
        await service.StoreRefreshToken(1, refreshToken, DateTime.UtcNow.AddHours(-1)); // 已过期

        var result = await service.RefreshTokens(refreshToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokens_RevokedToken_ReturnsNull()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var (_, refreshToken, _, _) = service.GenerateTokens(1, "admin", "admin");
        await service.StoreRefreshToken(1, refreshToken, DateTime.UtcNow.AddDays(7));
        await service.RevokeToken(refreshToken);

        var result = await service.RefreshTokens(refreshToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokens_InactiveUser_ReturnsNull()
    {
        var db = CreateDb();
        CreateUser(db, isActive: false); // 用户已被禁用
        var service = CreateService(db);

        var (_, refreshToken, _, _) = service.GenerateTokens(2, "disabled", "parent");
        await service.StoreRefreshToken(2, refreshToken, DateTime.UtcNow.AddDays(7));

        var result = await service.RefreshTokens(refreshToken);

        Assert.Null(result);
    }

    // ==================== RevokeToken / RevokeAllUserTokens ====================

    [Fact]
    public async Task RevokeToken_MarksTokenAsRevoked()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        var (_, refreshToken, _, _) = service.GenerateTokens(1, "admin", "admin");
        await service.StoreRefreshToken(1, refreshToken, DateTime.UtcNow.AddDays(7));

        await service.RevokeToken(refreshToken);

        var stored = await db.RefreshTokens.SingleAsync();
        Assert.True(stored.IsRevoked);
    }

    [Fact]
    public async Task RevokeToken_UnknownToken_DoesNotThrow()
    {
        var db = CreateDb();
        CreateUser(db);
        var service = CreateService(db);

        // 不存在的 token 不应抛异常
        await service.RevokeToken("unknown-token");
    }

    [Fact]
    public async Task RevokeAllUserTokens_RevokesOnlyTargetUser()
    {
        var db = CreateDb();
        CreateUser(db, id: 1, username: "parent1", role: "parent");
        CreateUser(db, id: 2, username: "parent2", role: "parent");
        var service = CreateService(db);

        // 用户 1 两条、用户 2 一条
        await service.StoreRefreshToken(1, "u1-token-a", DateTime.UtcNow.AddDays(7));
        await service.StoreRefreshToken(1, "u1-token-b", DateTime.UtcNow.AddDays(7));
        await service.StoreRefreshToken(2, "u2-token", DateTime.UtcNow.AddDays(7));

        await service.RevokeAllUserTokens(1);

        var user1Tokens = await db.RefreshTokens.Where(t => t.UserId == 1).ToListAsync();
        var user2Tokens = await db.RefreshTokens.Where(t => t.UserId == 2).ToListAsync();
        Assert.All(user1Tokens, t => Assert.True(t.IsRevoked));
        Assert.All(user2Tokens, t => Assert.False(t.IsRevoked));
    }
}
