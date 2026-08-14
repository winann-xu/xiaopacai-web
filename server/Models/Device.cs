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

    /// <summary>绑定家长账号（OPT12 需求 3，配对确认时绑定，可空）</summary>
    [MaxLength(64)]
    public string? OwnerUserId { get; set; }

    /// <summary>应用分类配置（OPT12 需求 1，JSON 数组，随 policy_push 下发）</summary>
    public string? AppCategories { get; set; }

    /// <summary>设备级访问令牌（TASK-OPT-12-P4-DEEPEN：诊断上报鉴权，可空；由 /api/devices/{id}/token 生成/轮换）</summary>
    [MaxLength(64)]
    public string? DeviceToken { get; set; }

    /// <summary>待下发的每日限额重置时间（UTC；设备离线时挂起，重连握手后补推并清空）</summary>
    public DateTime? PendingResetAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Policy? Policy { get; set; }
    public ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
    public ICollection<DailySummary> DailySummaries { get; set; } = new List<DailySummary>();
    public ICollection<PairingInfo> PairingInfos { get; set; } = new List<PairingInfo>();
}
