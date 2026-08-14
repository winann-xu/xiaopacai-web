using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Services;

/// <summary>
/// [TASK-PRELAUNCH-P2] 报告聚合器测试
///
/// 覆盖：
/// - 动态分类聚合（不写死四类：细分类保留、study→learning、空→other、占比计算）
/// - 按应用 Top-N 聚合与排序
/// - 24 小时时段分布（按时长分钟，非记录条数）
/// - 拦截事件提取
/// - 总计汇总
/// </summary>
public class ReportAggregatorTests
{
    private static UsageRecord Rec(int deviceId, string category, string package, string app,
        int durationSeconds, int hour = 10, bool blocked = false)
        => new()
        {
            DeviceId = deviceId,
            Category = category,
            AppPackage = package,
            AppName = app,
            DurationSeconds = durationSeconds,
            StartTime = new DateTime(2026, 8, 14, hour, 0, 0, DateTimeKind.Utc),
            IsBlocked = blocked,
        };

    // ==================== 分类归一 ====================

    [Theory]
    [InlineData("study", "learning")]
    [InlineData("STUDY", "learning")]
    [InlineData("Learning", "learning")]
    [InlineData("game", "game")]
    [InlineData("short_video", "short_video")] // 细分类保留
    [InlineData("browser", "browser")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    [InlineData("  ", "other")]
    public void NormalizeCategory_MapsStudyAndPreservesDynamic(string? input, string expected)
    {
        Assert.Equal(expected, ReportAggregator.NormalizeCategory(input));
    }

    // ==================== 动态分类聚合 ====================

    [Fact]
    public void AggregateByCategory_DynamicCategoriesAndPercent()
    {
        var records = new List<UsageRecord>
        {
            Rec(1, "learning", "a", "学习应用", 60 * 60),        // 60 min
            Rec(1, "study", "b", "旧口径学习", 30 * 60),         // → learning，30 min
            Rec(1, "short_video", "c", "短视频", 20 * 60),       // 细分类，20 min
            Rec(1, "game", "d", "游戏", 10 * 60),                // 10 min
        };

        var result = ReportAggregator.AggregateByCategory(records);

        Assert.Equal(3, result.Count);
        Assert.Equal("learning", result[0].Key);
        Assert.Equal("学习", result[0].Name);
        Assert.Equal(90, result[0].Minutes);                     // 60 + 30（study 归一）
        Assert.Equal(75.0, result[0].Percent);                   // 90/120
        Assert.Equal("short_video", result[1].Key);
        Assert.Equal("short_video", result[1].Name);             // 未知分类显示原名
        Assert.Equal(20, result[1].Minutes);
        Assert.Equal("game", result[2].Key);
        Assert.Equal(10, result[2].Minutes);
        Assert.Equal(8.3, result[2].Percent);                    // 10/120 保留 1 位小数
    }

    [Fact]
    public void AggregateByCategory_EmptyRecords_ReturnsEmpty()
    {
        Assert.Empty(ReportAggregator.AggregateByCategory(new List<UsageRecord>()));
    }

    // ==================== 应用 Top-N ====================

    [Fact]
    public void AggregateByApp_TopN_SortedDescending()
    {
        var records = new List<UsageRecord>
        {
            Rec(1, "game", "com.a", "A", 30 * 60),
            Rec(1, "game", "com.a", "A", 20 * 60),               // A 合计 50
            Rec(1, "video", "com.b", "B", 40 * 60),
            Rec(1, "social", "com.c", "C", 10 * 60),
            Rec(1, "social", "com.d", "", 25 * 60),              // AppName 空 → 回退包名
        };

        var result = ReportAggregator.AggregateByApp(records, topN: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(50, result[0].Minutes);
        Assert.Equal("com.a", result[0].PackageName);
        Assert.Equal(40, result[1].Minutes);
        Assert.Equal("com.d", result[2].AppName);                // 空名回退
        Assert.Equal(25, result[2].Minutes);
    }

    // ==================== 时段分布 ====================

    [Fact]
    public void HourlyDistribution_UsesMinutesNotRecordCount()
    {
        var records = new List<UsageRecord>
        {
            Rec(1, "game", "a", "A", 30 * 60, hour: 9),          // 09 点 30 分钟
            Rec(1, "game", "b", "B", 45 * 60, hour: 9),          // 09 点 45 分钟
            Rec(1, "game", "c", "C", 60, hour: 23),              // 23 点 1 分钟
        };

        var hourly = ReportAggregator.HourlyDistribution(records);

        Assert.Equal(24, hourly.Length);
        Assert.Equal(75, hourly[9]);                             // 按分钟合计，而非 2 条
        Assert.Equal(1, hourly[23]);
        Assert.Equal(0, hourly[0]);
    }

    // ==================== 拦截事件 ====================

    [Fact]
    public void BlockEvents_OnlyBlockedRecords()
    {
        var records = new List<UsageRecord>
        {
            Rec(1, "game", "a", "A", 60, blocked: true),
            Rec(1, "game", "b", "B", 60, blocked: false),
            Rec(1, "game", "c", "C", 60, blocked: true),
        };

        var events = ReportAggregator.BlockEvents(records);

        Assert.Equal(2, events.Count);
    }

    // ==================== 总计 ====================

    [Fact]
    public void Totals_SumsMinutesAndBlocks()
    {
        var records = new List<UsageRecord>
        {
            Rec(1, "game", "a", "A", 60 * 60, blocked: true),
            Rec(1, "game", "b", "B", 30 * 60, blocked: false),
        };

        var (total, blocks) = ReportAggregator.Totals(records);

        Assert.Equal(90, total);
        Assert.Equal(1, blocks);
    }
}
