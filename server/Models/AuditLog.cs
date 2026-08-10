using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 审计日志（管理后端操作记录）
/// </summary>
[Table("audit_logs")]
public class AuditLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? UserId { get; set; } // NULL=系统自动

    [Required]
    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? TargetType { get; set; }

    public int? TargetId { get; set; }

    public string? Detail { get; set; } // JSON

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(256)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public User? User { get; set; }
}
