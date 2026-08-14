using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Security;

/// <summary>
/// [SEC-K2] 设备归属校验：家长仅能访问自己绑定的设备，管理员不受限。
/// 越权访问一律 403（PROMPT_SECURITY_TEST.md K2）。
/// 三个状态：NotFound（设备不存在 → 404）、Forbidden（存在但不属于当前家长 → 403）、Ok。
/// </summary>
public enum DeviceAccessResult
{
    Ok,
    NotFound,
    Forbidden,
}

public static class DeviceAccess
{
    /// <summary>
    /// 校验当前用户对设备的访问权（家长：OwnerUserId 必须匹配；管理员：全部放行）
    /// </summary>
    public static async Task<(DeviceAccessResult Status, Device? Device)> CheckAsync(
        AppDbContext db, int deviceId, ClaimsPrincipal user)
    {
        var device = await db.Devices.FindAsync(deviceId);
        if (device == null)
            return (DeviceAccessResult.NotFound, null);

        if (user.IsInRole("admin"))
            return (DeviceAccessResult.Ok, device);

        var uid = GetUserId(user);
        if (uid != null && device.OwnerUserId == uid)
            return (DeviceAccessResult.Ok, device);

        return (DeviceAccessResult.Forbidden, null);
    }

    /// <summary>
    /// 提取当前用户 ID（sub 或 NameIdentifier 声明）
    /// </summary>
    public static string? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                 ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrEmpty(claim) ? null : claim;
    }
}
