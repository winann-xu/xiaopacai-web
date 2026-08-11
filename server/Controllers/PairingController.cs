using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 设备配对 REST API — 配对码生成/校验/取消、设备绑定管理
/// </summary>
[ApiController]
[Route("api/pairing")]
[Authorize(Policy = "ParentOrAdmin")]
public class PairingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<PairingController> _logger;

    public PairingController(AppDbContext db, ILogger<PairingController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/pairing/generate-code — 生成 6 位配对码
    /// 配对码有效期 5 分钟，用于儿童端首次连接时验证
    /// </summary>
    [HttpPost("generate-code")]
    public async Task<IActionResult> GeneratePairCode([FromBody] GeneratePairCodeRequest? request)
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"pairing-code:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        var pairCode = GenerateRandomCode();

        var deviceId = request?.DeviceId ?? 0;

        var pairingInfo = new PairingInfo
        {
            DeviceId = deviceId > 0 ? deviceId : null, // NULL 表示尚未分配设备（避免 FK 约束失败）
            PairCode = pairCode,
            PairMethod = request?.Method ?? "manual",
            PairStatus = "pending",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        };

        _db.PairingInfos.Add(pairingInfo);
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Pairing] 生成配对码: {Code}, 有效期至 {Expiry}", pairCode, pairingInfo.ExpiresAt);

        return Ok(new
        {
            pairCode = pairCode,
            expiresAt = pairingInfo.ExpiresAt,
            expiresInSeconds = 300,
        });
    }

    /// <summary>
    /// POST /api/pairing/verify — 验证配对码并绑定设备（手动 IP 配对流程）
    /// 服务端校验配对码，确认后绑定设备
    /// </summary>
    [HttpPost("verify")]
    public async Task<IActionResult> VerifyPairCode([FromBody] VerifyPairCodeRequest request)
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"pairing-verify:{clientIp}", 10, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        // 查找有效的 pending 配对码
        var pairingInfo = await _db.PairingInfos
            .Where(p => p.PairCode == request.PairCode && p.PairStatus == "pending")
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        if (pairingInfo == null)
        {
            return BadRequest(new { error = "配对码无效" });
        }

        if (pairingInfo.ExpiresAt < DateTime.UtcNow)
        {
            pairingInfo.PairStatus = "expired";
            await _db.SaveChangesAsync();
            return BadRequest(new { error = "配对码已过期" });
        }

        // 创建或更新设备
        Device device;
        if (pairingInfo.DeviceId is > 0)
        {
            device = await _db.Devices.FindAsync(pairingInfo.DeviceId);
            if (device == null)
                return NotFound(new { error = "设备不存在" });
        }
        else
        {
            // 使用请求中的设备信息创建新设备
            device = new Device
            {
                DeviceId = request.DeviceId ?? $"XP-{Guid.NewGuid():N}"[..14],
                DeviceName = request.DeviceName ?? "未知设备",
                Platform = request.Platform ?? "android",
                IpAddress = request.IpAddress,
                PairCode = request.PairCode,
                PairStatus = "paired",
                OnlineStatus = "offline",
            };
            _db.Devices.Add(device);
            await _db.SaveChangesAsync();

            // 创建默认策略
            var policy = new Policy
            {
                DeviceId = device.Id,
                DailyLimitMinutes = 120,
                OvertimeAction = "full_lock",
            };
            _db.Policies.Add(policy);
        }

        // 更新配对信息
        pairingInfo.DeviceId = device.Id;
        pairingInfo.TlsFingerprint = request.CertFingerprint;
        pairingInfo.PairStatus = "confirmed";
        pairingInfo.ConfirmedAt = DateTime.UtcNow;

        // 更新设备状态
        device.PairCode = request.PairCode;
        device.PairStatus = "paired";
        if (!string.IsNullOrEmpty(request.CertFingerprint))
            device.CertFingerprint = request.CertFingerprint;
        if (!string.IsNullOrEmpty(request.IpAddress))
            device.IpAddress = request.IpAddress;
        device.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // [TASK-OPT-12-P4-DEEPEN] 审计日志：设备配对确认
        await AuditAsync("pairing.verify", "Device", device.Id,
            $"{{\"deviceId\":\"{device.DeviceId}\",\"code\":\"{request.PairCode}\"}}");

        _logger.LogInformation("[Pairing] 配对确认: device={DeviceId}, code={Code}",
            device.DeviceId, request.PairCode);

        return Ok(new
        {
            deviceId = device.DeviceId,
            deviceName = device.DeviceName,
            pairStatus = device.PairStatus,
            certFingerprint = device.CertFingerprint,
        });
    }

    /// <summary>
    /// POST /api/pairing/cancel — 取消配对码
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelPairCode([FromBody] CancelPairCodeRequest request)
    {
        var pairingInfos = await _db.PairingInfos
            .Where(p => p.PairCode == request.PairCode && p.PairStatus == "pending")
            .ToListAsync();

        foreach (var pi in pairingInfos)
        {
            pi.PairStatus = "expired";
        }

        if (pairingInfos.Count > 0)
            await _db.SaveChangesAsync();

        return Ok(new { message = "配对码已取消" });
    }

    // ========== 辅助 ==========

    /// <summary>
    /// 生成 6 位随机数字配对码
    /// </summary>
    private static string GenerateRandomCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return code.ToString("D6");
    }

    // [TASK-OPT-12-P4-DEEPEN] ========== 审计日志 ==========

    private async Task AuditAsync(string action, string? targetType, int? targetId, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private int? GetUserId()
    {
        // 兼容测试环境无 HttpContext 的场景（User 为 null）
        var claim = User?.FindFirst("sub")?.Value
                 ?? User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

// ========== DTOs ==========

/// <summary>
/// 生成配对码请求
/// </summary>
public class GeneratePairCodeRequest
{
    /// <summary>已存在的设备 ID（可选，0=尚未分配）</summary>
    public int? DeviceId { get; set; }

    /// <summary>配对方式：manual | scan | broadcast</summary>
    public string Method { get; set; } = "manual";
}

/// <summary>
/// 验证配对码请求
/// </summary>
public class VerifyPairCodeRequest
{
    /// <summary>6 位配对码（必填）</summary>
    public string PairCode { get; set; } = string.Empty;

    /// <summary>设备唯一标识（新设备时必填）</summary>
    public string? DeviceId { get; set; }

    /// <summary>设备名称</summary>
    public string? DeviceName { get; set; }

    /// <summary>平台：android</summary>
    public string? Platform { get; set; }

    /// <summary>设备 IP 地址</summary>
    public string? IpAddress { get; set; }

    /// <summary>TLS 证书 SHA-256 指纹</summary>
    public string? CertFingerprint { get; set; }
}

/// <summary>
/// 取消配对码请求
/// </summary>
public class CancelPairCodeRequest
{
    public string PairCode { get; set; } = string.Empty;
}
