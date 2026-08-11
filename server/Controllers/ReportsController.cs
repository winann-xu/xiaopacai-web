using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 使用报告 API — 日报 / 周报 / 导出（TXT/JSON/CSV）
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
        var day = date ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (!DateTime.TryParse(day, out var parsedDay))
            return BadRequest(new { error = "日期格式无效，应为 YYYY-MM-DD" });
        var dayStr = parsedDay.ToString("yyyy-MM-dd");

        var query = _db.DailySummaries.AsNoTracking().Where(s => s.SummaryDate == dayStr);
        if (deviceId is > 0)
            query = query.Where(s => s.DeviceId == deviceId);

        var summaries = await query.ToListAsync();
        var devices = await _db.Devices.AsNoTracking().Select(d => new { d.Id, d.DeviceName }).ToListAsync();

        var categoryTotals = new Dictionary<string, int>
        {
            ["learning"] = summaries.Sum(s => s.LearningMinutes),
            ["video"] = summaries.Sum(s => s.VideoMinutes),
            ["social"] = summaries.Sum(s => s.SocialMinutes),
            ["game"] = summaries.Sum(s => s.GameMinutes),
            ["other"] = summaries.Sum(s => s.OtherMinutes),
        };

        // 按时段分布（基于原始记录）
        var hourly = new int[24];
        var recordsQuery = _db.UsageRecords.AsNoTracking()
            .Where(r => r.StartTime >= parsedDay.Date && r.StartTime < parsedDay.Date.AddDays(1));
        if (deviceId is > 0)
            recordsQuery = recordsQuery.Where(r => r.DeviceId == deviceId);
        var records = await recordsQuery.Select(r => r.StartTime).ToListAsync();
        foreach (var t in records)
            hourly[t.Hour] += 1;

        var categoryNames = new Dictionary<string, string>
        {
            ["learning"] = "学习", ["video"] = "视频", ["social"] = "社交",
            ["game"] = "游戏", ["other"] = "其他",
        };

        return Ok(new
        {
            date = dayStr,
            totalMinutes = summaries.Sum(s => s.TotalMinutes),
            categories = categoryTotals
                .Where(kv => kv.Value > 0)
                .Select(kv => new { name = categoryNames[kv.Key], key = kv.Key, minutes = kv.Value })
                .OrderByDescending(c => c.minutes),
            hourlyData = hourly,
            overtimeCount = summaries.Sum(s => s.OvertimeCount),
            blockCount = summaries.Sum(s => s.BlockCount),
            deviceCount = summaries.Count,
            byDevice = summaries.Select(s => new
            {
                deviceId = s.DeviceId,
                deviceName = devices.FirstOrDefault(d => d.Id == s.DeviceId)?.DeviceName ?? "未知设备",
                totalMinutes = s.TotalMinutes,
                gameMinutes = s.GameMinutes,
                videoMinutes = s.VideoMinutes,
                socialMinutes = s.SocialMinutes,
                learningMinutes = s.LearningMinutes,
                otherMinutes = s.OtherMinutes,
            }),
        });
    }

    /// <summary>
    /// GET /api/reports/weekly?deviceId=&amp;weekStart= — 周报（7 天）
    /// </summary>
    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly(int? deviceId, string? weekStart)
    {
        DateTime start;
        if (!string.IsNullOrEmpty(weekStart) && DateTime.TryParse(weekStart, out var parsed))
            start = parsed.Date;
        else
            start = DateTime.UtcNow.Date.AddDays(-6);

        var end = start.AddDays(7);
        var query = _db.DailySummaries.AsNoTracking()
            .Where(s => s.SummaryDate.CompareTo(start.ToString("yyyy-MM-dd")) >= 0
                     && s.SummaryDate.CompareTo(end.ToString("yyyy-MM-dd")) < 0);
        if (deviceId is > 0)
            query = query.Where(s => s.DeviceId == deviceId);

        var summaries = await query.ToListAsync();
        var days = Enumerable.Range(0, 7)
            .Select(i => start.AddDays(i).ToString("yyyy-MM-dd"))
            .ToList();

        var dailyTotals = days.Select(d =>
            summaries.Where(s => s.SummaryDate == d).Sum(s => s.TotalMinutes)).ToList();

        return Ok(new
        {
            weekStart = days[0],
            weekEnd = days[6],
            totalMinutes = dailyTotals.Sum(),
            dailyTotals,
            dates = days.Select(d => d[5..]),
            byDevice = summaries.GroupBy(s => s.DeviceId).Select(g => new
            {
                deviceId = g.Key,
                totalMinutes = g.Sum(s => s.TotalMinutes),
                gameMinutes = g.Sum(s => s.GameMinutes),
                videoMinutes = g.Sum(s => s.VideoMinutes),
                socialMinutes = g.Sum(s => s.SocialMinutes),
                learningMinutes = g.Sum(s => s.LearningMinutes),
            }),
        });
    }

    /// <summary>
    /// GET /api/reports/export?format=txt|json|csv — 导出报告
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(string format = "json", int? deviceId = null,
        string? from = null, string? to = null)
    {
        var startDate = DateTime.TryParse(from, out var f) ? f.Date : DateTime.UtcNow.Date.AddDays(-29);
        var endDate = DateTime.TryParse(to, out var t) ? t.Date.AddDays(1) : DateTime.UtcNow.Date.AddDays(1);

        var query = _db.DailySummaries.AsNoTracking()
            .Where(s => s.SummaryDate.CompareTo(startDate.ToString("yyyy-MM-dd")) >= 0
                     && s.SummaryDate.CompareTo(endDate.ToString("yyyy-MM-dd")) < 0);
        if (deviceId is > 0)
            query = query.Where(s => s.DeviceId == deviceId);

        var summaries = await query
            .OrderBy(s => s.SummaryDate)
            .ToListAsync();

        var devices = await _db.Devices.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.DeviceName);

        format = (format ?? "json").ToLowerInvariant();
        return format switch
        {
            "txt" => File(Encoding.UTF8.GetBytes(BuildTxt(summaries, devices)), "text/plain; charset=utf-8", "report.txt"),
            "csv" => File(Encoding.UTF8.GetBytes(BuildCsv(summaries, devices)), "text/csv; charset=utf-8", "report.csv"),
            _ => File(Encoding.UTF8.GetBytes(BuildJson(summaries, devices)), "application/json; charset=utf-8", "report.json"),
        };
    }

    // ========== helpers ==========

    private static string BuildJson(List<Models.DailySummary> summaries, Dictionary<int, string> devices)
    {
        var payload = summaries.Select(s => new
        {
            date = s.SummaryDate,
            deviceId = s.DeviceId,
            deviceName = devices.GetValueOrDefault(s.DeviceId, "未知"),
            totalMinutes = s.TotalMinutes,
            gameMinutes = s.GameMinutes,
            videoMinutes = s.VideoMinutes,
            socialMinutes = s.SocialMinutes,
            learningMinutes = s.LearningMinutes,
            otherMinutes = s.OtherMinutes,
            blockCount = s.BlockCount,
        });
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildCsv(List<Models.DailySummary> summaries, Dictionary<int, string> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,deviceId,deviceName,totalMinutes,gameMinutes,videoMinutes,socialMinutes,learningMinutes,otherMinutes,blockCount");
        foreach (var s in summaries)
        {
            sb.AppendLine($"{s.SummaryDate},{s.DeviceId},{devices.GetValueOrDefault(s.DeviceId, "未知")}," +
                          $"{s.TotalMinutes},{s.GameMinutes},{s.VideoMinutes},{s.SocialMinutes},{s.LearningMinutes},{s.OtherMinutes},{s.BlockCount}");
        }
        return sb.ToString();
    }

    private static string BuildTxt(List<Models.DailySummary> summaries, Dictionary<int, string> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("小趴菜 Web 3.0 使用报告");
        sb.AppendLine("=".PadRight(60, '='));
        foreach (var s in summaries)
        {
            sb.AppendLine($"{s.SummaryDate}  {devices.GetValueOrDefault(s.DeviceId, "未知")}: " +
                          $"总时长 {s.TotalMinutes} 分钟 (游戏{s.GameMinutes}/视频{s.VideoMinutes}/社交{s.SocialMinutes}/学习{s.LearningMinutes}/其他{s.OtherMinutes})");
        }
        return sb.ToString();
    }
}
