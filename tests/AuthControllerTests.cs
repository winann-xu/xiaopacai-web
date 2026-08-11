using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

/// <summary>
/// 认证控制器测试 — JWT 登录/登出/刷新/改密/个人信息
///
/// 覆盖：
/// - 登录（成功/用户不存在/密码错误/用户已禁用）
/// - 登出（成功）
/// - Token 刷新（成功/无效 Token）
/// - 修改密码（成功/旧密码错误）
/// - 获取个人信息（成功/未登录/用户不存在）
/// </summary>
public class AuthControllerTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static AuthController CreateController(
        AppDbContext db,
        IPasswordHasher? hasher = null,
        IJwtService? jwt = null)
    {
        hasher ??= Mock.Of<IPasswordHasher>();
        jwt ??= Mock.Of<IJwtService>();

        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthController>();
        return new AuthController(db, hasher, jwt, new TicketStore(), logger);
    }

    /// <summary>
    /// 设置 Controller.HttpContext.User ClaimsPrincipal（模拟已登录用户）
    /// </summary>
    private static void SetUserClaims(ControllerBase controller, int userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    // ==================== Login ====================

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        // Arrange
        var db = CreateInMemoryDbContext();
        var password = "test-password";
        var hasherMock = new Mock<IPasswordHasher>();
        hasherMock.Setup(h => h.VerifyPassword(password, It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var jwtMock = new Mock<IJwtService>();
        var accessExpiry = DateTime.UtcNow.AddHours(1);
        var refreshExpiry = DateTime.UtcNow.AddDays(7);
        jwtMock.Setup(j => j.GenerateTokens(It.IsAny<int>(), "admin", "admin"))
            .Returns(("access-token-value", "refresh-token-value", accessExpiry, refreshExpiry));

        // 预置用户
        var user = new User
        {
            Id = 1,
            Username = "admin",
            DisplayName = "管理员",
            PasswordHash = "hashed-password",
            PasswordSalt = "$argon2id$salt-value",
            Role = "admin",
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, hasherMock.Object, jwtMock.Object);

        // Act
        var result = await controller.Login(new LoginRequest
        {
            Username = "admin",
            Password = password,
        });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        // 登录响应为匿名对象，同时含 profile 与 user 字段（兼容新旧前端）
        var json = System.Text.Json.JsonSerializer.Serialize(okResult.Value,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("access-token-value", root.GetProperty("accessToken").GetString());
        Assert.Equal("refresh-token-value", root.GetProperty("refreshToken").GetString());
        Assert.Equal("Bearer", root.GetProperty("tokenType").GetString());
        Assert.Equal("admin", root.GetProperty("profile").GetProperty("username").GetString());
        Assert.Equal("admin", root.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task Login_UserNotFound_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.Login(new LoginRequest
        {
            Username = "nonexistent",
            Password = "password",
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(unauthorized.Value);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDbContext();
        var hasherMock = new Mock<IPasswordHasher>();
        hasherMock.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false); // 始终拒绝

        db.Users.Add(new User
        {
            Id = 1,
            Username = "admin",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Role = "admin",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, hasherMock.Object);

        var result = await controller.Login(new LoginRequest
        {
            Username = "admin",
            Password = "wrong-password",
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_UserInactive_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User
        {
            Id = 1,
            Username = "disabled-user",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Role = "parent",
            IsActive = false, // 禁用
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.Login(new LoginRequest
        {
            Username = "disabled-user",
            Password = "password",
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_UpdatesLastLoginAt()
    {
        var db = CreateInMemoryDbContext();
        var hasherMock = new Mock<IPasswordHasher>();
        hasherMock.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var jwtMock = new Mock<IJwtService>();
        jwtMock.Setup(j => j.GenerateTokens(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(("at", "rt", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(7)));

        var user = new User
        {
            Id = 1,
            Username = "testuser",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Role = "parent",
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, hasherMock.Object, jwtMock.Object);

        await controller.Login(new LoginRequest { Username = "testuser", Password = "pwd" });

        // 验证 LastLoginAt 被更新
        var updated = await db.Users.FindAsync(1);
        Assert.NotNull(updated!.LastLoginAt);
        Assert.True(updated.LastLoginAt > DateTime.UtcNow.AddMinutes(-1));
    }

    // ==================== Logout ====================

    [Fact]
    public async Task Logout_WithRefreshToken_RevokesToken()
    {
        var db = CreateInMemoryDbContext();
        var jwtMock = new Mock<IJwtService>();

        var controller = CreateController(db, jwt: jwtMock.Object);
        SetUserClaims(controller, 1);

        var result = await controller.Logout(new RefreshRequest
        {
            RefreshToken = "token-to-revoke",
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        jwtMock.Verify(j => j.RevokeToken("token-to-revoke"), Times.Once);
    }

    [Fact]
    public async Task Logout_WithoutRefreshToken_Succeeds()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        SetUserClaims(controller, 1);

        var result = await controller.Logout(null);

        Assert.IsType<OkObjectResult>(result);
    }

    // ==================== Refresh Token ====================

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokens()
    {
        var db = CreateInMemoryDbContext();
        var jwtMock = new Mock<IJwtService>();
        jwtMock.Setup(j => j.RefreshTokens("valid-refresh"))
            .ReturnsAsync(new AuthResponse
            {
                AccessToken = "new-access",
                RefreshToken = "new-refresh",
                TokenType = "Bearer",
                Profile = new UserProfile { Id = 1, Username = "test" },
            });

        var controller = CreateController(db, jwt: jwtMock.Object);

        var result = await controller.Refresh(new RefreshRequest
        {
            RefreshToken = "valid-refresh",
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthResponse>(okResult.Value);
        Assert.Equal("new-access", response.AccessToken);
        Assert.Equal("new-refresh", response.RefreshToken);
    }

    [Fact]
    public async Task Refresh_InvalidToken_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDbContext();
        var jwtMock = new Mock<IJwtService>();
        jwtMock.Setup(j => j.RefreshTokens("expired-token"))
            .ReturnsAsync((AuthResponse?)null);

        var controller = CreateController(db, jwt: jwtMock.Object);

        var result = await controller.Refresh(new RefreshRequest
        {
            RefreshToken = "expired-token",
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // ==================== Change Password ====================

    [Fact]
    public async Task ChangePassword_ValidOldPassword_Succeeds()
    {
        var db = CreateInMemoryDbContext();
        var hasherMock = new Mock<IPasswordHasher>();
        hasherMock.Setup(h => h.VerifyPassword("old-password", It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        hasherMock.Setup(h => h.HashPassword("new-password"))
            .Returns(("new-hash", "new-salt"));

        var jwtMock = new Mock<IJwtService>();

        db.Users.Add(new User
        {
            Id = 1,
            Username = "testuser",
            PasswordHash = "old-hash",
            PasswordSalt = "old-salt",
            Role = "parent",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, hasherMock.Object, jwtMock.Object);
        SetUserClaims(controller, 1);

        var result = await controller.ChangePassword(new ChangePasswordRequest
        {
            OldPassword = "old-password",
            NewPassword = "new-password",
        });

        Assert.IsType<OkObjectResult>(result);

        // 验证密码已更新
        var updated = await db.Users.FindAsync(1);
        Assert.Equal("new-hash", updated!.PasswordHash);
        Assert.Equal("new-salt", updated.PasswordSalt);

        // 验证所有 Refresh Token 已吊销
        jwtMock.Verify(j => j.RevokeAllUserTokens(1), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_ReturnsBadRequest()
    {
        var db = CreateInMemoryDbContext();
        var hasherMock = new Mock<IPasswordHasher>();
        hasherMock.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        db.Users.Add(new User { Id = 1, Username = "test", PasswordHash = "h", PasswordSalt = "s", Role = "parent" });
        await db.SaveChangesAsync();

        var controller = CreateController(db, hasherMock.Object);
        SetUserClaims(controller, 1);

        var result = await controller.ChangePassword(new ChangePasswordRequest
        {
            OldPassword = "wrong",
            NewPassword = "newpass123",
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ==================== GetProfile (me) ====================

    [Fact]
    public async Task GetProfile_Authenticated_ReturnsUser()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User
        {
            Id = 1,
            Username = "admin",
            DisplayName = "管理员",
            Role = "admin",
            Email = "admin@example.com",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        SetUserClaims(controller, 1);

        var result = await controller.GetProfile();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var profile = Assert.IsType<UserProfile>(okResult.Value);
        Assert.Equal("admin", profile.Username);
        Assert.Equal("管理员", profile.DisplayName);
        Assert.Equal("admin", profile.Role);
        Assert.Equal("admin@example.com", profile.Email);
    }

    [Fact]
    public async Task GetProfile_UserNotFound_ReturnsNotFound()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        SetUserClaims(controller, 999); // 不存在的用户

        var result = await controller.GetProfile();
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetProfile_NoClaims_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        // 设置空 HttpContext（无任何 Claims），模拟未登录请求
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var result = await controller.GetProfile();
        Assert.IsType<UnauthorizedResult>(result);
    }

    // ==================== Model Validation ====================

    [Fact]
    public async Task Login_InvalidModel_ReturnsBadRequest()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        controller.ModelState.AddModelError("Username", "Required");

        var result = await controller.Login(new LoginRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
