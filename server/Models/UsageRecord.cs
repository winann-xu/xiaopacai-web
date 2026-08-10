using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 使用记录（原始数据）
/// </summary>
[Table("usage_records")]
public class UsageRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int DeviceId { get; set; }

    [MaxLength(256)]
    public string AppPackage { get; set; } = string.Empty;

    [MaxLength(128)]
    public string AppName { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Category { get; set; } = "other"; // game | social | video | learning | other

    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DurationSeconds { get; set; } = 0;

    public bool IsBlocked { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("DeviceId")]
    public Device Device { get; set; } = null!;
}
