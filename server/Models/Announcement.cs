using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 公告管理
/// </summary>
[Table("announcements")]
public class Announcement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Content { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Priority { get; set; } = "normal"; // normal | important | urgent

    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = "draft"; // draft | published | revoked

    public int? TargetDeviceId { get; set; } // NULL=全部设备

    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    // [TASK-PRELAUNCH-P3] 去重字段：发布代数 + 内容哈希（见 docs/adr/0004）
    [Required]
    public int Version { get; set; } = 0; // 每次 publish 递增；首次发布 = 1

    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty; // SHA-256(title|content|priority) 前 16 hex

    [Required]
    public int CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CreatedBy")]
    public User Creator { get; set; } = null!;

    [ForeignKey("TargetDeviceId")]
    public Device? TargetDevice { get; set; }
}
