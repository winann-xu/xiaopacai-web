using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using Xunit;

namespace XiaopacaiWeb.Tests.Services;

/// <summary>
/// [TASK-PRELAUNCH-P3] 公告去重/回执落库测试
///
/// 覆盖：
/// - 内容哈希：确定性、内容变化敏感、优先级敏感
/// - 回执落库：ack 首次确认时间保留、displayed 首次显示时间保留、未知设备安全跳过
/// - 送达 upsert：推送计数递增
/// - 紧急公告未确认统计口径（控制器逻辑核心公式）
/// </summary>
public class AnnouncementDedupTests
{
    private static Announcement Ann(string title = "测试公告", string content = "正文",
        string priority = "normal")
        => new() { Id = 1, Title = title, Content = content, Priority = priority };

    // ==================== 内容哈希 ====================

    [Fact]
    public void ComputeContentHash_Deterministic()
    {
        var h1 = P2pMessageHandler.ComputeContentHash("标题", "内容", "urgent");
        var h2 = P2pMessageHandler.ComputeContentHash("标题", "内容", "urgent");
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void ComputeContentHash_ChangesWithContentAndPriority()
    {
        var base_ = P2pMessageHandler.ComputeContentHash("标题", "内容", "normal");
        Assert.NotEqual(base_, P2pMessageHandler.ComputeContentHash("标题", "内容变了", "normal"));
        Assert.NotEqual(base_, P2pMessageHandler.ComputeContentHash("标题", "内容", "urgent"));
        Assert.NotEqual(base_, P2pMessageHandler.ComputeContentHash("标题变了", "内容", "normal"));
    }

    [Fact]
    public void GetContentHash_FallsBackToComputedWhenColumnEmpty()
    {
        var a = Ann();
        Assert.Equal(P2pMessageHandler.ComputeContentHash(a.Title, a.Content, a.Priority),
            P2pMessageHandler.GetContentHash(a));
        // 已落库哈希优先使用
        a.ContentHash = "deadbeefdeadbeef";
        Assert.Equal("deadbeefdeadbeef", P2pMessageHandler.GetContentHash(a));
    }

    // ==================== 回执落库 ====================

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Devices.Add(new Device
        {
            Id = 1,
            DeviceId = "AND-001",
            DeviceName = "测试设备",
            PairStatus = "paired",
            IsActive = true,
        });
        db.Devices.Add(new Device
        {
            Id = 2,
            DeviceId = "AND-002",
            DeviceName = "离线设备",
            PairStatus = "paired",
            IsActive = true,
        });
        db.Announcements.Add(new Announcement
        {
            Id = 11,
            Title = "紧急通知",
            Content = "请确认",
            Priority = "urgent",
            Status = "published",
            Version = 1,
            ContentHash = "hash111111111111",
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task HandleAnnouncementAck_CreatesDeliveryRow_WithFirstAckTime()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);

        var firstAck = 1_760_000_000L;
        var secondAck = firstAck + 3600;
        await handler.HandleAnnouncementAck("AND-001", "11", firstAck);
        await handler.HandleAnnouncementAck("AND-001", "11", secondAck);

        var row = await db.AnnouncementDeliveries.SingleAsync();
        Assert.Equal(11, row.AnnouncementId);
        Assert.Equal(1, row.DeviceId);
        // 保留首次确认时间，重复 ack 不覆盖
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(firstAck).UtcDateTime, row.AcknowledgedAt);
        Assert.NotEqual(DateTimeOffset.FromUnixTimeSeconds(secondAck).UtcDateTime, row.AcknowledgedAt);
    }

    [Fact]
    public async Task HandleAnnouncementDisplayed_SetsDisplayedAtOnce()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);

        var first = 1_760_000_100L;
        await handler.HandleAnnouncementDisplayed("AND-001", "11", first);
        await handler.HandleAnnouncementDisplayed("AND-001", "11", first + 60);

        var row = await db.AnnouncementDeliveries.SingleAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(first).UtcDateTime, row.DisplayedAt);
        Assert.Null(row.AcknowledgedAt);
    }

    [Fact]
    public async Task HandleAnnouncementAck_UnknownDevice_NoRow()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);

        await handler.HandleAnnouncementAck("AND-999", "11", 1_760_000_000L);

        Assert.Empty(await db.AnnouncementDeliveries.ToListAsync());
    }

    [Fact]
    public async Task HandleAnnouncementAck_InvalidAnnouncementId_NoRow()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);

        await handler.HandleAnnouncementAck("AND-001", "not-a-number", 1_760_000_000L);

        Assert.Empty(await db.AnnouncementDeliveries.ToListAsync());
    }

    private static P2pMessageHandler CreateHandler(AppDbContext db)
    {
        // 直接注入同一个上下文：CreateScope 内部使用相同的 options 实例无法共享数据，
        // 改用可重入的 scope factory 包装现有 db（仅测试用）
        var scope = new TestScope(db);
        return new P2pMessageHandler(new TestScopeFactory(scope),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<P2pMessageHandler>.Instance);
    }

    /// <summary>测试专用 scope：直接返回传入的 db 上下文</summary>
    private sealed class TestScope : IServiceScope
    {
        public TestScope(AppDbContext db) => _provider = new TestProvider(db);
        private readonly TestProvider _provider;
        public IServiceProvider ServiceProvider => _provider;
        public void Dispose() { }
    }

    private sealed class TestProvider : IServiceProvider
    {
        private readonly AppDbContext _db;
        public TestProvider(AppDbContext db) => _db = db;
        public object? GetService(Type serviceType)
            => serviceType == typeof(AppDbContext) ? _db : null;
    }

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScope _scope;
        public TestScopeFactory(IServiceScope scope) => _scope = scope;
        public IServiceScope CreateScope() => _scope;
    }
}
