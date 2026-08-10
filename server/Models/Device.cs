using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 儿童设备注册信息
/// </summary>
[Table("devices")]
public class Device
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string DeviceName { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty; // Android 设备唯一标识

    [Required]
    [MaxLength(16)]
    public string Platform { get; set; } = "android";

    [MaxLength(24)]
    public string? MacAddress { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(96)]
    public string? CertFingerprint { get; set; } // SHA256 指纹

    [MaxLength(6)]
    public string? PairCode { get; set; }

    [Required]
    [MaxLength(16)]
    public string PairStatus { get; set; } = "unpaired"; // unpaired | paired | revoked

    [Required]
    [MaxLength(16)]
    public string OnlineStatus { get; set; } = "offline"; // online | offline | reconnecting

    public DateTime? LastSeenAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Policy? Policy { get; set; }
    public ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
    public ICollection<DailySummary> DailySummaries { get; set; } = new List<DailySummary>();
    public ICollection<PairingInfo> PairingInfos { get; set; } = new List<PairingInfo>();
}
