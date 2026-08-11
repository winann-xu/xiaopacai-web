using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 云端中继会话记录（OPT12 需求 3）
///
/// 记录儿童端/家长端通过 Web 3.0 中继（P2P TCP/TLS 9527）连接的会话，
/// 供管理端查看在线中继设备。
///
/// TODO(P4)：P2pMessageHandler 握手时写入会话记录，断线时更新 disconnected_at。
/// </summary>
[Table("relay_sessions")]
public class RelaySession
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>连接方设备唯一标识（儿童端 devices.device_id 或家长端 APP 设备 ID）</summary>
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>连接方角色：child（儿童端）| parent（家长端）</summary>
    [Required]
    [MaxLength(16)]
    public string Role { get; set; } = "child";

    /// <summary>关联家长账号（可空，握手携带家长身份时绑定）</summary>
    public int? UserId { get; set; }

    /// <summary>连接来源 IP</summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>会话状态：connected | disconnected</summary>
    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = "connected";

    /// <summary>连接建立时间（UTC）</summary>
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>断开时间（UTC，连接中为 NULL）</summary>
    public DateTime? DisconnectedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
