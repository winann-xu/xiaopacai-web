using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 配对信息（发现与配对过程记录）
/// </summary>
[Table("pairing_info")]
public class PairingInfo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>关联设备（NULL=尚未分配，生成配对码时预置）</summary>
    public int? DeviceId { get; set; }

    [Required]
    [MaxLength(6)]
    public string PairCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string PairMethod { get; set; } = "manual"; // scan | manual_ip | broadcast

    public string? DiscoveryData { get; set; } // JSON

    [MaxLength(96)]
    public string? TlsFingerprint { get; set; }

    [Required]
    [MaxLength(16)]
    public string PairStatus { get; set; } = "pending"; // pending | confirmed | expired | rejected

    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("DeviceId")]
    public Device Device { get; set; } = null!;
}
