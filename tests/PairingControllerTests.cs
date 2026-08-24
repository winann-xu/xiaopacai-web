using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

/// <summary>
/// 设备配对 REST API 测试 — 配对码生成 / 校验绑定 / 取消
///
/// 覆盖：
/// - 生成配对码（6 位、5 分钟有效、持久化 pending）
/// - 验证配对码（新设备创建 + 默认策略 + 证书指纹记录）
/// - 验证失败（无效码 / 过期码）
/// - 已存在设备绑定（不重复创建）
/// - 取消配对码
/// </summary>
public class PairingControllerTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static PairingController CreateController(AppDbContext db)
    {
        // [SEC-P1] PairingController 新增 P2pCertificateService 依赖：测试提供最小配置实例
        var certService = new XiaopacaiWeb.P2P.P2pCertificateService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<XiaopacaiWeb.P2P.P2pCertificateService>.Instance);
        var controller = new PairingController(db, NullLogger<PairingController>.Instance, certService);
        // [SEC-R2.1] 默认以管理员身份调用（DeviceAccess 校验归属）
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"),
            }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return controller;
    }

    /// <summary>
    /// 以指定角色/用户身份创建控制器（默认测试为 admin；归属防护测试需要普通家长身份）
    /// </summary>
    private static PairingController CreateControllerAs(AppDbContext db, string role, string userId)
    {
        var certService = new XiaopacaiWeb.P2P.P2pCertificateService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<XiaopacaiWeb.P2P.P2pCertificateService>.Instance);
        var controller = new PairingController(db, NullLogger<PairingController>.Instance, certService);
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return controller;
    }

    /// <summary>
    /// 读取匿名对象属性（控制器返回匿名类型，无法直接强转）
    /// </summary>
    private static T? GetAnonValue<T>(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);
        return prop == null ? default : (T?)prop.GetValue(obj);
    }

    /// <summary>
    /// 预置一个 pending 状态的配对码
    /// </summary>
    private static async Task<PairingInfo> SeedPairingCode(AppDbContext db, string code,
        DateTime? expiresAt = null, int deviceId = 0)
    {
        var info = new PairingInfo
        {
            DeviceId = deviceId,
            PairCode = code,
            PairMethod = "manual",
            PairStatus = "pending",
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        };
        db.PairingInfos.Add(info);
        await db.SaveChangesAsync();
        return info;
    }

    // ==================== 生成配对码 ====================

    [Fact]
    public async Task GeneratePairCode_ReturnsSixDigitCode()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.GeneratePairCode(null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var pairCode = GetAnonValue<string>(ok.Value!, "pairCode");
        Assert.NotNull(pairCode);
        Assert.Matches("^[0-9]{6}$", pairCode);
        Assert.Equal(300, GetAnonValue<int>(ok.Value!, "expiresInSeconds"));
    }

    [Fact]
    public async Task GeneratePairCode_PersistsPendingPairingInfo()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        var before = DateTime.UtcNow;

        await controller.GeneratePairCode(new GeneratePairCodeRequest());

        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("pending", info.PairStatus);
        Assert.Equal("manual", info.PairMethod);
        Assert.True(info.ExpiresAt >= before.AddMinutes(4.9), "配对码有效期应为 5 分钟");
        Assert.True(info.ExpiresAt <= before.AddMinutes(5.1));
    }

    [Fact]
    public async Task GeneratePairCode_WithDeviceId_StoresDeviceReference()
    {
        var db = CreateInMemoryDbContext();
        // [SEC-R2.1] 生成指向已有设备的配对码前必须存在该设备
        db.Devices.Add(new Device
        {
            Id = 7,
            DeviceId = "dev-7",
            DeviceName = "测试设备",
            Platform = "android",
            PairStatus = "unpaired",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        await controller.GeneratePairCode(new GeneratePairCodeRequest { DeviceId = 7 });

        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal(7, info.DeviceId);
    }

    [Fact]
    public async Task GeneratePairCode_WithScanMethod_UsesRequestedMethod()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        await controller.GeneratePairCode(new GeneratePairCodeRequest { Method = "scan" });

        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("scan", info.PairMethod);
    }

    // ==================== 验证配对码 — 新设备 ====================

    [Fact]
    public async Task VerifyPairCode_ValidCode_CreatesDeviceAndDefaultPolicy()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "123456");
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "123456",
            DeviceId = "android-device-001",
            DeviceName = "小明手机",
            Platform = "android",
            IpAddress = "192.168.1.100",
            // [SEC] 指纹格式校验：仅接受 64 位十六进制 SHA-256
            CertFingerprint = "a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890",
        });

        // 响应
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("android-device-001", GetAnonValue<string>(ok.Value!, "deviceId"));
        Assert.Equal("小明手机", GetAnonValue<string>(ok.Value!, "deviceName"));
        Assert.Equal("paired", GetAnonValue<string>(ok.Value!, "pairStatus"));

        // 设备已创建并绑定
        var device = await db.Devices.SingleAsync();
        Assert.Equal("android-device-001", device.DeviceId);
        Assert.Equal("paired", device.PairStatus);
        Assert.Equal("192.168.1.100", device.IpAddress);
        Assert.Equal("a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890", device.CertFingerprint);
        Assert.Equal("123456", device.PairCode);

        // 默认策略已创建（每日 120 分钟 + 整机停用）
        var policy = await db.Policies.SingleAsync();
        Assert.Equal(device.Id, policy.DeviceId);
        Assert.Equal(120, policy.DailyLimitMinutes);
        Assert.Equal("full_lock", policy.OvertimeAction);

        // 配对信息已确认
        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("confirmed", info.PairStatus);
        Assert.Equal(device.Id, info.DeviceId);
        Assert.Equal("a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890", info.TlsFingerprint);
        Assert.NotNull(info.ConfirmedAt);
    }

    [Fact]
    public async Task VerifyPairCode_WithoutDeviceId_GeneratesFallbackId()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "654321");
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "654321",
            DeviceName = "未知设备测试",
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var deviceId = GetAnonValue<string>(ok.Value!, "deviceId");
        Assert.NotNull(deviceId);
        // 实现为 $"XP-{Guid:N}"[..14]，即 "XP-" + 11 位十六进制随机
        Assert.StartsWith("XP-", deviceId);
        Assert.Equal(14, deviceId.Length);
    }

    [Fact]
    public async Task VerifyPairCode_InvalidCode_ReturnsBadRequest()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "999999",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = GetAnonValue<string>(bad.Value!, "error");
        Assert.Contains("配对码无效", error);
    }

    [Fact]
    public async Task VerifyPairCode_ExpiredCode_ReturnsBadRequestAndMarksExpired()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "111222", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "111222",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("已过期", GetAnonValue<string>(bad.Value!, "error"));

        // 状态标记为 expired
        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("expired", info.PairStatus);
    }

    // ==================== 验证配对码 — 已有设备 ====================

    [Fact]
    public async Task VerifyPairCode_ExistingDevice_BindsWithoutRecreating()
    {
        var db = CreateInMemoryDbContext();
        var device = new Device
        {
            DeviceId = "existing-device",
            DeviceName = "已有设备",
            Platform = "android",
            PairStatus = "unpaired",
            OnlineStatus = "offline",
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        await SeedPairingCode(db, "333444", deviceId: device.Id);
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "333444",
            CertFingerprint = "a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890",
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("existing-device", GetAnonValue<string>(ok.Value!, "deviceId"));

        // 未创建新设备
        Assert.Equal(1, await db.Devices.CountAsync());

        // 原设备绑定成功
        var updated = await db.Devices.FindAsync(device.Id);
        Assert.Equal("paired", updated!.PairStatus);
        Assert.Equal("a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890a1b2c3d4e5f67890", updated.CertFingerprint);

        // 配对信息确认
        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("confirmed", info.PairStatus);
    }

    [Fact]
    public async Task VerifyPairCode_ExistingDeviceId_DeviceMissing_ReturnsNotFound()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "555666", deviceId: 999); // 设备不存在
        var controller = CreateController(db);

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "555666",
        });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ==================== 取消配对码 ====================

    [Fact]
    public async Task CancelPairCode_MarksPendingCodesAsExpired()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "777888");
        await SeedPairingCode(db, "777888");
        // 已确认的码不受影响
        await SeedPairingCode(db, "777888");
        var confirmed = await db.PairingInfos.LastAsync();
        confirmed.PairStatus = "confirmed";
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.CancelPairCode(new CancelPairCodeRequest { PairCode = "777888" });

        Assert.IsType<OkObjectResult>(result);

        var pending = await db.PairingInfos.Where(p => p.PairStatus == "expired").ToListAsync();
        Assert.Equal(2, pending.Count);
        var stillConfirmed = await db.PairingInfos.CountAsync(p => p.PairStatus == "confirmed");
        Assert.Equal(1, stillConfirmed);
    }

    [Fact]
    public async Task CancelPairCode_UnknownCode_ReturnsOk()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.CancelPairCode(new CancelPairCodeRequest { PairCode = "000000" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ==================== 绑定状态查询（儿童端换绑前置检查） ====================

    [Fact]
    public async Task Status_UnknownDevice_ReturnsFoundFalseAndBoundFalse()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.Status("no-such-device");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False(GetAnonValue<bool>(ok.Value!, "found"));
        Assert.False(GetAnonValue<bool>(ok.Value!, "bound"));
    }

    [Fact]
    public async Task Status_BoundDevice_ReturnsBoundTrueAndPairStatus()
    {
        var db = CreateInMemoryDbContext();
        db.Devices.Add(new Device
        {
            DeviceId = "bound-dev",
            DeviceName = "已绑定设备",
            Platform = "android",
            PairStatus = "paired",
            OwnerUserId = "1",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.Status("bound-dev");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(GetAnonValue<bool>(ok.Value!, "found"));
        Assert.True(GetAnonValue<bool>(ok.Value!, "bound"));
        Assert.Equal("paired", GetAnonValue<string>(ok.Value!, "pairStatus"));
    }

    [Fact]
    public async Task Status_UnboundDevice_ReturnsBoundFalse()
    {
        var db = CreateInMemoryDbContext();
        db.Devices.Add(new Device
        {
            DeviceId = "unbound-dev",
            DeviceName = "未绑定设备",
            Platform = "android",
            PairStatus = "unpaired",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.Status("unbound-dev");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(GetAnonValue<bool>(ok.Value!, "found"));
        Assert.False(GetAnonValue<bool>(ok.Value!, "bound"));
    }

    [Fact]
    public async Task Status_MissingDeviceId_ReturnsBadRequest()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);

        var result = await controller.Status(null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ==================== 归属防护：换绑必须先在原家长端解绑 ====================

    [Fact]
    public async Task VerifyPairCode_DeviceIdOwnedByOther_Returns403()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "888999");
        db.Devices.Add(new Device
        {
            DeviceId = "other-owned-dev",
            DeviceName = "他人设备",
            Platform = "android",
            PairStatus = "paired",
            OwnerUserId = "2",
        });
        await db.SaveChangesAsync();
        // 普通家长身份（非 admin），归属防护应生效
        var controller = CreateControllerAs(db, "parent", "1");

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "888999",
            DeviceId = "other-owned-dev",
        });

        var forbidden = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        // 设备行未被改动
        var device = await db.Devices.SingleAsync();
        Assert.Equal("paired", device.PairStatus);
        Assert.Equal("2", device.OwnerUserId);
    }

    [Fact]
    public async Task VerifyPairCode_ExistingDeviceId_OwnDevice_ReusesRow()
    {
        var db = CreateInMemoryDbContext();
        await SeedPairingCode(db, "888998");
        db.Devices.Add(new Device
        {
            DeviceId = "own-dev",
            DeviceName = "本人设备",
            Platform = "android",
            PairStatus = "unpaired",
        });
        await db.SaveChangesAsync();
        var controller = CreateControllerAs(db, "parent", "1");

        var result = await controller.VerifyPairCode(new VerifyPairCodeRequest
        {
            PairCode = "888998",
            DeviceId = "own-dev",
            DeviceName = "本人设备",
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("own-dev", GetAnonValue<string>(ok.Value!, "deviceId"));
        Assert.Equal(1, await db.Devices.CountAsync());
        var updated = await db.Devices.SingleAsync();
        Assert.Equal("paired", updated.PairStatus);
        Assert.Equal("1", updated.OwnerUserId);
    }
}
