using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 策略配置（每设备一条）
/// </summary>
[Table("policies")]
public class Policy
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int DeviceId { get; set; }

    public int DailyLimitMinutes { get; set; } = 120; // 30~480

    [MaxLength(5)]
    public string? BedtimeStart { get; set; } // HH:mm

    [MaxLength(5)]
    public string? BedtimeEnd { get; set; } // HH:mm

    // 分类限额（分钟/天，-1=不限）
    public int CategoryGameLimit { get; set; } = -1;
    public int CategorySocialLimit { get; set; } = -1;
    public int CategoryVideoLimit { get; set; } = -1;
    public int CategoryLearningLimit { get; set; } = -1;

    // 黑白名单（JSON 数组存储）
    public string? WhitelistApps { get; set; }
    public string? BlacklistApps { get; set; }

    [Required]
    [MaxLength(16)]
    public string OvertimeAction { get; set; } = "full_lock"; // full_lock | partial_lock | warn_only

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// [TASK-MILESTONE-V3] A2 策略版本号：每次保存递增（服务端权威版本）。
    /// 多端并发修改时客户端携带 expectedVersion，不匹配返回 409 由调用方刷新后重试；
    /// 儿童端重连收到策略时比对本地版本，旧版本不覆盖新版本。
    /// </summary>
    [Required]
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("DeviceId")]
    public Device Device { get; set; } = null!;
}
