using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 儿童端故障诊断记录（OPT12 需求 5）
///
/// 儿童端每天上报一次（或异常时立即补报），用于 Web 管理端收集与展示，
/// 为后续升级提供数据依据。
/// </summary>
[Table("diagnostics")]
public class DiagnosticRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>儿童端设备唯一标识（与 devices.device_id 对应）</summary>
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>儿童端 APP 版本号（如 3.0.1）</summary>
    [MaxLength(32)]
    public string? AppVersion { get; set; }

    /// <summary>Android 系统版本号（如 12）</summary>
    [MaxLength(16)]
    public string? AndroidVersion { get; set; }

    /// <summary>设备型号（如 Pixel 6）</summary>
    [MaxLength(64)]
    public string? DeviceModel { get; set; }

    /// <summary>设备厂商（如 Google / Xiaomi）</summary>
    [MaxLength(64)]
    public string? Manufacturer { get; set; }

    /// <summary>权限状态（JSON）：无障碍/用量统计/设备管理器/通知/电池优化 等</summary>
    public string? PermissionStatus { get; set; }

    /// <summary>服务运行状态（JSON）：守护服务 / 无障碍服务运行状态</summary>
    public string? ServiceStatus { get; set; }

    /// <summary>最近崩溃堆栈（JSON，最近 5 条）</summary>
    public string? RecentCrashes { get; set; }

    /// <summary>P2P 连接历史（JSON：成功/失败/重连次数）</summary>
    public string? P2pHistory { get; set; }

    /// <summary>本地数据库大小（字节）</summary>
    public long? DbSizeBytes { get; set; }

    /// <summary>网络状态：wifi | cellular | none</summary>
    [MaxLength(16)]
    public string? NetworkType { get; set; }

    /// <summary>上报时间（UTC）</summary>
    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;
}
