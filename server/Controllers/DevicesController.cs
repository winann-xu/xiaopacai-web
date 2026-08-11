using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 设备管理 API — 列表 / 详情 / 解绑 / 配对码生成 / 手动配对
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize(Policy = "ParentOrAdmin")]
public class DevicesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(AppDbContext db, ILogger<DevicesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/devices — 设备列表（含今日使用汇总）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var devices = await _db.Devices
            .Include(d => d.Policy)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

        var summaries = await _db.DailySummaries
            .Where(s => s.SummaryDate == today)
            .ToDictionaryAsync(s => s.DeviceId);

        var result = devices.Select(d =>
        {
            summaries.TryGetValue(d.Id, out var summary);
            return new
            {
                id = d.Id,
                name = d.DeviceName,
                deviceId = d.DeviceId,
                ipAddress = d.IpAddress,
                osVersion = d.Platform,
                status = d.OnlineStatus,
                lastSeen = d.LastSeenAt,
                certFingerprint = d.CertFingerprint,
                pairedAt = d.UpdatedAt,
                todayUsageMinutes = summary?.TotalMinutes ?? 0,
                todayLimitMinutes = d.Policy?.DailyLimitMinutes ?? 120,
                pairStatus = d.PairStatus,
            };
        });

        return Ok(result);
    }

    /// <summary>
    /// GET /api/devices/{id} — 设备详情
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var device = await _db.Devices
            .Include(d => d.Policy)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var summary = await _db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == device.Id && s.SummaryDate == today);

        return Ok(new
        {
            id = device.Id,
            name = device.DeviceName,
            deviceId = device.DeviceId,
            platform = device.Platform,
            ipAddress = device.IpAddress,
            certFingerprint = device.CertFingerprint,
            pairStatus = device.PairStatus,
            status = device.OnlineStatus,
            lastSeen = device.LastSeenAt,
            isActive = device.IsActive,
            createdAt = device.CreatedAt,
            todayUsageMinutes = summary?.TotalMinutes ?? 0,
            todayLimitMinutes = device.Policy?.DailyLimitMinutes ?? 120,
        });
    }

    /// <summary>
    /// DELETE /api/devices/{id} — 解绑设备（软删除：revoked + 停用）
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Unpair(int id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        device.PairStatus = "revoked";
        device.IsActive = false;
        device.OnlineStatus = "offline";
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("device.unpair", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"name\":\"{device.DeviceName}\"}}");

        _logger.LogInformation("[Devices] 设备已解绑: {DeviceId}", device.DeviceId);
        return Ok(new { message = "设备已解绑" });
    }

    /// <summary>
    /// POST /api/devices/pairing-code — 生成 6 位配对码（5 分钟有效）
    /// </summary>
    [HttpPost("pairing-code")]
    public async Task<IActionResult> GeneratePairingCode()
    {
        var pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAt = DateTime.UtcNow.AddMinutes(5);

        _db.PairingInfos.Add(new PairingInfo
        {
            DeviceId = null,
            PairCode = pairCode,
            PairMethod = "manual",
            PairStatus = "pending",
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await AuditAsync("device.pairing-code", "PairingInfo", null, $"{{\"code\":\"{pairCode}\"}}");

        return Ok(new
        {
            pairCode,
            expiresAt,
            expiresInSeconds = 300,
        });
    }

    /// <summary>
    /// POST /api/devices/pair — 手动配对（输入配对码 + 设备 IP）
    /// </summary>
    [HttpPost("pair")]
    public async Task<IActionResult> Pair([FromBody] ManualPairRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PairingCode))
            return BadRequest(new { error = "配对码不能为空" });

        var pairingInfo = await _db.PairingInfos
            .Where(p => p.PairCode == request.PairingCode && p.PairStatus == "pending")
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        if (pairingInfo == null || pairingInfo.ExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "配对码无效或已过期" });

        // 绑定或创建设备
        Device device;
        if (pairingInfo.DeviceId is > 0)
        {
            device = await _db.Devices.FindAsync(pairingInfo.DeviceId);
            if (device == null)
                return NotFound(new { error = "绑定的设备不存在" });
        }
        else
        {
            device = new Device
            {
                DeviceId = $"XP-{Guid.NewGuid():N}"[..14],
                DeviceName = request.DeviceName ?? "未命名设备",
                Platform = request.Platform ?? "android",
                IpAddress = request.IpAddress,
                PairCode = request.PairingCode,
                PairStatus = "paired",
                OnlineStatus = "offline",
                IsActive = true,
            };
            _db.Devices.Add(device);
            await _db.SaveChangesAsync();

            _db.Policies.Add(new Policy
            {
                DeviceId = device.Id,
                DailyLimitMinutes = 120,
                OvertimeAction = "full_lock",
            });
        }

        pairingInfo.DeviceId = device.Id;
        pairingInfo.PairStatus = "confirmed";
        pairingInfo.ConfirmedAt = DateTime.UtcNow;

        device.PairCode = request.PairingCode;
        device.PairStatus = "paired";
        device.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(request.IpAddress))
            device.IpAddress = request.IpAddress;

        await _db.SaveChangesAsync();

        await AuditAsync("device.pair", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"code\":\"{request.PairingCode}\"}}");

        return Ok(new
        {
            deviceId = device.DeviceId,
            id = device.Id,
            name = device.DeviceName,
            pairStatus = device.PairStatus,
        });
    }

    // ========== helpers ==========

    private async Task AuditAsync(string action, string? targetType, int? targetId, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>
/// 手动配对请求
/// </summary>
public class ManualPairRequest
{
    public string PairingCode { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
}
