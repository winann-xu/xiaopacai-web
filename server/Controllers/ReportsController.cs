using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 使用报告 API — 日报 / 周报 / 导出（TXT/JSON/CSV）
/// [TASK-PRELAUNCH-P2] 重构：真实数据源 usage_records，按应用聚合 + 动态分类（回归终端分类），
/// 时段分布按时长（分钟）统计，导出复用聚合逻辑，报告为原始累计口径
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = "ParentOrAdmin")]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/reports/daily?deviceId=&amp;date= — 日报
    /// </summary>
    [HttpGet("daily")]
    public async Task<IActionResult> Daily(int? deviceId, string? date)
    {
        var day = DateTime.TryParse(date, out var parsed) ? parsed.Date : DateTime.UtcNow.Date;

        var (records, _) = await QueryRecords(deviceId, day.AddDays(-1), day.AddDays(2));
        var dayRecords = records.Where(r => r.StartTime.ToString("yyyy-MM-dd") == day.ToString("yyyy-MM-dd")).ToList();
        var prevDayRecords = records.Where(r => r.StartTime.ToString("yyyy-MM-dd") == day.AddDays(-1).ToString("yyyy-MM-dd")).ToList();

        var totals = ReportAggregator.Totals(dayRecords);
        var (prevTotal, _) = ReportAggregator.Totals(prevDayRecords);

        // 限额：所选设备策略合计（报告展示"剩余额度"参考值）
        var limitMinutes = await GetLimitMinutes(deviceId);
        var remainingMinutes = limitMinutes > 0 ? Math.Max(0, limitMinutes - totals.TotalMinutes) : (int?)null;

        // 设备明细（保留，与设备页同源 raw 口径）
        var devices = await _db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.DeviceName })
            .ToListAsync();
        var byDevice = dayRecords
            .GroupBy(r => r.DeviceId)
            .Select(g => new
            {
                deviceId = g.Key,
                deviceName = devices.FirstOrDefault(d => d.Id == g.Key)?.DeviceName ?? "未知设备",
                totalMinutes = g.Sum(r => r.DurationSeconds) / 60,
                blockCount = g.Count(r => r.IsBlocked),
            })
            .OrderByDescending(x => x.totalMinutes)
            .ToList();

        return Ok(new
        {
            date = day.ToString("yyyy-MM-dd"),
            totalMinutes = totals.TotalMinutes,
            limitMinutes,
            remainingMinutes,
            // 报告为原始累计口径（含重置前用量），与设备页调整后口径的区分见需求 7
            rawAccumulated = true,
            categories = ReportAggregator.AggregateByCategory(dayRecords),
            topApps = ReportAggregator.AggregateByApp(dayRecords, 10),
            hourlyData = ReportAggregator.HourlyDistribution(dayRecords),
            blockCount = totals.BlockCount,
            overtimeCount = 0, // 服务端暂无超时事件流，保留字段待 P4 接入
            events = ReportAggregator.BlockEvents(dayRecords),
            previousDayTotalMinutes = prevTotal,
            byDevice,
        });
    }

    /// <summary>
    /// GET /api/reports/weekly?deviceId=&amp;weekStart= — 周报（7 天，含环比上周）
    /// </summary>
    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly(int? deviceId, string? weekStart)
    {
        DateTime start;
        if (!string.IsNullOrEmpty(weekStart) && DateTime.TryParse(weekStart, out var parsed))
            start = parsed.Date;
        else
            start = DateTime.UtcNow.Date.AddDays(-6);

        var (records, _) = await QueryRecords(deviceId, start.AddDays(-7), start.AddDays(14));

        var days = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).ToList();
        var prevDays = Enumerable.Range(0, 7).Select(i => start.AddDays(i - 7)).ToList();

        // 每日：总时长 + 拦截次数
        var dailyDetails = days.Select(d =>
        {
            var rs = records.Where(r => r.StartTime.ToString("yyyy-MM-dd") == d.ToString("yyyy-MM-dd")).ToList();
            var (total, blocks) = ReportAggregator.Totals(rs);
            return new
            {
                date = d.ToString("yyyy-MM-dd"),
                totalMinutes = total,
                blockCount = blocks,
            };
        }).ToList();

        var weekRecords = records.Where(r =>
            r.StartTime.ToString("yyyy-MM-dd").CompareTo(days[0].ToString("yyyy-MM-dd")) >= 0 &&
            r.StartTime.ToString("yyyy-MM-dd").CompareTo(start.AddDays(7).ToString("yyyy-MM-dd")) < 0).ToList();
        var prevWeekRecords = records.Where(r =>
            r.StartTime.ToString("yyyy-MM-dd").CompareTo(prevDays[0].ToString("yyyy-MM-dd")) >= 0 &&
            r.StartTime.ToString("yyyy-MM-dd").CompareTo(start.ToString("yyyy-MM-dd")) < 0).ToList();

        var (weekTotal, weekBlocks) = ReportAggregator.Totals(weekRecords);
        var (prevWeekTotal, _) = ReportAggregator.Totals(prevWeekRecords);

        var limitMinutes = await GetLimitMinutes(deviceId);

        return Ok(new
        {
            weekStart = days[0].ToString("yyyy-MM-dd"),
            weekEnd = days[6].ToString("yyyy-MM-dd"),
            totalMinutes = weekTotal,
            limitMinutes,
            prevWeekTotalMinutes = prevWeekTotal,
            dailyTotals = dailyDetails.Select(x => x.totalMinutes).ToList(),
            dailyDetails,
            dates = days.Select(d => d.ToString("MM/dd")).ToList(),
            categories = ReportAggregator.AggregateByCategory(weekRecords),
            topApps = ReportAggregator.AggregateByApp(weekRecords, 10),
            blockCount = weekBlocks,
            overtimeCount = 0,
        });
    }

    /// <summary>
    /// GET /api/reports/export?format=txt|json|csv&amp;deviceId=&amp;from=&amp;to= — 导出真实数据
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(string format = "json", int? deviceId = null,
        string? from = null, string? to = null)
    {
        var startDate = DateTime.TryParse(from, out var f) ? f.Date : DateTime.UtcNow.Date.AddDays(-29);
        var endDate = DateTime.TryParse(to, out var t) ? t.Date : DateTime.UtcNow.Date;

        if (endDate < startDate)
            return BadRequest(new { error = "结束日期不能早于开始日期" });
        if ((endDate - startDate).TotalDays > 366)
            return BadRequest(new { error = "导出范围不能超过一年" });

        var (records, _) = await QueryRecords(deviceId, startDate, endDate.AddDays(2));
        var devices = await _db.Devices.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.DeviceName);

        // 逐日聚合
        var dailyRows = new List<ExportDay>();
        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            var rs = records.Where(r => r.StartTime.ToString("yyyy-MM-dd") == d.ToString("yyyy-MM-dd")).ToList();
            var (total, blocks) = ReportAggregator.Totals(rs);
            dailyRows.Add(new ExportDay
            {
                Date = d.ToString("yyyy-MM-dd"),
                TotalMinutes = total,
                BlockCount = blocks,
                Categories = ReportAggregator.AggregateByCategory(rs),
                TopApps = ReportAggregator.AggregateByApp(rs, 10),
                Records = rs,
            });
        }

        format = (format ?? "json").ToLowerInvariant();
        var filename = $"xiaopacai-report-{startDate:yyyyMMdd}-{endDate:yyyyMMdd}";
        return format switch
        {
            "txt" => File(Encoding.UTF8.GetBytes(BuildTxt(dailyRows, devices, startDate, endDate)), "text/plain; charset=utf-8", $"{filename}.txt"),
            "csv" => File(Encoding.UTF8.GetBytes(BuildCsv(dailyRows, devices)), "text/csv; charset=utf-8", $"{filename}.csv"),
            _ => File(Encoding.UTF8.GetBytes(BuildJson(dailyRows, devices)), "application/json; charset=utf-8", $"{filename}.json"),
        };
    }

    // ========== helpers ==========

    /// <summary>
    /// 拉取指定设备（或全部）在时间窗内的 usage_records（设备名一并返回）
    /// </summary>
    private async Task<(List<UsageRecord> Records, Dictionary<int, string> Devices)> QueryRecords(
        int? deviceId, DateTime fromUtc, DateTime toUtc)
    {
        var query = _db.UsageRecords.AsNoTracking()
            .Where(r => r.StartTime >= fromUtc && r.StartTime < toUtc);
        if (deviceId is > 0)
            query = query.Where(r => r.DeviceId == deviceId);

        var records = await query.ToListAsync();
        var devices = await _db.Devices.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.DeviceName);
        return (records, devices);
    }

    /// <summary>
    /// 报告参考限额：单设备取其策略值，全部设备取策略合计
    /// </summary>
    private async Task<int> GetLimitMinutes(int? deviceId)
    {
        var policiesQuery = _db.Policies.AsNoTracking();
        if (deviceId is > 0)
            policiesQuery = policiesQuery.Where(p => p.DeviceId == deviceId);
        var limits = await policiesQuery.Select(p => (int?)p.DailyLimitMinutes).ToListAsync();
        return limits.Where(l => l.HasValue).Sum(l => l!.Value);
    }

    private class ExportDay
    {
        public string Date { get; set; } = string.Empty;
        public int TotalMinutes { get; set; }
        public int BlockCount { get; set; }
        public List<ReportAggregator.CategoryStat> Categories { get; set; } = new();
        public List<ReportAggregator.AppStat> TopApps { get; set; } = new();
        public List<UsageRecord> Records { get; set; } = new();
    }

    private static string BuildJson(List<ExportDay> days, Dictionary<int, string> devices)
    {
        var payload = days.Select(d => new
        {
            date = d.Date,
            totalMinutes = d.TotalMinutes,
            blockCount = d.BlockCount,
            categories = d.Categories.Select(c => new { key = c.Key, name = c.Name, minutes = c.Minutes, percent = c.Percent }),
            topApps = d.TopApps.Select(a => new { packageName = a.PackageName, appName = a.AppName, category = a.Category, minutes = a.Minutes }),
        });
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildCsv(List<ExportDay> days, Dictionary<int, string> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,totalMinutes,blockCount,categories,topApps");
        foreach (var d in days)
        {
            var cats = string.Join(" | ", d.Categories.Select(c => $"{c.Name}({c.Key}):{c.Minutes}"));
            var apps = string.Join(" | ", d.TopApps.Select(a => $"{a.AppName}({a.PackageName}):{a.Minutes}"));
            sb.AppendLine($"{d.Date},{d.TotalMinutes},{d.BlockCount},{EscapeCsv(cats)},{EscapeCsv(apps)}");
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string s)
        => $"\"{s.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// TXT 可读模板（复用 Android ReportGenerator 文本风格：逐日总览 + 分类 + Top 应用）
    /// </summary>
    private static string BuildTxt(List<ExportDay> days, Dictionary<int, string> devices,
        DateTime start, DateTime end)
    {
        var sb = new StringBuilder();
        sb.AppendLine("小趴菜 · 使用报告（原始累计口径）");
        sb.AppendLine($"导出范围：{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（共 {(end - start).TotalDays + 1:0} 天）");
        sb.AppendLine("注：报告为原始累计（含重置前用量），与设备页调整后口径可能不同");
        sb.AppendLine(new string('=', 60));

        foreach (var d in days)
        {
            sb.AppendLine();
            sb.AppendLine($"【{d.Date}】");
            sb.AppendLine($"  总使用时长：{d.TotalMinutes} 分钟（{d.TotalMinutes / 60.0:0.0} 小时）");
            if (d.Categories.Count > 0)
            {
                var catText = string.Join(" · ", d.Categories.Select(c => $"{c.Name} {c.Minutes}min（{c.Percent:0.0}%）"));
                sb.AppendLine($"  分类：{catText}");
            }
            else
            {
                sb.AppendLine("  分类：（无使用记录）");
            }
            if (d.TopApps.Count > 0)
            {
                sb.AppendLine("  Top 应用：");
                for (var i = 0; i < d.TopApps.Count; i++)
                {
                    var a = d.TopApps[i];
                    sb.AppendLine($"    {i + 1,2}. {a.AppName}（{a.PackageName}）{ReportAggregator.CategoryDisplayName(a.Category)} {a.Minutes} 分钟");
                }
            }
            sb.AppendLine($"  拦截次数：{d.BlockCount}");
        }

        sb.AppendLine();
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("— 小趴菜 · 开源免费 · 本地优先 · 数据不上云");
        return sb.ToString();
    }
}
