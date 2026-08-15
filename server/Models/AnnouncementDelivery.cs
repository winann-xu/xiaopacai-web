using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-PRELAUNCH-P3] 公告送达/回执记录（见 docs/adr/0004）
/// 每公告×每设备一行：推送次数、终端显示时间、确认回执时间。
/// 推送（发布/补推）时 upsert push_count++；announcement_displayed / announcement_ack 落库。
/// </summary>
[Table("announcement_deliveries")]
[Index(nameof(AnnouncementId), nameof(DeviceId), IsUnique = true, Name = "idx_deliveries_ann_device")]
public class AnnouncementDelivery
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int AnnouncementId { get; set; }

    [Required]
    public int DeviceId { get; set; }

    /// <summary>累计推送次数（每次 publish/重连补推 +1）</summary>
    public int PushCount { get; set; }

    public DateTime? LastPushedAt { get; set; }

    /// <summary>终端首次显示时间（announcement_displayed 上报）</summary>
    public DateTime? DisplayedAt { get; set; }

    /// <summary>终端确认回执时间（announcement_ack 上报）</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// [TASK-MILESTONE-V3] B2/B10 补偿重推时间：发布后 60 秒未收到 displayed 回执时
    /// 对在线设备补偿重推一次（幂等：终端按版本+内容哈希去重），避免偶发瞬时未处理。
    /// </summary>
    public DateTime? CompensatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AnnouncementId))]
    public Announcement Announcement { get; set; } = null!;

    [ForeignKey(nameof(DeviceId))]
    public Device Device { get; set; } = null!;
}
