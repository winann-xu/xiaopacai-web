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

        // [SEC] 指定已有设备时校验归属（红线 R2.1）：防生成指向他人设备的配对码
        // 后在 verify 环节篡改该设备的证书指纹锚点/劫持身份
        if (deviceId > 0)
        {
            var (access, _) = await DeviceAccess.CheckAsync(_db, deviceId, User);
            if (access == DeviceAccessResult.NotFound)
                return NotFound(new { error = "设备不存在" });
            if (access == DeviceAccessResult.Forbidden)
                return StatusCode(403, new { error = "无权访问该设备" });
        }

        var pairingInfo = new PairingInfo
        {
            DeviceId = deviceId > 0 ? deviceId : null, // NULL 表示尚未分配设备（避免 FK 约束失败）
            PairCode = pairCode,
            PairMethod = request?.Method ?? "manual",
            PairStatus = "pending",
            OwnerUserId = GetUserId()?.ToString(),
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
    /// POST /api/pairing/binding-qr — 生成儿童端扫码绑定二维码内容
    /// 家长在 Web 登录后展示二维码，儿童端扫码即绑定到当前家长账号。
    /// </summary>
    [HttpPost("binding-qr")]
    public async Task<IActionResult> BindingQr()
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"pairing-code:{clientIp}", 5, 60))
            return StatusCode(429, new { error = "操作过于频繁，请 1 分钟后再试" });

        var pairCode = GenerateRandomCode();
        var pairingInfo = new PairingInfo
        {
            PairCode = pairCode,
            PairMethod = "scan",
            PairStatus = "pending",
            OwnerUserId = GetUserId()?.ToString(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        };
        _db.PairingInfos.Add(pairingInfo);
        await _db.SaveChangesAsync();

        // [REQ] 中继地址优先读配置（LAN 用内网 IP；跨网用公网域名并需转发 9527），未配置回退请求 Host
        var relayHostConfig = await _db.SystemConfigs
            .FirstOrDefaultAsync(c => c.Key == "relay_host");
        var host = string.IsNullOrWhiteSpace(relayHostConfig?.Value)
            ? Request.Host.Host
            : relayHostConfig!.Value.Trim();
        var qrContent = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "web_relay",
            host,
            port = 9527,
            pairingCode = pairCode,
            fingerprint = ""
        });

        _logger.LogInformation("[Pairing] 生成绑定二维码: {Code} host={Host}", pairCode, host);
        return Ok(new
        {
            pairCode,
            host,
            port = 9527,
            qrContent,
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

        // [SEC] 指纹格式校验：仅接受 64 位小写十六进制 SHA-256 指纹，防非法值污染设备指纹锚点
        if (!string.IsNullOrEmpty(request.CertFingerprint) &&
            !System.Text.RegularExpressions.Regex.IsMatch(request.CertFingerprint, "^[0-9a-f]{64}$"))
            return BadRequest(new { error = "证书指纹格式无效" });

        // [SEC] 配对码归属校验：防跨账号猜测 6 位配对码劫持绑定流程
        var currentUserId = GetUserId()?.ToString();
        if (!string.IsNullOrEmpty(pairingInfo.OwnerUserId) && !User.IsInRole("admin") &&
            pairingInfo.OwnerUserId != currentUserId)
            return StatusCode(403, new { error = "无权验证该配对码" });

        // 创建或更新设备
        Device device;
        if (pairingInfo.DeviceId is > 0)
        {
            // [SEC] 已有设备时校验归属（红线 R2.1）：防他人在 verify 环节覆盖本设备的证书指纹
            var (access, _) = await DeviceAccess.CheckAsync(_db, pairingInfo.DeviceId.Value, User);
            if (access == DeviceAccessResult.NotFound)
                return NotFound(new { error = "设备不存在" });
            if (access == DeviceAccessResult.Forbidden)
                return StatusCode(403, new { error = "无权访问该设备" });

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
        // [REQ] 绑定配对码归属账号到设备（无归属时回退当前登录账号）
        var ownerId = pairingInfo.OwnerUserId ?? GetUserId()?.ToString();
        if (ownerId != null && string.IsNullOrEmpty(device.OwnerUserId))
            device.OwnerUserId = ownerId;
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

        // [SEC] 仅配对码归属者或管理员可取消（红线 R2.1），防跨账号作废他人配对码
        var currentUserId = GetUserId()?.ToString();
        var isAdmin = User.IsInRole("admin");
        var owned = pairingInfos
            .Where(p => isAdmin || p.OwnerUserId == currentUserId)
            .ToList();

        foreach (var pi in owned)
        {
            pi.PairStatus = "expired";
        }

        if (owned.Count > 0)
            await _db.SaveChangesAsync();

        if (pairingInfos.Count > 0 && owned.Count == 0)
            return StatusCode(403, new { error = "无权取消该配对码" });

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
