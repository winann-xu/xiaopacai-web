using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

/// <summary>
/// [SEC-K2] 设备归属校验测试 — 家长仅可访问自己绑定的设备，越权一律 403
///
/// 覆盖：
/// - DeviceAccess 助手：管理员放行 / 家长本人设备 / 他人设备 403 / 设备不存在
/// - ReportsController：家长指定他人设备 403；未指定设备仅统计本人设备；管理员全量
/// - DevicesController：解绑他人设备 403
/// - PoliciesController：保存他人设备策略 403
/// </summary>
public class DeviceAccessTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ClaimsPrincipal Principal(int userId, string role = "parent")
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        }, "Test"));

    private static void SetHttpContext(ControllerBase controller, ClaimsPrincipal principal)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
    }

    /// <summary>创建真实 P2P 依赖（无需监听端口，SendToDevice 无会话时返回 false）</summary>
    private static (P2pMessageHandler, P2pListenerService) CreateP2pServices(AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<P2pCertificateService>();
        services.AddSingleton<P2pMessageHandler>();
        services.AddSingleton<P2pListenerService>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<P2pMessageHandler>(),
                provider.GetRequiredService<P2pListenerService>());
    }

    /// <summary>
    /// [TASK-ACCOUNT-V1] 为当前请求签发并注入一次性 Action Token（模拟 verify-password 通过后的解绑前置）
    /// </summary>
    private static void SetActionTokenHeader(ControllerBase controller, ActionTokenStore store, int userId)
    {
        var token = store.Issue(userId);
        controller.HttpContext.Request.Headers["X-Action-Token"] = token;
    }

    private async Task<(Device Own, Device Other)> SeedTwoDevices(AppDbContext db)
    {
        var own = new Device { Id = 1, DeviceName = "自家设备", DeviceId = "own-001", Platform = "android", OwnerUserId = "1", PairStatus = "paired" };
        var other = new Device { Id = 2, DeviceName = "他人设备", DeviceId = "other-001", Platform = "android", OwnerUserId = "2", PairStatus = "paired" };
        db.Devices.AddRange(own, other);
        await db.SaveChangesAsync();
        return (own, other);
    }

    // ==================== DeviceAccess 助手 ====================

    [Fact]
    public async Task DeviceAccess_Admin_AccessAnyDevice()
    {
        var db = CreateInMemoryDbContext();
        var (_, other) = await SeedTwoDevices(db);

        var (status, device) = await DeviceAccess.CheckAsync(db, other.Id, Principal(99, "admin"));

        Assert.Equal(DeviceAccessResult.Ok, status);
        Assert.Equal(other.Id, device!.Id);
    }

    [Fact]
    public async Task DeviceAccess_ParentOwnedDevice_Ok()
    {
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);

        var (status, device) = await DeviceAccess.CheckAsync(db, own.Id, Principal(1));

        Assert.Equal(DeviceAccessResult.Ok, status);
        Assert.Equal(own.Id, device!.Id);
    }

    [Fact]
    public async Task DeviceAccess_ParentOtherDevice_Forbidden()
    {
        var db = CreateInMemoryDbContext();
        var (_, other) = await SeedTwoDevices(db);

        var (status, device) = await DeviceAccess.CheckAsync(db, other.Id, Principal(1));

        Assert.Equal(DeviceAccessResult.Forbidden, status);
        Assert.Null(device);
    }

    [Fact]
    public async Task DeviceAccess_MissingDevice_NotFound()
    {
        var db = CreateInMemoryDbContext();

        var (status, device) = await DeviceAccess.CheckAsync(db, 999, Principal(1));

        Assert.Equal(DeviceAccessResult.NotFound, status);
        Assert.Null(device);
    }

    // ==================== ReportsController ====================

    [Fact]
    public async Task Reports_Daily_OtherParentDevice_Returns403()
    {
        var db = CreateInMemoryDbContext();
        var (_, other) = await SeedTwoDevices(db);
        var controller = new ReportsController(db);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Daily(other.Id, null);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Reports_Daily_ParentNoDeviceId_OnlyOwnDevices()
    {
        var db = CreateInMemoryDbContext();
        var (own, other) = await SeedTwoDevices(db);
        db.UsageRecords.AddRange(
            new UsageRecord { DeviceId = own.Id, StartTime = AppClock.TodayShanghaiDate().AddHours(12), DurationSeconds = 3600 },
            new UsageRecord { DeviceId = other.Id, StartTime = AppClock.TodayShanghaiDate().AddHours(12), DurationSeconds = 3600 });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Daily(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        // 仅统计本人设备：总时长 60 分钟而非 120
        var payload = ok.Value!.GetType().GetProperty("totalMinutes")!.GetValue(ok.Value);
        Assert.Equal(60, (int)payload!);
    }

    [Fact]
    public async Task Reports_Daily_AdminNoDeviceId_AllDevices()
    {
        var db = CreateInMemoryDbContext();
        var (own, other) = await SeedTwoDevices(db);
        db.UsageRecords.AddRange(
            new UsageRecord { DeviceId = own.Id, StartTime = AppClock.TodayShanghaiDate().AddHours(12), DurationSeconds = 3600 },
            new UsageRecord { DeviceId = other.Id, StartTime = AppClock.TodayShanghaiDate().AddHours(12), DurationSeconds = 3600 });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        SetHttpContext(controller, Principal(99, "admin"));

        var result = await controller.Daily(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!.GetType().GetProperty("totalMinutes")!.GetValue(ok.Value);
        Assert.Equal(120, (int)payload!);
    }

    // ==================== DevicesController ====================

    [Fact]
    public async Task Devices_Unpair_WithoutActionToken_Returns401()
    {
        // [TASK-ACCOUNT-V1] 解绑前置：缺少 X-Action-Token 一律 401（不进入归属校验）
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var controller = new DevicesController(db, handler, p2p, Mock.Of<XiaopacaiWeb.Services.IJwtService>(),
            new ActionTokenStore(), NullLogger<DevicesController>.Instance);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Unpair(own.Id);

        var status = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task Devices_Unpair_OtherUsersToken_Returns401()
    {
        // [TASK-ACCOUNT-V1] Action Token 绑定 userId：他人令牌不可用于本账号解绑
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var tokens = new ActionTokenStore();
        var controller = new DevicesController(db, handler, p2p, Mock.Of<XiaopacaiWeb.Services.IJwtService>(),
            tokens, NullLogger<DevicesController>.Instance);
        SetHttpContext(controller, Principal(1));
        SetActionTokenHeader(controller, tokens, userId: 2); // 令牌属于用户 2

        var result = await controller.Unpair(own.Id);

        var status = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task Devices_Unpair_OtherParentDevice_Returns403()
    {
        var db = CreateInMemoryDbContext();
        var (_, other) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var tokens = new ActionTokenStore();
        var controller = new DevicesController(db, handler, p2p, Mock.Of<XiaopacaiWeb.Services.IJwtService>(),
            tokens, NullLogger<DevicesController>.Instance);
        SetHttpContext(controller, Principal(1));
        SetActionTokenHeader(controller, tokens, userId: 1); // 令牌有效，但设备归属他人 → 403

        var result = await controller.Unpair(other.Id);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Devices_Unpair_OwnDevice_ClearsOwnerAndPairCode()
    {
        // [TASK-PRELAUNCH-FIX-SCAN] 解绑即释放归属：清空 OwnerUserId 与 PairCode，
        // 使任意账号凭新 pending 配对码可重新绑定
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        own.PairCode = "111111";
        await db.SaveChangesAsync();

        var (handler, p2p) = CreateP2pServices(db);
        var tokens = new ActionTokenStore();
        var controller = new DevicesController(db, handler, p2p, Mock.Of<XiaopacaiWeb.Services.IJwtService>(),
            tokens, NullLogger<DevicesController>.Instance);
        SetHttpContext(controller, Principal(1));
        SetActionTokenHeader(controller, tokens, userId: 1);

        var result = await controller.Unpair(own.Id);

        Assert.IsType<OkObjectResult>(result);
        var updated = await db.Devices.FindAsync(own.Id);
        Assert.Equal("unpaired", updated!.PairStatus);
        Assert.Null(updated.OwnerUserId);
        Assert.Null(updated.PairCode);
    }

    [Fact]
    public async Task Devices_List_ReturnsOwnerAccount_AndUnpairClearsIt()
    {
        // [TASK-PRELAUNCH-FIX-SCAN] 设备列表/详情返回绑定账号（跨账号扫码排查）；
        // 解绑清空归属后 ownerAccount 为 null
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 1, Username = "parent1" });
        db.Devices.Add(new Device
        {
            Id = 1, DeviceName = "自家设备", DeviceId = "own-001",
            Platform = "android", OwnerUserId = "1", PairStatus = "paired",
        });
        await db.SaveChangesAsync();

        var (handler, p2p) = CreateP2pServices(db);
        var tokens = new ActionTokenStore();
        var controller = new DevicesController(db, handler, p2p, Mock.Of<XiaopacaiWeb.Services.IJwtService>(),
            tokens, NullLogger<DevicesController>.Instance);
        SetHttpContext(controller, Principal(1));

        // [TASK-ACCOUNT-V1] List 响应为 { devices, deviceCount }
        var ok = Assert.IsType<OkObjectResult>(await controller.List());
        var payload = ok.Value!;
        var devices = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            payload.GetType().GetProperty("devices")!.GetValue(payload));
        Assert.Equal(1, (int)payload.GetType().GetProperty("deviceCount")!.GetValue(payload)!);
        var first = devices.Cast<object>().First();
        Assert.Equal("parent1", first.GetType().GetProperty("ownerAccount")!.GetValue(first));

        // 解绑后归属清空 → 管理员视角 ownerAccount 为 null
        SetActionTokenHeader(controller, tokens, userId: 1);
        await controller.Unpair(1);
        SetHttpContext(controller, Principal(99, "admin"));
        var ok2 = Assert.IsType<OkObjectResult>(await controller.List());
        var payload2 = ok2.Value!;
        var devices2 = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            payload2.GetType().GetProperty("devices")!.GetValue(payload2));
        var first2 = devices2.Cast<object>().First();
        Assert.Null(first2.GetType().GetProperty("ownerAccount")!.GetValue(first2));
    }

    // ==================== PoliciesController ====================

    [Fact]
    public async Task Policies_Save_OtherParentDevice_Returns403()
    {
        var db = CreateInMemoryDbContext();
        var (_, other) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var controller = new PoliciesController(db, handler, p2p, NullLogger<PoliciesController>.Instance);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Save(other.Id, new PolicySaveRequest { DailyLimitMinutes = 120 });

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Policies_Save_OwnDevice_InvalidBedtime_Returns400()
    {
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var controller = new PoliciesController(db, handler, p2p, NullLogger<PoliciesController>.Instance);
        SetHttpContext(controller, Principal(1));

        // [SEC-K7] 历史 K7 脏数据：ISO 时间戳格式的就寝时间必须被拒绝
        var result = await controller.Save(own.Id, new PolicySaveRequest
        {
            DailyLimitMinutes = 120,
            BedtimeStart = "2026-08-13T21:00:00Z",
            BedtimeEnd = "07:00",
        });

        var status = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("就寝时间", status.Value!.ToString());
    }

    [Fact]
    public async Task Policies_Save_OwnDevice_InvalidPackageName_Returns400()
    {
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var controller = new PoliciesController(db, handler, p2p, NullLogger<PoliciesController>.Instance);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Save(own.Id, new PolicySaveRequest
        {
            DailyLimitMinutes = 120,
            Whitelist = new List<string> { "com.android.contacts", "not a package name" },
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Policies_Save_OwnDevice_ValidInput_Succeeds()
    {
        var db = CreateInMemoryDbContext();
        var (own, _) = await SeedTwoDevices(db);
        var (handler, p2p) = CreateP2pServices(db);
        var controller = new PoliciesController(db, handler, p2p, NullLogger<PoliciesController>.Instance);
        SetHttpContext(controller, Principal(1));

        var result = await controller.Save(own.Id, new PolicySaveRequest
        {
            DailyLimitMinutes = 90,
            BedtimeStart = "21:00",
            BedtimeEnd = "07:00",
            TimeoutAction = "partial_lock",
            Whitelist = new List<string> { "com.android.contacts", "com.xiaopacai.child" },
        });

        Assert.IsType<OkObjectResult>(result);
        var policy = await db.Policies.FirstAsync(p => p.DeviceId == own.Id);
        Assert.Equal("21:00", policy.BedtimeStart);
        Assert.Equal("partial_lock", policy.OvertimeAction);
    }
}
