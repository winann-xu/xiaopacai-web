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

public class DeviceApiControllerTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }

    private static Mock<IJwtService> CreateJwtMock()
    {
        var mock = new Mock<IJwtService>();
        mock.Setup(j => j.GenerateDeviceToken(It.IsAny<string>()))
            .Returns(("device-token", DateTime.UtcNow.AddHours(24)));
        mock.Setup(j => j.TryValidateDeviceToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        mock.Setup(j => j.GenerateTokens(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(("", "", DateTime.UtcNow, DateTime.UtcNow));
        mock.Setup(j => j.RefreshTokens(It.IsAny<string>()))
            .ReturnsAsync((AuthResponse?)null);
        mock.Setup(j => j.RevokeAllUserTokens(It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        mock.Setup(j => j.RevokeToken(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mock.Setup(j => j.StoreRefreshToken(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static DeviceApiController CreateController(AppDbContext db, Mock<IJwtService>? jwtMock = null)
    {
        jwtMock ??= CreateJwtMock();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceApiController>();
        var c = new DeviceApiController(db, jwtMock.Object, logger);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return c;
    }

    private static void SetDeviceClaims(ControllerBase c, string deviceId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, deviceId),
            new Claim(ClaimTypes.Role, "device"),
        };
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    private static void SetUserClaims(ControllerBase c, int userId, string role = "parent")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        };
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    private static Device CreateDevice(AppDbContext db, string deviceId, int? ownerUserId = null, string pairStatus = "paired")
    {
        var device = new Device
        {
            DeviceId = deviceId,
            DeviceName = $"设备-{deviceId[^6..]}",
            Platform = "harmonyos",
            PairCode = "123456",
            PairStatus = pairStatus,
            OnlineStatus = "offline",
            OwnerUserId = ownerUserId?.ToString(),
            IsActive = true,
        };
        db.Devices.Add(device);
        db.SaveChanges();
        db.Policies.Add(new Policy { DeviceId = device.Id, DailyLimitMinutes = 120, OvertimeAction = "full_lock" });
        db.SaveChanges();
        return device;
    }

    // ===== Register =====

    [Fact]
    public async Task Register_NewDevice_ReturnsToken()
    {
        var db = CreateDb();
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.GenerateDeviceToken(It.IsAny<string>()))
            .Returns(("device-token", DateTime.UtcNow.AddHours(24)));
        var c = CreateController(db, jwtMock);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-001", Platform = "harmonyos" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("device-token", ok.Value!.ToString());
    }

    [Fact]
    public async Task Register_ExistingDevice_NoToken_ReturnsConflict()
    {
        var db = CreateDb();
        CreateDevice(db, "dev-001");
        var c = CreateController(db);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-001" });
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("device_already_registered", conflict.Value!.ToString());
    }

    [Fact]
    public async Task Register_ExistingDevice_InvalidToken_Returns403()
    {
        var db = CreateDb();
        CreateDevice(db, "dev-001");
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.TryValidateDeviceToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        var c = CreateController(db, jwtMock);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-001", ExistingToken = "bad-token" });
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
    }

    [Fact]
    public async Task Register_ExistingDevice_ValidToken_ReturnsNewToken()
    {
        var db = CreateDb();
        CreateDevice(db, "dev-001");
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.TryValidateDeviceToken(It.IsAny<string>(), "dev-001", "device_api"))
            .Returns(true);
        jwtMock.Setup(j => j.GenerateDeviceToken("dev-001"))
            .Returns(("new-token", DateTime.UtcNow.AddHours(24)));
        var c = CreateController(db, jwtMock);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-001", ExistingToken = "valid-token" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("new-token", ok.Value!.ToString());
    }

    [Fact]
    public async Task Register_NewDevice_DoesNotReturnBindCode()
    {
        var db = CreateDb();
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.GenerateDeviceToken(It.IsAny<string>()))
            .Returns(("t", DateTime.UtcNow.AddHours(24)));
        var c = CreateController(db, jwtMock);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-002" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var s = ok.Value!.ToString()!;
        Assert.DoesNotContain("bindCode", s);
    }

    [Fact]
    public async Task Register_NewDevice_DoesNotReturnPairCode()
    {
        var db = CreateDb();
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.GenerateDeviceToken(It.IsAny<string>()))
            .Returns(("t", DateTime.UtcNow.AddHours(24)));
        var c = CreateController(db, jwtMock);

        var result = await c.Register(new DeviceRegisterRequest { DeviceId = "dev-003" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var s = ok.Value!.ToString()!;
        Assert.DoesNotContain("pairCode", s, StringComparison.OrdinalIgnoreCase);
    }

    // [TASK-V208-UNBIND-FIX] 匿名注册不得创建默认策略（否则解绑后重注册会“自动下发”策略）
    [Fact]
    public async Task Register_NewDevice_DoesNotCreateDefaultPolicy()
    {
        var db = CreateDb();
        var jwtMock = CreateJwtMock();
        jwtMock.Setup(j => j.GenerateDeviceToken(It.IsAny<string>()))
            .Returns(("t", DateTime.UtcNow.AddHours(24)));
        var c = CreateController(db, jwtMock);

        await c.Register(new DeviceRegisterRequest { DeviceId = "dev-nopolicy" });

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == "dev-nopolicy");
        Assert.NotNull(device);
        Assert.False(await db.Policies.AnyAsync(p => p.DeviceId == device!.Id),
            "匿名注册不应创建默认策略");
    }

    // ===== GetPolicies（解绑/未绑定设备不返回策略） =====

    [Fact]
    public async Task GetPolicies_UnboundDevice_ReturnsEmptyPolicies()
    {
        var db = CreateDb();
        // 未绑定设备（OwnerUserId 为空），注册时代建了默认策略也不应下发
        var device = CreateDevice(db, "dev-ub", ownerUserId: null, pairStatus: "unpaired");
        var c = CreateController(db);
        SetDeviceClaims(c, "dev-ub");

        var result = await c.GetPolicies();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"policies\":[]", json);
    }

    [Fact]
    public async Task GetPolicies_BoundDevice_ReturnsPolicy()
    {
        var db = CreateDb();
        CreateDevice(db, "dev-bnd", ownerUserId: 10);
        var c = CreateController(db);
        SetDeviceClaims(c, "dev-bnd");

        var result = await c.GetPolicies();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("dailyLimitMinutes", json);
        Assert.Contains("\"policies\":[", json);
    }

    // ===== Heartbeat（未绑定设备返回 0 版本，不下发任何下行内容） =====

    [Fact]
    public async Task Heartbeat_UnboundDevice_ReturnsZeroPolicyVersionAndNoCommands()
    {
        var db = CreateDb();
        CreateDevice(db, "dev-hb-ub", ownerUserId: null, pairStatus: "unpaired");
        var c = CreateController(db);
        SetDeviceClaims(c, "dev-hb-ub");

        var result = await c.Heartbeat(new DeviceHeartbeatRequest { DeviceId = "dev-hb-ub" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"policyVersion\":0", json);
        Assert.Contains("\"announcementSignature\":\"\"", json);
        Assert.Contains("\"commands\":[]", json);
    }

    // ===== BindWithCode（绑定成功补建默认策略） =====

    [Fact]
    public async Task BindWithCode_BindsDeviceAndCreatesDefaultPolicy()
    {
        var db = CreateDb();
        db.Users.Add(new User
        {
            Id = 20,
            Username = "bindowner@x.com",
            PasswordHash = "h",
            PasswordSalt = "s",
            Role = "parent",
            IsActive = true,
        });
        db.PairingInfos.Add(new PairingInfo
        {
            PairCode = "654321",
            PairStatus = "pending",
            OwnerUserId = "20",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        // 模拟“匿名注册后无策略”的设备（TASK-V208-UNBIND-FIX 后注册不再建策略）
        var device = CreateDevice(db, "dev-bc", ownerUserId: null, pairStatus: "unpaired");
        db.Policies.RemoveRange(db.Policies.Where(p => p.DeviceId == device.Id));
        db.SaveChanges();
        var c = CreateController(db);
        SetDeviceClaims(c, "dev-bc");

        var result = await c.BindWithCode(new DeviceBindWithCodeRequest { PairCode = "654321" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("ownerEmail", ok.Value!.ToString()!);
        Assert.True(await db.Policies.AnyAsync(p => p.DeviceId == device.Id),
            "绑定成功应补建默认策略（120 分钟 / full_lock）");
        var bound = await db.Devices.FirstAsync(d => d.DeviceId == "dev-bc");
        Assert.Equal("20", bound.OwnerUserId);
        Assert.Equal("paired", bound.PairStatus);
    }

    // ===== Heartbeat (版本号条件拉取) =====

    [Fact]
    public async Task Heartbeat_ReturnsPolicyVersionAndAnnouncementSignature()
    {
        var db = CreateDb();
        db.Users.Add(new User
        {
            Id = 10,
            Username = "owner@x.com",
            PasswordHash = "h",
            PasswordSalt = "s",
            Role = "parent",
            IsActive = true,
        });
        CreateDevice(db, "dev-001", ownerUserId: 10);
        db.Announcements.Add(new Announcement
        {
            Id = 100,
            Title = "测试公告",
            Content = "内容",
            Priority = "normal",
            Status = "published",
            Version = 2,
            CreatedBy = 10,
            PublishedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var c = CreateController(db);
        SetDeviceClaims(c, "dev-001");

        var result = await c.Heartbeat(new DeviceHeartbeatRequest { DeviceId = "dev-001" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.True(json.Contains("\"policyVersion\":1") && json.Contains("\"announcementSignature\":\"100:2\""), json);
    }

    // ===== EmergencyRelease =====

    [Fact]
    public async Task EmergencyRelease_ParentOwner_ReturnsOk()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 30, Reason = "test" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("success", ok.Value!.ToString());
    }

    [Fact]
    public async Task EmergencyRelease_NonOwnerParent_Returns403()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 99, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001" });
        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task EmergencyRelease_Admin_ReturnsOk()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 1, "admin");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 60 });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("success", ok.Value!.ToString());
    }

    [Fact]
    public async Task EmergencyRelease_DurationOver480_ReturnsBadRequest()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 600 });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task EmergencyRelease_DurationZero_ReturnsBadRequest()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 0 });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task EmergencyRelease_WritesSystemConfig()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 60 });
        var cfg = await db.SystemConfigs.FirstOrDefaultAsync(sc => sc.Key == "emergency_release:dev-001");
        Assert.NotNull(cfg);
        Assert.Contains("parent_initiated", cfg.Value);
        Assert.Contains("by=10", cfg.Value);
    }

    [Fact]
    public async Task EmergencyRelease_DefaultDurationIs60()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("durationMinutes", ok.Value!.ToString()!);
    }

    [Fact]
    public async Task EmergencyRelease_MaxDuration480_ReturnsOk()
    {
        var db = CreateDb();
        var device = CreateDevice(db, "dev-001", ownerUserId: 10);
        var c = CreateController(db);
        SetUserClaims(c, 10, "parent");

        var result = await c.EmergencyRelease(new DeviceEmergencyReleaseRequest { DeviceId = "dev-001", DurationMinutes = 480 });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("success", ok.Value!.ToString());
    }
}
