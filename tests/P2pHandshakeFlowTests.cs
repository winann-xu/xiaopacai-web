using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using Xunit;

namespace XiaopacaiWeb.Tests.P2P;

/// <summary>
/// P2P 握手全流程集成测试 — 设备注册 / 配对 / 策略下发 / 使用上报 / 心跳 / 断线
///
/// 与单元测试不同，这里使用真实 DI 容器（IServiceScopeFactory + InMemory 数据库），
/// 完整走 P2pMessageHandler 的业务逻辑链路（与生产代码路径一致）。
///
/// 覆盖：
/// - 新设备握手（缺配对码拒绝 / 有效配对码注册 + 默认策略 + 配对确认）
/// - 已配对设备重连（状态更新 + 策略下发）
/// - 已吊销设备拒绝
/// - 使用上报（记录写入 + 分类规范化 + 每日汇总 + sync_ack）
/// - 心跳（在线状态 + 公告待推送标记）
/// - 断线（状态置离线）
/// </summary>
public class P2pHandshakeFlowTests : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly string _dbName;
    private readonly List<AppDbContext> _contexts = new();

    public P2pHandshakeFlowTests()
    {
        // 真实 DI 容器：P2pMessageHandler 内部会 CreateScope 获取 AppDbContext
        _dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        foreach (var db in _contexts)
        {
            db.Dispose();
        }
    }

    /// <summary>
    /// 创建消息处理器（与生产注册方式一致：scopeFactory + logger）
    /// </summary>
    private P2pMessageHandler CreateHandler()
    {
        return new P2pMessageHandler(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<P2pMessageHandler>.Instance);
    }

    /// <summary>
    /// 获取一个数据库上下文实例（用于预置数据 / 断言）
    /// 每次调用返回全新未跟踪的上下文，避免 EF InMemory 身份解析
    /// （同一实例返回已跟踪的陈旧实体，导致读到旧状态）
    /// </summary>
    private AppDbContext CreateDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options);
        _contexts.Add(db);
        return db;
    }

    /// <summary>
    /// 预置一个 pending 配对码
    /// </summary>
    private async Task SeedPairingCode(string code, DateTime? expiresAt = null)
    {
        var db = CreateDb();
        db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = 0,
            PairCode = code,
            PairMethod = "manual",
            PairStatus = "pending",
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ==================== Handshake — 新设备 ====================

    [Fact]
    public async Task Handshake_NewDeviceWithoutPairCode_Rejected()
    {
        var handler = CreateHandler();

        var (response, policy, _, dbDeviceId) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "new-device-1", DeviceName = "新手机" },
            peerFingerprint: "aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011",
            remoteEndPoint: "192.168.1.10:1234");

        Assert.False(response.Ok);
        Assert.Equal("unpaired", response.PairStatus);
        Assert.Contains("配对码", response.Error);
        Assert.Null(policy);
        Assert.Null(dbDeviceId);

        // 未创建任何设备
        Assert.Equal(0, await CreateDb().Devices.CountAsync());
    }

    // ==================== [SEC-K1] mTLS 客户端证书强制 ====================

    [Fact]
    public async Task Handshake_MissingClientCertificate_Rejected()
    {
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "no-cert-dev", PairCode = "123456" },
            peerFingerprint: null, remoteEndPoint: "1.2.3.4:9999");

        Assert.False(response.Ok);
        Assert.Contains("客户端证书", response.Error);
        Assert.Equal(0, await CreateDb().Devices.CountAsync());
    }

    [Fact]
    public async Task Handshake_NewDeviceWithValidPairCode_RegistersAndPairs()
    {
        await SeedPairingCode("123456");
        var handler = CreateHandler();

        var (response, policy, _, dbDeviceId) = await handler.HandleHandshake(
            new HandshakeRequest
            {
                DeviceId = "android-device-abc",
                DeviceName = "小明的手机",
                Platform = "android",
                ClientVersion = "2.0.0",
                PairCode = "123456",
                CertFingerprint = "a1b2c3d4e5f67890",
            },
            peerFingerprint: "a1b2c3d4e5f67890", remoteEndPoint: "192.168.1.50:9999");

        // 握手成功
        Assert.True(response.Ok);
        Assert.Equal("paired", response.PairStatus);
        Assert.NotNull(response.SessionId);
        Assert.NotNull(dbDeviceId);

        // 策略随握手返回（2.0 policy_update JSON，默认 120 分钟 full）
        Assert.NotNull(policy);
        var dailyLimit = ExtractDailyLimit(policy!);
        Assert.Equal(120, dailyLimit.LimitMinutes);
        Assert.Equal("full", dailyLimit.RestrictMode);

        // 设备入库
        var db = CreateDb();
        var device = await db.Devices.SingleAsync(d => d.DeviceId == "android-device-abc");
        Assert.Equal("paired", device.PairStatus);
        Assert.Equal("online", device.OnlineStatus);
        Assert.Equal("a1b2c3d4e5f67890", device.CertFingerprint);
        Assert.Equal("123456", device.PairCode);
        Assert.Equal("192.168.1.50:9999", device.IpAddress);

        // 默认策略入库
        var dbPolicy = await db.Policies.SingleAsync(p => p.DeviceId == device.Id);
        Assert.Equal(120, dbPolicy.DailyLimitMinutes);

        // 配对信息确认
        var info = await db.PairingInfos.SingleAsync();
        Assert.Equal("confirmed", info.PairStatus);
        Assert.Equal(device.Id, info.DeviceId);
        Assert.Equal("a1b2c3d4e5f67890", info.TlsFingerprint);
    }

    [Fact]
    public async Task Handshake_NewDeviceWithInvalidPairCode_Rejected()
    {
        await SeedPairingCode("123456"); // 数据库里有别的码
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "dev-x", PairCode = "999999" },
            peerFingerprint: "fp-invalid-pair-code", remoteEndPoint: "ip");

        Assert.False(response.Ok);
        Assert.Contains("无效或已过期", response.Error);
        Assert.Equal(0, await CreateDb().Devices.CountAsync());
    }

    [Fact]
    public async Task Handshake_NewDeviceWithExpiredPairCode_Rejected()
    {
        await SeedPairingCode("123456", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "dev-y", PairCode = "123456" },
            peerFingerprint: "fp-expired-pair-code", remoteEndPoint: "ip");

        Assert.False(response.Ok);
        Assert.Contains("无效或已过期", response.Error);
    }

    // ==================== Handshake — 已有设备 ====================

    [Fact]
    public async Task Handshake_ExistingPairedDevice_ReconnectsAndReturnsPolicy()
    {
        // 预置已配对设备 + 自定义策略
        var db = CreateDb();
        var device = new Device
        {
            DeviceId = "existing-dev",
            DeviceName = "旧名称",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            IsActive = true,
            CertFingerprint = "fp-existing-dev", // [SEC-K1] 已配对设备必须已有可信指纹
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        db.Policies.Add(new Policy
        {
            DeviceId = device.Id,
            DailyLimitMinutes = 180, // 自定义限额
            OvertimeAction = "warn_only",
            CategoryGameLimit = 45,
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, policy, _, dbDeviceId) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "existing-dev", DeviceName = "新名称" },
            peerFingerprint: "fp-existing-dev", remoteEndPoint: "10.0.0.8:7777");

        Assert.True(response.Ok);
        Assert.Equal("paired", response.PairStatus);
        Assert.Equal(device.Id, dbDeviceId);

        // 下发数据库中的自定义策略（2.0 policy_update JSON）
        Assert.NotNull(policy);
        var customPolicy = ExtractDailyLimit(policy!);
        Assert.Equal(180, customPolicy.LimitMinutes);
        Assert.Equal("warn", customPolicy.RestrictMode);
        // [TASK-PRELAUNCH-P1] 分类限额暂不可用：握手策略不再下发 category_limit（-1 = 不限）
        Assert.Equal(-1, ExtractCategoryLimit(policy!, "game"));

        // 状态更新（用全新上下文断言，避免读到跟踪的旧状态）
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "existing-dev");
        Assert.Equal("online", updated.OnlineStatus);
        Assert.Equal("新名称", updated.DeviceName);
        Assert.NotNull(updated.LastSeenAt);
        Assert.Equal("10.0.0.8:7777", updated.IpAddress);
    }

    [Fact]
    public async Task Handshake_RevokedDevice_Rejected()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "revoked-dev",
            DeviceName = "被吊销设备",
            Platform = "android",
            PairStatus = "revoked",
            OnlineStatus = "offline",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, dbDeviceId) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "revoked-dev", PairCode = "123456" },
            peerFingerprint: "fp-revoked-dev", remoteEndPoint: "ip");

        Assert.False(response.Ok);
        Assert.Equal("revoked", response.PairStatus);
        Assert.Contains("吊销", response.Error);
        Assert.NotNull(dbDeviceId);
    }

    [Fact]
    public async Task Handshake_UnpairedExistingDevice_WithPairCode_RepairsPairing()
    {
        await SeedPairingCode("888888"); // 修复既有测试缺陷：需先存在待确认配对码，才能完成重新配对
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "unpaired-dev",
            DeviceName = "待重配设备",
            Platform = "android",
            PairStatus = "unpaired",
            OnlineStatus = "offline",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "unpaired-dev", PairCode = "888888" },
            peerFingerprint: "fp-repaired-dev", remoteEndPoint: "ip");

        Assert.True(response.Ok);
        Assert.Equal("paired", response.PairStatus);

        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "unpaired-dev");
        Assert.Equal("paired", updated.PairStatus);
    }

    // ==================== [SEC-K1] 证书指纹固定 ====================

    [Fact]
    public async Task Handshake_PairedDeviceFingerprintMismatch_RejectedAndNotOverwritten()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "pinned-dev",
            DeviceName = "指纹设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-original",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        // 攻击者以不同客户端证书冒充已配对设备 → 必须拒绝
        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "pinned-dev" },
            peerFingerprint: "fp-attacker", remoteEndPoint: "6.6.6.6:1234");

        Assert.False(response.Ok);
        Assert.Contains("证书指纹不匹配", response.Error);

        // 指纹不得被覆盖（此前缺陷：静默用攻击者证书替换存储指纹）
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "pinned-dev");
        Assert.Equal("fp-original", updated.CertFingerprint);
        // 拒绝发生在状态更新之前：设备仍离线
        Assert.Equal("offline", updated.OnlineStatus);
    }

    [Fact]
    public async Task Handshake_PairedDeviceMatchingFingerprint_Accepted()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "match-dev",
            DeviceName = "匹配设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "FP-AAA",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        // 大小写不敏感比对：真实证书指纹重新编码不应导致误拒
        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "match-dev" },
            peerFingerprint: "fp-aaa", remoteEndPoint: "7.7.7.7:1234");

        Assert.True(response.Ok);
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "match-dev");
        Assert.Equal("online", updated.OnlineStatus);
    }

    [Fact]
    public async Task Handshake_PairedDeviceNoStoredFingerprint_RejectedRequiresRepair()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "tofu-dev",
            DeviceName = "历史设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = null, // 历史设备无指纹记录
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "tofu-dev" },
            peerFingerprint: "fp-tofu-new", remoteEndPoint: "8.8.8.8:1234");

        // [SEC-K1] 无指纹历史设备不再 TOFU 采纳，必须解绑后重新配对
        Assert.False(response.Ok);
        Assert.Contains("重新配对", response.Error);
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "tofu-dev");
        Assert.Null(updated.CertFingerprint);
    }

    [Fact]
    public async Task Handshake_RevokedDeviceWithValidPairCode_RotatesFingerprint()
    {
        // 信任轮换：凭新配对码重新绑定后，新客户端证书指纹被采纳（旧指纹作废）
        await SeedPairingCode("666666");
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "rotate-dev",
            DeviceName = "轮换设备",
            Platform = "android",
            PairStatus = "revoked",
            OnlineStatus = "offline",
            CertFingerprint = "fp-old",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "rotate-dev", PairCode = "666666" },
            peerFingerprint: "fp-rotated", remoteEndPoint: "9.9.9.9:1234");

        Assert.True(response.Ok);
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "rotate-dev");
        Assert.Equal("paired", updated.PairStatus);
        Assert.Equal("fp-rotated", updated.CertFingerprint);
    }

    // ==================== [TASK-PRELAUNCH-FIX-SCAN] 扫码绑定根因修复 ====================

    private async Task SeedPairingCodeOwned(string code, string ownerUserId)
    {
        var db = CreateDb();
        db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = 0,
            PairCode = code,
            PairMethod = "manual",
            PairStatus = "pending",
            OwnerUserId = ownerUserId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Handshake_PairedReconnectWithCrossOwnerPendingCode_Rejected()
    {
        // [TASK-REBIND-GATE] 已配对设备携带他人账号签发的 pending 配对码 = 跨账号换绑尝试，
        // 必须确定性拒绝 device_owned_by_other（不计数限速，避免重试雪崩）；
        // 归属与指纹不允许被配对码逻辑触碰。
        var db = CreateDb();
        db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = 0,
            PairCode = "123456",
            PairMethod = "manual",
            PairStatus = "pending",
            OwnerUserId = "2", // 其他账号签发的 pending 码
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        db.Devices.Add(new Device
        {
            DeviceId = "reconnect-dev",
            DeviceName = "重连设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-reconnect",
            OwnerUserId = "1",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "reconnect-dev", PairCode = "123456" },
            peerFingerprint: "fp-reconnect", remoteEndPoint: "10.255.250.100:1234");

        Assert.False(response.Ok);
        Assert.Equal("device_owned_by_other", response.ErrorCode);
        Assert.Contains("解绑", response.Error);

        // 归属与指纹不被配对码逻辑触碰
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "reconnect-dev");
        Assert.Equal("1", updated.OwnerUserId);
        Assert.Equal("fp-reconnect", updated.CertFingerprint);
        Assert.Equal("offline", updated.OnlineStatus);
    }

    [Fact]
    public async Task Handshake_PairedReconnectWithoutCode_Accepted()
    {
        // 断线重连不携码：仅按证书指纹放行，归属不变（回归旧 117 场景的放行语义）。
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "reconnect-dev",
            DeviceName = "重连设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-reconnect",
            OwnerUserId = "1",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "reconnect-dev" },
            peerFingerprint: "fp-reconnect", remoteEndPoint: "10.255.250.100:1234");

        Assert.True(response.Ok);
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "reconnect-dev");
        Assert.Equal("1", updated.OwnerUserId);
        Assert.Equal("online", updated.OnlineStatus);
    }

    [Fact]
    public async Task Handshake_PairedReconnectWithSameOwnerPendingCode_Accepted()
    {
        // 已配对设备携带本人账号签发的 pending 码 = 正常重连/刷新，仍按指纹放行。
        var db = CreateDb();
        db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = 0,
            PairCode = "123456",
            PairMethod = "manual",
            PairStatus = "pending",
            OwnerUserId = "1", // 与设备归属一致
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        db.Devices.Add(new Device
        {
            DeviceId = "reconnect-dev",
            DeviceName = "重连设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-reconnect",
            OwnerUserId = "1",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "reconnect-dev", PairCode = "123456" },
            peerFingerprint: "fp-reconnect", remoteEndPoint: "10.255.250.100:1234");

        Assert.True(response.Ok);
        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "reconnect-dev");
        Assert.Equal("1", updated.OwnerUserId);
        Assert.Equal("online", updated.OnlineStatus);
    }

    [Fact]
    public async Task Handshake_RebindOwnershipMismatch_ErrorCodeAndNotRateCounted()
    {
        // 设备归属账号 1，pending 码由账号 2 签发 → device_owned_by_other；
        // 确定性拒绝不计入 IP 失败限速：连试 12 次仍返回明确错误码而不是 ip_rate_limited
        await SeedPairingCodeOwned("666666", "2");
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "owned-dev",
            DeviceName = "归属设备",
            Platform = "android",
            PairStatus = "unpaired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-owned-old",
            OwnerUserId = "1",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();
        const string ip = "10.255.250.101:1234";

        for (var i = 0; i < 12; i++)
        {
            var (response, _, _, _) = await handler.HandleHandshake(
                new HandshakeRequest { DeviceId = "owned-dev", PairCode = "666666" },
                peerFingerprint: "fp-owned-new", remoteEndPoint: ip);

            Assert.False(response.Ok);
            Assert.Equal("device_owned_by_other", response.ErrorCode);
            Assert.Contains("解绑", response.Error);
        }
    }

    [Fact]
    public async Task Handshake_FingerprintMismatch_ErrorCodeAndNotRateCounted()
    {
        // 指纹不匹配 = 确定性拒绝：error_code=fingerprint_mismatch 且不计入限速，
        // 儿童端凭错误码停止重试回配对界面，避免重连雪崩触发 K3 封禁
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "fp-dev",
            DeviceName = "指纹设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-original",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();
        const string ip = "10.255.250.102:1234";

        for (var i = 0; i < 12; i++)
        {
            var (response, _, _, _) = await handler.HandleHandshake(
                new HandshakeRequest { DeviceId = "fp-dev" },
                peerFingerprint: "fp-attacker", remoteEndPoint: ip);

            Assert.False(response.Ok);
            Assert.Equal("fingerprint_mismatch", response.ErrorCode);
        }
    }

    [Fact]
    public async Task Handshake_RevokedWithoutCode_ErrorCodeRevoked()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "revoked-dev",
            DeviceName = "吊销设备",
            Platform = "android",
            PairStatus = "revoked",
            OnlineStatus = "offline",
            CertFingerprint = "fp-revoked",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "revoked-dev" },
            peerFingerprint: "fp-revoked", remoteEndPoint: "10.255.250.103:1234");

        Assert.False(response.Ok);
        Assert.Equal("revoked", response.ErrorCode);
    }

    [Fact]
    public async Task Handshake_ExpiredPairCode_HintRefreshQr()
    {
        await SeedPairingCode("654321", DateTime.UtcNow.AddMinutes(-1));
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "expired-code-dev", PairCode = "654321" },
            peerFingerprint: "fp-expired", remoteEndPoint: "10.255.250.104:1234");

        Assert.False(response.Ok);
        Assert.Equal("invalid_pairing_code", response.ErrorCode);
        Assert.Contains("请刷新二维码", response.Error);
    }

    [Fact]
    public async Task Handshake_RebindSuccess_ConsumesPairingCode()
    {
        // 重绑成功后配对码一次性消费（pending → confirmed），不能再次复用
        await SeedPairingCode("777777");
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "consume-dev",
            DeviceName = "消费设备",
            Platform = "android",
            PairStatus = "unpaired",
            OnlineStatus = "offline",
            CertFingerprint = "fp-consumed-old",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "consume-dev", PairCode = "777777" },
            peerFingerprint: "fp-consumed-new", remoteEndPoint: "10.255.250.105:1234");

        Assert.True(response.Ok);
        var pairing = await CreateDb().PairingInfos.SingleAsync(p => p.PairCode == "777777");
        Assert.Equal("confirmed", pairing.PairStatus);
        Assert.NotNull(pairing.ConfirmedAt);
    }

    // ==================== [SEC-K2] 家长端中继会话令牌 + 指纹绑定 ====================

    private async Task SeedParentSession(string deviceId, string? token, string? fingerprint)
    {
        var db = CreateDb();
        db.RelaySessions.Add(new RelaySession
        {
            DeviceId = deviceId,
            Role = "parent",
            Status = "disconnected",
            ConnectedAt = DateTime.UtcNow,
            SessionToken = token,
            Fingerprint = fingerprint,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ParentRelay_MissingSessionToken_Rejected()
    {
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "parent-notoken1", Relay = true },
            peerFingerprint: "fp-parent-1", remoteEndPoint: "10.0.0.1:1000");

        Assert.False(response.Ok);
        Assert.Contains("会话令牌", response.Error);
    }

    [Fact]
    public async Task ParentRelay_InvalidSessionToken_Rejected()
    {
        await SeedParentSession("parent-badtoken", "real-token-1234", "fp-parent-2");
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "parent-badtoken", Relay = true, SessionToken = "fake-token-5678" },
            peerFingerprint: "fp-parent-2", remoteEndPoint: "10.0.0.2:1000");

        Assert.False(response.Ok);
        Assert.Contains("未授权", response.Error);
    }

    [Fact]
    public async Task ParentRelay_ValidTokenButWrongFingerprint_Rejected()
    {
        // 令牌正确但 TLS 客户端证书与注册时绑定的不一致 → 拒绝（防令牌被盗后冒充）
        await SeedParentSession("parent-fpmismatch", "tok-abc", "fp-registered");
        var handler = CreateHandler();

        var (response, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "parent-fpmismatch", Relay = true, SessionToken = "tok-abc" },
            peerFingerprint: "fp-attacker", remoteEndPoint: "10.0.0.3:1000");

        Assert.False(response.Ok);
        Assert.Contains("未授权", response.Error);
    }

    [Fact]
    public async Task ParentRelay_ValidTokenAndFingerprint_Accepted()
    {
        await SeedParentSession("parent-ok1234", "tok-xyz", "fp-parent-ok");
        var handler = CreateHandler();

        var (response, policy, _, dbDeviceId) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "parent-ok1234", Relay = true, SessionToken = "tok-xyz" },
            peerFingerprint: "fp-parent-ok", remoteEndPoint: "10.0.0.4:1000");

        Assert.True(response.Ok);
        Assert.Equal("paired", response.PairStatus);
        Assert.Null(policy);      // 家长端不下发策略
        Assert.Null(dbDeviceId);  // 家长端不创建 Device

        // 复用已注册会话（更新状态而非新建行）
        var db = CreateDb();
        var sessions = await db.RelaySessions.Where(s => s.DeviceId == "parent-ok1234").ToListAsync();
        Assert.Single(sessions);
        Assert.Equal("connected", sessions[0].Status);
    }

    // ==================== [SEC-K3] 握手失败限速 ====================

    [Fact]
    public async Task Handshake_PairCodeBruteForce_BlockedAfterLimit()
    {
        var handler = CreateHandler();

        // 同一 IP 连续 10 次配对失败 → 第 11 次被 IP 级限速拦截
        for (var i = 0; i < 10; i++)
        {
            var (resp, _, _, _) = await handler.HandleHandshake(
                new HandshakeRequest { DeviceId = $"brute-dev-{i}", PairCode = "777777" },
                peerFingerprint: $"fp-brute-{i}", remoteEndPoint: "5.5.5.5:1234");
            Assert.False(resp.Ok);
        }

        var (blocked, _, _, _) = await handler.HandleHandshake(
            new HandshakeRequest { DeviceId = "brute-dev-final", PairCode = "777777" },
            peerFingerprint: "fp-brute-final", remoteEndPoint: "5.5.5.5:1234");

        Assert.False(blocked.Ok);
        Assert.Contains("尝试次数过多", blocked.Error);
        // [TASK-PRELAUNCH-FIX-RATELIMIT] 限速拒绝必须携带 error_code，
        // 儿童端据此进入指数退避而非 1s 重试（122 信自锁闭环根因）
        Assert.Equal("ip_rate_limited", blocked.ErrorCode);
    }

    [Fact]
    public async Task Handshake_BlockedIp_RepeatedAttempts_StayBlockedWithCode()
    {
        // [TASK-PRELAUNCH-FIX-RATELIMIT] 封禁期内的重复尝试：返回稳定 error_code 且
        // 不再计失败次数（窗口不续期），冷却后自动放行——闭环自愈而非无限锁死
        var handler = CreateHandler();

        for (var i = 0; i < 10; i++)
        {
            var (resp, _, _, _) = await handler.HandleHandshake(
                new HandshakeRequest { DeviceId = $"brute-dev-{i}", PairCode = "888888" },
                peerFingerprint: $"fp-brute-{i}", remoteEndPoint: "6.6.6.7:1234");
            Assert.False(resp.Ok);
        }

        // 封禁期内连续 12 次重复尝试（模拟儿童端 1s 重试风暴）
        for (var i = 0; i < 12; i++)
        {
            var (blocked, _, _, _) = await handler.HandleHandshake(
                new HandshakeRequest { DeviceId = $"brute-dev-r{i}", PairCode = "888888" },
                peerFingerprint: $"fp-brute-r{i}", remoteEndPoint: "6.6.6.7:1234");
            Assert.False(blocked.Ok);
            Assert.Equal("ip_rate_limited", blocked.ErrorCode);
        }
    }

    // ==================== Usage Report ====================

    [Fact]
    public async Task UsageReport_ValidRecords_WritesRecordsAndSummary()
    {
        // 预置设备 + 策略（120 分钟）
        var db = CreateDb();
        var device = new Device
        {
            DeviceId = "report-dev",
            DeviceName = "上报设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "online",
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        db.Policies.Add(new Policy { DeviceId = device.Id, DailyLimitMinutes = 120 });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleUsageReport(new UsageReportRequest
        {
            DeviceId = "report-dev",
            BatchId = "batch-001",
            Records = new List<UsageRecordItem>
            {
                new() { AppPackage = "com.tencent.sgame", AppName = "王者荣耀", Category = "GAME",
                    StartTime = DateTime.UtcNow.AddMinutes(-60).ToString("o"), DurationSeconds = 3600, IsBlocked = true },
                new() { AppPackage = "com.android.chrome", AppName = "浏览器", Category = "other",
                    StartTime = DateTime.UtcNow.AddMinutes(-30).ToString("o"), DurationSeconds = 900, IsBlocked = false },
            },
        });

        // sync_ack 汇总
        Assert.Equal("batch-001", ack.BatchId);
        Assert.Equal(2, ack.Synced);
        Assert.Equal(75, ack.TodayTotalMinutes);   // (3600+900)/60
        Assert.Equal(45, ack.TodayRemainingMinutes); // 120 - 75
        Assert.False(ack.OvertimeLocked);

        // 记录已写入 + 分类规范化（GAME → game）
        var records = await db.UsageRecords.ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.AppPackage == "com.tencent.sgame" && r.Category == "game" && r.IsBlocked);
        Assert.Contains(records, r => r.AppPackage == "com.android.chrome" && r.Category == "other");

        // 每日汇总已更新
        var summary = await db.DailySummaries.SingleAsync();
        Assert.Equal(75, summary.TotalMinutes);
        Assert.Equal(60, summary.GameMinutes);
        Assert.Equal(15, summary.OtherMinutes);
        Assert.Equal(1, summary.BlockCount);
    }

    [Fact]
    public async Task UsageReport_OverLimit_ReturnsOvertimeLocked()
    {
        var db = CreateDb();
        var device = new Device
        {
            DeviceId = "overtime-dev",
            DeviceName = "超时设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "online",
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        db.Policies.Add(new Policy { DeviceId = device.Id, DailyLimitMinutes = 30 }); // 限额 30 分钟
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleUsageReport(new UsageReportRequest
        {
            DeviceId = "overtime-dev",
            Records = new List<UsageRecordItem>
            {
                new() { AppPackage = "com.game", Category = "game",
                    StartTime = DateTime.UtcNow.AddMinutes(-45).ToString("o"), DurationSeconds = 2700 }, // 45 分钟
            },
        });

        Assert.Equal(45, ack.TodayTotalMinutes);
        Assert.Equal(0, ack.TodayRemainingMinutes);
        Assert.True(ack.OvertimeLocked, "超过每日限额应标记 overtime_locked");
    }

    [Fact]
    public async Task UsageReport_UnknownDevice_SyncedZero()
    {
        var handler = CreateHandler();

        var ack = await handler.HandleUsageReport(new UsageReportRequest
        {
            DeviceId = "no-such-device",
            Records = new List<UsageRecordItem>
            {
                new() { AppPackage = "com.x", Category = "game", StartTime = "2026-08-11T10:00:00Z", DurationSeconds = 60 },
            },
        });

        Assert.Equal(0, ack.Synced);
        Assert.Equal(0, await CreateDb().UsageRecords.CountAsync());
    }

    [Fact]
    public async Task UsageReport_InvalidStartTime_Skipped()
    {
        var db = CreateDb();
        var device = new Device
        {
            DeviceId = "skip-dev",
            DeviceName = "跳过设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "online",
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleUsageReport(new UsageReportRequest
        {
            DeviceId = "skip-dev",
            Records = new List<UsageRecordItem>
            {
                new() { AppPackage = "com.bad", StartTime = "not-a-date", DurationSeconds = 100 }, // 非法时间 → 跳过
                new() { AppPackage = "com.ok", StartTime = DateTime.UtcNow.AddMinutes(-5).ToString("o"), DurationSeconds = 60 },
            },
        });

        Assert.Equal(1, ack.Synced);
        Assert.Equal(1, await db.UsageRecords.CountAsync());
    }

    // ==================== Heartbeat ====================

    [Fact]
    public async Task Heartbeat_UpdatesOnlineStatusAndLastSeen()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "hb-dev",
            DeviceName = "心跳设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline", // 当前离线
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleHeartbeat(new HeartbeatMessage { DeviceId = "hb-dev", ClientTs = 1000 });

        Assert.True(ack.ServerTs > 0);
        Assert.False(ack.PolicyPending);

        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "hb-dev");
        Assert.Equal("online", updated.OnlineStatus);
        Assert.NotNull(updated.LastSeenAt);
    }

    [Fact]
    public async Task Heartbeat_RecentPublishedAnnouncement_MarksPending()
    {
        var db = CreateDb();
        var device = new Device
        {
            DeviceId = "hb-ann-dev",
            DeviceName = "公告设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "offline",
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        // 1 小时内发布的定向公告 → 应标记待推送
        db.Announcements.Add(new Announcement
        {
            Title = "周末提醒",
            Content = "记得休息",
            Priority = "important",
            Status = "published",
            TargetDeviceId = device.Id,
            CreatedBy = 1,
            PublishedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleHeartbeat(new HeartbeatMessage { DeviceId = "hb-ann-dev" });

        Assert.True(ack.AnnouncementPending, "最近发布的公告应触发 announcement_pending");
    }

    [Fact]
    public async Task Heartbeat_NoPendingContent_AllFlagsFalse()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "hb-quiet-dev",
            DeviceName = "安静设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "online",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        var ack = await handler.HandleHeartbeat(new HeartbeatMessage { DeviceId = "hb-quiet-dev" });

        Assert.False(ack.PolicyPending);
        Assert.False(ack.AnnouncementPending);
    }

    // ==================== 断线 ====================

    [Fact]
    public async Task OnDeviceDisconnected_SetsOffline()
    {
        var db = CreateDb();
        db.Devices.Add(new Device
        {
            DeviceId = "bye-dev",
            DeviceName = "断线设备",
            Platform = "android",
            PairStatus = "paired",
            OnlineStatus = "online",
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler();

        await handler.OnDeviceDisconnected("bye-dev");

        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "bye-dev");
        Assert.Equal("offline", updated.OnlineStatus);
    }

    [Fact]
    public async Task OnDeviceDisconnected_UnknownDevice_NoThrow()
    {
        var handler = CreateHandler();

        await handler.OnDeviceDisconnected("never-existed");

        Assert.Equal(0, await CreateDb().Devices.CountAsync());
    }

    // ==================== 公告推送 ====================

    /// <summary>
    /// 从 2.0 policy_update JSON 中提取 daily_limit 策略
    /// </summary>
    private static (int LimitMinutes, string RestrictMode) ExtractDailyLimit(string policyJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(policyJson);
        var policies = doc.RootElement.GetProperty("payload").GetProperty("policies");
        foreach (var item in policies.EnumerateArray())
        {
            using var policyDoc = System.Text.Json.JsonDocument.Parse(item.GetString()!);
            if (policyDoc.RootElement.GetProperty("policyType").GetString() == "daily_limit")
            {
                return (
                    policyDoc.RootElement.GetProperty("limitMinutes").GetInt32(),
                    policyDoc.RootElement.GetProperty("restrictMode").GetString() ?? "full"
                );
            }
        }
        return (0, "none");
    }

    /// <summary>
    /// 从 2.0 policy_update JSON 中提取指定分类限额
    /// </summary>
    private static int ExtractCategoryLimit(string policyJson, string category)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(policyJson);
        var policies = doc.RootElement.GetProperty("payload").GetProperty("policies");
        foreach (var item in policies.EnumerateArray())
        {
            using var policyDoc = System.Text.Json.JsonDocument.Parse(item.GetString()!);
            var root = policyDoc.RootElement;
            if (root.GetProperty("policyType").GetString() == "category_limit" &&
                root.GetProperty("category").GetString() == category)
            {
                return root.GetProperty("categoryLimitMinutes").GetInt32();
            }
        }
        return -1;
    }

    [Fact]
    public async Task PushAnnouncement_NullP2pService_NoOp()
    {
        // p2p 服务为 null（单元测试场景）时不应抛异常
        var handler = CreateHandler();
        var announcement = new Announcement
        {
            Id = 1,
            Title = "测试公告",
            Content = "内容",
            Priority = "normal",
            Status = "published",
        };

        await handler.PushAnnouncement(announcement, "publish", p2pService: null);
    }
}
