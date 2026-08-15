using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

// ========== 扫码登录 Ticket（OPT12 需求 10） ==========

/// <summary>
/// 生成扫码登录 Ticket 请求（未登录可调用）
/// </summary>
public class LoginTicketRequest
{
    /// <summary>客户端标识（可选，用于追踪来源浏览器）</summary>
    [MaxLength(128)]
    public string? ClientId { get; set; }
}

/// <summary>
/// 扫码登录 Ticket 响应 / 轮询结果
/// </summary>
public class LoginTicketResponse
{
    /// <summary>一次性 Ticket（UUID）</summary>
    public string Ticket { get; set; } = string.Empty;

    /// <summary>状态：pending | confirmed | expired</summary>
    public string Status { get; set; } = "pending";

    /// <summary>过期时间（UTC）</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>剩余有效秒数</summary>
    public int ExpiresInSeconds { get; set; }

    /// <summary>确认后返回的登录信息（仅 status=confirmed 时非空）</summary>
    public AuthResponse? Auth { get; set; }
}

/// <summary>
/// 家长端 APP 确认扫码登录请求（已登录调用）
/// </summary>
public class LoginTicketConfirmRequest
{
    /// <summary>确认来源设备标识（可选，家长端 APP 设备 ID）</summary>
    [MaxLength(128)]
    public string? DeviceId { get; set; }
}
