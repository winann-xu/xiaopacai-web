using System;
using System.Diagnostics.CodeAnalysis;

namespace XiaopacaiWeb.Services;

/// <summary>
/// [TASK-PRELAUNCH-P4] 时间额度口径统一工具：
/// 1. 服务端统一 Asia/Shanghai（UTC+8）日期口径，避免 00:00–08:00 UTC 跨日错位（需求 7 第 2 条）
/// 2. 调整后今日已用的纯函数计算（可单测）
/// </summary>
[SuppressMessage("Style", "IDE1006", Justification = "工具类统一命名")]
public static class AppClock
{
    private static readonly TimeZoneInfo ShanghaiZone = LoadShanghaiZone();

    private static TimeZoneInfo LoadShanghaiZone()
    {
        try
        {
            // Linux/macOS：IANA 时区 ID；Windows：系统注册表 ID
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
        catch (TimeZoneNotFoundException)
        {
            // 无 tzdata 环境兜底：手工构造固定 +08:00 时区
            return TimeZoneInfo.CreateCustomTimeZone(
                "Asia/Shanghai", TimeSpan.FromHours(8), "Asia/Shanghai (fixed)", "Asia/Shanghai");
        }
    }

    /// <summary>今日日期（Asia/Shanghai 口径，yyyy-MM-dd）</summary>
    public static string TodayShanghai()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ShanghaiZone).ToString("yyyy-MM-dd");
    }

    /// <summary>今日零点（Asia/Shanghai 口径，用于报告默认日期等场景）</summary>
    public static DateTime TodayShanghaiDate()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ShanghaiZone).Date;
    }
}

/// <summary>
/// 调整后今日已用额度计算（纯函数，便于 xunit 覆盖）
/// </summary>
public static class AdjustedUsageCalculator
{
    /// <summary>
    /// 调整后已用 = 原始累计 − 重置偏移（偏移仅当日有效，最小 0）
    /// </summary>
    /// <param name="rawTotalMinutes">原始累计分钟（daily_summary 口径）</param>
    /// <param name="resetOffsetMinutes">设备上报的重置偏移（分钟）</param>
    /// <param name="resetDate">偏移所属日期（yyyy-MM-dd）</param>
    /// <param name="today">查询当日（yyyy-MM-dd，Asia/Shanghai 或设备本地日）</param>
    public static int ComputeAdjusted(int rawTotalMinutes, int resetOffsetMinutes, string? resetDate, string today)
    {
        if (string.IsNullOrEmpty(resetDate) || resetDate != today)
            return Math.Max(0, rawTotalMinutes);
        return Math.Max(0, rawTotalMinutes - resetOffsetMinutes);
    }
}
