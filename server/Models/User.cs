using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// 用户账号（家长 + 管理员）
/// </summary>
[Table("users")]
public class User
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    public string PasswordSalt { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Role { get; set; } = "parent"; // admin | parent

    [MaxLength(128)]
    public string? Email { get; set; }

    [MaxLength(256)]
    public string? AvatarUrl { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>[SEC] 强制改密标记：种子账号（默认口令）或管理员重置口令后置 true，改密成功后清除</summary>
    public bool MustChangePassword { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    // Navigation: auth refresh tokens
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
