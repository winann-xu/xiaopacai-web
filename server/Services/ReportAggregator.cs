using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Services;

/// <summary>
/// [TASK-PRELAUNCH-P2] 报告聚合器（纯函数，便于单元测试）
/// 数据源：usage_records（含 app_package/app_name/category/duration_seconds/is_blocked）
/// 分类口径：以终端上报的 category 实际值为准（动态分类，不写死四类），study 归一为 learning
/// </summary>
public static class ReportAggregator
{
    public class CategoryStat
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Minutes { get; set; }
        public double Percent { get; set; }
    }

    public class AppStat
    {
        public string PackageName { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Minutes { get; set; }
    }

    /// <summary>
    /// 分类归一：study → learning（兼容旧口径），空 → other，其余保留终端原值（支持细分类）
    /// </summary>
    public static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "other";
        var c = category.Trim().ToLowerInvariant();
        return c == "study" ? "learning" : c;
    }

    /// <summary>
    /// 分类展示名：标准五类映射中文，未知分类显示原名
    /// </summary>
    public static string CategoryDisplayName(string category) => category switch
    {
        "game" => "游戏",
        "social" => "社交",
        "video" => "视频",
        "learning" => "学习",
        "other" => "其他",
        _ => category,
    };

    /// <summary>
    /// 按分类动态聚合（分钟，降序，含占比）
    /// </summary>
    public static List<CategoryStat> AggregateByCategory(List<UsageRecord> records)
    {
        var buckets = records
            .GroupBy(r => NormalizeCategory(r.Category))
            .Select(g => new CategoryStat
            {
                Key = g.Key,
                Name = CategoryDisplayName(g.Key),
                Minutes = g.Sum(r => r.DurationSeconds) / 60,
            })
            .OrderByDescending(c => c.Minutes)
            .ToList();

        var total = buckets.Sum(b => b.Minutes);
        foreach (var b in buckets)
            b.Percent = total > 0 ? Math.Round(b.Minutes * 100.0 / total, 1) : 0;

        return buckets;
    }

    /// <summary>
    /// 按应用聚合 Top-N（分钟，降序）
    /// </summary>
    public static List<AppStat> AggregateByApp(List<UsageRecord> records, int topN = 10)
    {
        return records
            .GroupBy(r => new { r.AppPackage, r.AppName })
            .Select(g => new AppStat
            {
                PackageName = g.Key.AppPackage,
                AppName = string.IsNullOrWhiteSpace(g.Key.AppName) ? g.Key.AppPackage : g.Key.AppName,
                Category = NormalizeCategory(g.First().Category),
                Minutes = g.Sum(r => r.DurationSeconds) / 60,
            })
            .OrderByDescending(a => a.Minutes)
            .ThenBy(a => a.AppName)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// 24 小时时段分布（按使用分钟，非记录条数）
    /// </summary>
    public static int[] HourlyDistribution(List<UsageRecord> records)
    {
        var hourly = new int[24];
        foreach (var r in records)
        {
            var hour = r.StartTime.Hour;
            if (hour < 0 || hour > 23) continue;
            hourly[hour] += r.DurationSeconds / 60;
        }
        return hourly;
    }

    /// <summary>
    /// 拦截事件（is_blocked 记录，按时间倒序取 limit 条）
    /// </summary>
    public static List<object> BlockEvents(List<UsageRecord> records, int limit = 20)
    {
        return records
            .Where(r => r.IsBlocked)
            .OrderByDescending(r => r.StartTime)
            .Take(limit)
            .Select(r => (object)new
            {
                time = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                appName = string.IsNullOrWhiteSpace(r.AppName) ? r.AppPackage : r.AppName,
                category = CategoryDisplayName(NormalizeCategory(r.Category)),
            })
            .ToList();
    }

    /// <summary>
    /// 统计汇总：总分钟 / 拦截次数
    /// </summary>
    public static (int TotalMinutes, int BlockCount) Totals(List<UsageRecord> records)
    {
        return (
            records.Sum(r => r.DurationSeconds) / 60,
            records.Count(r => r.IsBlocked));
    }
}
