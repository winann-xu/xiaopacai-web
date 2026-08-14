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
            peerFingerprint: null, remoteEndPoint: "192.168.1.10:1234");

        Assert.False(response.Ok);
        Assert.Equal("unpaired", response.PairStatus);
        Assert.Contains("配对码", response.Error);
        Assert.Null(policy);
        Assert.Null(dbDeviceId);

        // 未创建任何设备
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
            peerFingerprint: null, remoteEndPoint: "192.168.1.50:9999");

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
            peerFingerprint: null, remoteEndPoint: "ip");

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
            peerFingerprint: null, remoteEndPoint: "ip");

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
            peerFingerprint: null, remoteEndPoint: "10.0.0.8:7777");

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
            peerFingerprint: null, remoteEndPoint: "ip");

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
            peerFingerprint: null, remoteEndPoint: "ip");

        Assert.True(response.Ok);
        Assert.Equal("paired", response.PairStatus);

        var updated = await CreateDb().Devices.SingleAsync(d => d.DeviceId == "unpaired-dev");
        Assert.Equal("paired", updated.PairStatus);
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
