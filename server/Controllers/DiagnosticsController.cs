using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;

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
    private readonly IJwtService _jwt;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(AppDbContext db, IJwtService jwt, ILogger<DiagnosticsController> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/diagnostics — 儿童端上报诊断信息
    /// 设备级鉴权（TASK-OPT-12-P4-DEEPEN）：
    /// 1. 设备必须已注册（devices 表存在该 device_id），否则 403；
    /// 2. 设备已配置 DeviceToken 时必须携带有效凭证：
    ///    - Authorization: Bearer 设备级 JWT（scope 含 diagnostics），或
    ///    - 请求体 device_token 与设备存储令牌一致。
    /// </summary>
    [HttpPost("api/diagnostics")]
    [AllowAnonymous]
    public async Task<IActionResult> Submit([FromBody] DiagnosticReportRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "device_id 必填" });

        if (request.DeviceId.Length > 128)
            return BadRequest(new { error = "device_id 过长" });

        // [TASK-OPT-12-P4-DEEPEN] 设备级鉴权：设备必须已注册
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == request.DeviceId);
        if (device == null)
        {
            _logger.LogWarning("[Diagnostics] 未注册设备尝试上报被拒绝: {DeviceId}", request.DeviceId);
            return StatusCode(403, new { error = "设备未注册，拒绝上报" });
        }

        // [TASK-OPT-12-P4-DEEPEN] 设备 Token / 设备 JWT 校验（已配置令牌的设备必须携带有效凭证）
        if (!IsDeviceAuthenticated(request, device))
        {
            _logger.LogWarning("[Diagnostics] 设备令牌校验失败被拒绝: {DeviceId}", request.DeviceId);
            return StatusCode(403, new { error = "设备令牌无效" });
        }

        // [SEC-K9] 诊断数据最小化：JSON 字段类型/尺寸收敛，畸形或超限直接拒绝；
        // NetworkType 白名单外一律丢弃（不落库）
        var permissionStatus = NormalizeDiagnosticsJson(request.PermissionStatus, JsonValueKind.Object, 4096, 0, out var pValid);
        var serviceStatus = NormalizeDiagnosticsJson(request.ServiceStatus, JsonValueKind.Object, 4096, 0, out var sValid);
        var recentCrashes = NormalizeDiagnosticsJson(request.RecentCrashes, JsonValueKind.Array, 16384, 20, out var cValid);
        var p2pHistory = NormalizeDiagnosticsJson(request.P2pHistory, JsonValueKind.Object, 4096, 0, out var hValid);
        if (!pValid || !sValid || !cValid || !hValid)
        {
            _logger.LogWarning("[Diagnostics] 设备 {DeviceId} 上报非法字段被拒绝", request.DeviceId);
            return BadRequest(new { error = "诊断字段格式无效" });
        }

        var networkType = NormalizeNetworkType(request.NetworkType);

        var record = new DiagnosticRecord
        {
            DeviceId = request.DeviceId,
            AppVersion = Truncate(request.AppVersion, 32),
            AndroidVersion = Truncate(request.AndroidVersion, 16),
            DeviceModel = Truncate(request.DeviceModel, 64),
            Manufacturer = Truncate(request.Manufacturer, 64),
            PermissionStatus = permissionStatus,
            ServiceStatus = serviceStatus,
            RecentCrashes = recentCrashes,
            P2pHistory = p2pHistory,
            DbSizeBytes = request.DbSizeBytes,
            NetworkType = networkType,
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

        // [SEC-K10] 诊断数据导出审计（条数/筛选范围）
        await AuditAsync("diagnostics.export", "Diagnostics", null,
            $"{{\"count\":{items.Count},\"deviceId\":\"{deviceId ?? ""}\",\"from\":\"{from ?? ""}\",\"to\":\"{to ?? ""}\"}}");

        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        var fileName = $"diagnostics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
    }

    // ========== 辅助 ==========

    // [TASK-OPT-12-P4-DEEPEN] 设备级鉴权：设备 JWT（Authorization 头）或设备 Token（请求体）任一通过即可
    private bool IsDeviceAuthenticated(DiagnosticReportRequest request, Device device)
    {
        // 1. Authorization: Bearer 设备级 JWT 校验（优先级最高；提供了但无效则直接拒绝，不降级）
        var authHeader = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var jwt = authHeader["Bearer ".Length..].Trim();
            return _jwt.TryValidateDeviceToken(jwt, device.DeviceId, "diagnostics");
        }

        // 2. 请求体 device_token 校验（设备未配置令牌时兼容放行，已配置则必须一致）
        if (string.IsNullOrEmpty(device.DeviceToken))
            return true;

        return !string.IsNullOrEmpty(request.DeviceToken) && request.DeviceToken == device.DeviceToken;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>
    /// [SEC-K10] 审计日志落库（管理端数据导出等安全事件）
    /// </summary>
    private async Task AuditAsync(string action, string? targetType, int? targetId, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = int.TryParse(Security.DeviceAccess.GetUserId(User), out var uid) ? uid : null,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// [SEC-K9] 诊断 JSON 字段收敛：必须为合法 JSON 且根类型匹配，
    /// 尺寸/条目数受限（防恶意设备灌入超大或畸形数据）
    /// </summary>
    private static string? NormalizeDiagnosticsJson(string? value, JsonValueKind expectedKind,
        int maxLength, int maxItems, out bool valid)
    {
        valid = true;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > maxLength)
        {
            valid = false;
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind != expectedKind)
            {
                valid = false;
                return null;
            }
            if (expectedKind == JsonValueKind.Array && root.GetArrayLength() > maxItems)
            {
                valid = false;
                return null;
            }
            return value;
        }
        catch (JsonException)
        {
            valid = false;
            return null;
        }
    }

    /// <summary>
    /// [SEC-K9] 网络类型白名单：wifi/cellular/none，其余值丢弃（不落库）
    /// </summary>
    private static string? NormalizeNetworkType(string? value)
        => value?.ToLowerInvariant() switch
        {
            "wifi" => "wifi",
            "cellular" => "cellular",
            "none" => "none",
            _ => null,
        };
}

// ========== DTOs ==========

/// <summary>
/// 诊断信息上报请求（儿童端 → Web）
/// </summary>
public class DiagnosticReportRequest
{
    /// <summary>儿童端设备唯一标识（必填）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备访问令牌（TASK-OPT-12-P4-DEEPEN：由 /api/devices/{id}/token 生成，设备已配置令牌时必填）</summary>
    public string? DeviceToken { get; set; }

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
