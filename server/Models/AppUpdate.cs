using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-APP-UPDATE-V1] App 更新清单
/// abiUrls / abiSha256 以 JSON 对象存 TEXT 列（{ "arm64-v8a": "/downloads/xxx.apk", ... }），
/// DTO 层解析后透出，避免按平台拆列导致 Windows/未来 ABI 扩展需要反复改表。
/// </summary>
[Table("app_updates")]
public class AppUpdate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(16)]
    public string Platform { get; set; } = "android"; // android | windows（windows 本期预留）

    [Required]
    [MaxLength(32)]
    public string VersionName { get; set; } = string.Empty;

    /// <summary>语义化版本推导 versionCode（v1.2.0 → 10200），单调递增即防降级</summary>
    [Required]
    public int VersionCode { get; set; }

    /// <summary>强制更新阈值：客户端 versionCode &lt; minVersionCode 时强制</summary>
    [Required]
    public int MinVersionCode { get; set; }

    /// <summary>JSON 对象：abi → 下载路径（相对路径，如 /downloads/XiaopacaiParent-1.2.0-arm64-v8a.apk）</summary>
    [Required]
    public string AbiUrls { get; set; } = "{}";

    /// <summary>JSON 对象：abi → SHA-256（64 位小写 hex，服务端上传时计算）</summary>
    [Required]
    public string AbiSha256 { get; set; } = "{}";

    public long SizeBytes { get; set; }

    public string Changelog { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = "draft"; // draft | published

    // [TASK-APP-UPDATE-V1] D4 更新通道预留（stable/beta），本期固定 stable
    [MaxLength(16)]
    public string Channel { get; set; } = "stable";

    public DateTime? PublishedAt { get; set; }

    [Required]
    public int CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CreatedBy")]
    public User Creator { get; set; } = null!;
}
