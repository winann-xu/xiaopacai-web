using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 系统配置键值存储（设置页 / 管理端系统设置共用）
/// </summary>
[Table("system_configs")]
public class SystemConfig
{
    [Key]
    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
