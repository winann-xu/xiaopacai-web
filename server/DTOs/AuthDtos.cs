using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

/// <summary>
/// 登录请求
/// </summary>
public class LoginRequest
{
    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 家长邮箱注册请求（个人唯一账号）
/// </summary>
public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Token 刷新请求
/// </summary>
public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// 修改密码请求
/// </summary>
public class ChangePasswordRequest
{
    [Required]
    public string OldPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// 鉴权响应（含 access + refresh token）
/// </summary>
public class AuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public UserProfile Profile { get; set; } = null!;
}

/// <summary>
/// 用户信息（不含敏感字段）
/// </summary>
public class UserProfile
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
