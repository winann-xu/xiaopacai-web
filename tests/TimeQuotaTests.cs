using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Services;

/// <summary>
/// [TASK-PRELAUNCH-P4] 时间额度口径测试
///
/// 覆盖：
/// - 调整后已用计算：偏移当日有效、跨日失效、最小 0
/// - usage_records upsert：同 (包名,日期) 重复上报覆盖而非累加（消除累计虚高）
/// - 重置偏移落库：上报带回 dailyResetOffsetMinutes → 设备行更新、ack 用调整后口径
/// - 时区助手：Asia/Shanghai 日期格式
/// </summary>
public class TimeQuotaTests
{
    // ==================== 调整后已用计算（纯函数） ====================

    [Theory]
    [InlineData(100, 40, "2026-08-14", "2026-08-14", 60)]
    [InlineData(100, 40, "2026-08-14", "2026-08-15", 100)]   // 偏移跨日失效
    [InlineData(100, 0, "2026-08-14", "2026-08-14", 100)]
    [InlineData(30, 40, "2026-08-14", "2026-08-14", 0)]      // 不小于 0
    [InlineData(100, 40, null, "2026-08-14", 100)]           // 无偏移日期
    [InlineData(100, 40, "", "2026-08-14", 100)]
    public void ComputeAdjusted_Cases(int raw, int offset, string? resetDate, string today, int expected)
    {
        Assert.Equal(expected, AdjustedUsageCalculator.ComputeAdjusted(raw, offset, resetDate, today));
    }

    [Fact]
    public void TodayShanghai_HasExpectedFormat()
    {
        var today = AppClock.TodayShanghai();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", today);
        Assert.Equal(today, AppClock.TodayShanghaiDate().ToString("yyyy-MM-dd"));
    }

    // ==================== usage_records upsert 去重 ====================

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
        db.Policies.Add(new Policy
        {
            Id = 1,
            DeviceId = 1,
            DailyLimitMinutes = 120,
            OvertimeAction = "full_lock",
        });
        db.SaveChanges();
        return db;
    }

    private static P2pMessageHandler CreateHandler(AppDbContext db)
    {
        var scope = new TestScope(db);
        return new P2pMessageHandler(new TestScopeFactory(scope),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<P2pMessageHandler>.Instance);
    }

    /// <summary>
    /// 儿童端每周期上报当日累计值：同 (包名,日期) 必须覆盖更新，总时长不得重复累加
    /// </summary>
    [Fact]
    public async Task HandleUsageReportLegacy_RepeatedCumulativeReports_DoNotInflate()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // 第一轮：微信 30 分钟
        var r1 = $"[{{\"packageName\":\"com.tencent.mm\",\"appName\":\"微信\",\"date\":\"{day}\",\"totalMinutes\":30,\"category\":\"social\"}}]";
        await handler.HandleUsageReportLegacy("AND-001", r1);

        // 第二轮：微信累计涨到 55 分钟（同日重复上报，不应 30+55 累加）
        var r2 = $"[{{\"packageName\":\"com.tencent.mm\",\"appName\":\"微信\",\"date\":\"{day}\",\"totalMinutes\":55,\"category\":\"social\"}}]";
        await handler.HandleUsageReportLegacy("AND-001", r2);

        var rows = await db.UsageRecords
            .Where(r => r.DeviceId == 1 && r.AppPackage == "com.tencent.mm")
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(55 * 60, rows[0].DurationSeconds);

        var summary = await db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == 1);
        Assert.NotNull(summary);
        Assert.Equal(55, summary!.TotalMinutes);   // 不是 85
    }

    /// <summary>
    /// 上报携带重置偏移 → 设备行落库；ack 的已用/剩余为调整后口径
    /// </summary>
    [Fact]
    public async Task HandleUsageReportLegacy_WithResetOffset_UpdatesDeviceAndAck()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var r = $"[{{\"packageName\":\"com.tencent.mm\",\"appName\":\"微信\",\"date\":\"{day}\",\"totalMinutes\":90,\"category\":\"social\"}}]";
        var ack = await handler.HandleUsageReportLegacy("AND-001", r, dailyResetOffsetMinutes: 60, offsetReported: true);

        var device = await db.Devices.FirstAsync(d => d.DeviceId == "AND-001");
        Assert.Equal(60, device.LastResetOffsetMinutes);
        Assert.Equal(day, device.LastResetDate);
        Assert.NotNull(device.LastReportAt);

        // 调整后：90 - 60 = 30 已用，剩余 90
        Assert.Equal(30, ack.TodayTotalMinutes);
        Assert.Equal(90, ack.TodayRemainingMinutes);
        Assert.False(ack.OvertimeLocked);
    }

    /// <summary>
    /// 未上报偏移字段（旧端）→ 不覆盖已有偏移，ack 按原始口径
    /// </summary>
    [Fact]
    public async Task HandleUsageReportLegacy_WithoutOffsetField_KeepsExistingOffset()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var device = await db.Devices.FirstAsync(d => d.DeviceId == "AND-001");
        device.LastResetOffsetMinutes = 50;
        device.LastResetDate = day;
        await db.SaveChangesAsync();

        var r = $"[{{\"packageName\":\"com.tencent.mm\",\"appName\":\"微信\",\"date\":\"{day}\",\"totalMinutes\":90,\"category\":\"social\"}}]";
        var ack = await handler.HandleUsageReportLegacy("AND-001", r); // offsetReported=false

        // 已有偏移保留（90-50=40 已用）
        Assert.Equal(40, ack.TodayTotalMinutes);
        Assert.Equal(80, ack.TodayRemainingMinutes);
    }

    /// <summary>
    /// 记录日期为设备本地日：以批次记录日期聚合汇总（不是 UTC 今日）
    /// </summary>
    [Fact]
    public async Task HandleUsageReportLegacy_UsesRecordLocalDateForSummary()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);

        // 设备本地日期固定为昨天（模拟 00:00-08:00 期间设备本地已跨日）
        var localDay = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var r = $"[{{\"packageName\":\"com.tencent.mm\",\"appName\":\"微信\",\"date\":\"{localDay}\",\"totalMinutes\":20,\"category\":\"social\"}}]";
        var ack = await handler.HandleUsageReportLegacy("AND-001", r);

        var summary = await db.DailySummaries.FirstOrDefaultAsync(s => s.DeviceId == 1);
        Assert.NotNull(summary);
        Assert.Equal(localDay, summary!.SummaryDate);   // 归属设备本地日期
        Assert.Equal(20, ack.TodayTotalMinutes);
    }

    // ==================== 测试用 scope 桩 ====================

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
