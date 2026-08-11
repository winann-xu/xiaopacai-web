using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 故障诊断收集 REST API（OPT12 需求 5）
///
/// - POST /api/diagnostics — 儿童端上报诊断信息（每天一次 / 异常立即补报）
/// - GET  /api/admin/diagnostics — 管理端列表 / 筛选
/// - GET  /api/admin/diagnostics/export — 管理端导出（JSON）
/// </summary>
[ApiController]
public class DiagnosticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(AppDbContext db, ILogger<DiagnosticsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/diagnostics — 儿童端上报诊断信息
    /// 儿童端无 JWT，走 P2P 证书链路 / 未鉴权 HTTP；TODO(P4 安全审查)：接入设备级 Token 校验
    /// </summary>
    [HttpPost("api/diagnostics")]
    [AllowAnonymous]
    public async Task<IActionResult> Submit([FromBody] DiagnosticReportRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "device_id 必填" });

        if (request.DeviceId.Length > 128)
            return BadRequest(new { error = "device_id 过长" });

        var record = new DiagnosticRecord
        {
            DeviceId = request.DeviceId,
            AppVersion = Truncate(request.AppVersion, 32),
            AndroidVersion = Truncate(request.AndroidVersion, 16),
            DeviceModel = Truncate(request.DeviceModel, 64),
            Manufacturer = Truncate(request.Manufacturer, 64),
            PermissionStatus = request.PermissionStatus,
            ServiceStatus = request.ServiceStatus,
            RecentCrashes = request.RecentCrashes,
            P2pHistory = request.P2pHistory,
            DbSizeBytes = request.DbSizeBytes,
            NetworkType = Truncate(request.NetworkType, 16),
            ReportedAt = DateTime.UtcNow,
        };

        _db.Diagnostics.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Diagnostics] 设备 {DeviceId} 上报诊断, app={AppVer}, android={AndroidVer}",
            record.DeviceId, record.AppVersion, record.AndroidVersion);

        return Ok(new
        {
            id = record.Id,
            receivedAt = record.ReportedAt,
        });
    }

    /// <summary>
    /// GET /api/admin/diagnostics — 管理端列表 / 筛选
    /// 查询参数：deviceId（按设备筛）、from / to（上报时间范围，ISO8601）、limit（默认 50，上限 200）
    /// </summary>
    [HttpGet("api/admin/diagnostics")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> List(
        [FromQuery] string? deviceId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.Diagnostics.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(d => d.DeviceId == deviceId);

        if (DateTime.TryParse(from, out var fromTime))
            query = query.Where(d => d.ReportedAt >= fromTime);
        if (DateTime.TryParse(to, out var toTime))
            query = query.Where(d => d.ReportedAt <= toTime);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(d => d.ReportedAt)
            .Take(limit)
            .Select(d => new
            {
                d.Id,
                d.DeviceId,
                d.AppVersion,
                d.AndroidVersion,
                d.DeviceModel,
                d.Manufacturer,
                d.PermissionStatus,
                d.ServiceStatus,
                d.RecentCrashes,
                d.P2pHistory,
                d.DbSizeBytes,
                d.NetworkType,
                d.ReportedAt,
            })
            .ToListAsync();

        return Ok(new
        {
            total,
            limit,
            items,
        });
    }

    /// <summary>
    /// GET /api/admin/diagnostics/export — 管理端导出（JSON 文件下载）
    /// </summary>
    [HttpGet("api/admin/diagnostics/export")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Export(
        [FromQuery] string? deviceId,
        [FromQuery] string? from,
        [FromQuery] string? to)
    {
        var query = _db.Diagnostics.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(d => d.DeviceId == deviceId);

        if (DateTime.TryParse(from, out var fromTime))
            query = query.Where(d => d.ReportedAt >= fromTime);
        if (DateTime.TryParse(to, out var toTime))
            query = query.Where(d => d.ReportedAt <= toTime);

        var items = await query
            .OrderBy(d => d.ReportedAt)
            .Select(d => new
            {
                d.Id,
                d.DeviceId,
                d.AppVersion,
                d.AndroidVersion,
                d.DeviceModel,
                d.Manufacturer,
                d.PermissionStatus,
                d.ServiceStatus,
                d.RecentCrashes,
                d.P2pHistory,
                d.DbSizeBytes,
                d.NetworkType,
                d.ReportedAt,
            })
            .ToListAsync();

        _logger.LogInformation("[Diagnostics] 管理端导出诊断数据 {Count} 条", items.Count);

        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        var fileName = $"diagnostics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
    }

    // ========== 辅助 ==========

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

// ========== DTOs ==========

/// <summary>
/// 诊断信息上报请求（儿童端 → Web）
/// </summary>
public class DiagnosticReportRequest
{
    /// <summary>儿童端设备唯一标识（必填）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>儿童端 APP 版本号</summary>
    public string? AppVersion { get; set; }

    /// <summary>Android 系统版本号</summary>
    public string? AndroidVersion { get; set; }

    /// <summary>设备型号</summary>
    public string? DeviceModel { get; set; }

    /// <summary>设备厂商</summary>
    public string? Manufacturer { get; set; }

    /// <summary>权限状态（JSON 对象：无障碍/用量/设备管理器/通知/电池优化）</summary>
    public string? PermissionStatus { get; set; }

    /// <summary>服务运行状态（JSON 对象：守护服务/无障碍服务）</summary>
    public string? ServiceStatus { get; set; }

    /// <summary>最近崩溃堆栈（JSON 数组，最近 5 条）</summary>
    public string? RecentCrashes { get; set; }

    /// <summary>P2P 连接历史（JSON 对象：成功/失败/重连次数）</summary>
    public string? P2pHistory { get; set; }

    /// <summary>本地数据库大小（字节）</summary>
    public long? DbSizeBytes { get; set; }

    /// <summary>网络状态：wifi | cellular | none</summary>
    public string? NetworkType { get; set; }
}
