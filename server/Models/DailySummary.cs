using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 每日汇总（聚合数据）
/// </summary>
[Table("daily_summary")]
public class DailySummary
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int DeviceId { get; set; }

    [Required]
    [MaxLength(10)]
    public string SummaryDate { get; set; } = string.Empty; // YYYY-MM-DD

    public int TotalMinutes { get; set; } = 0;
    public int GameMinutes { get; set; } = 0;
    public int SocialMinutes { get; set; } = 0;
    public int VideoMinutes { get; set; } = 0;
    public int LearningMinutes { get; set; } = 0;
    public int OtherMinutes { get; set; } = 0;
    public int OvertimeCount { get; set; } = 0;
    public int BlockCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("DeviceId")]
    public Device Device { get; set; } = null!;
}
