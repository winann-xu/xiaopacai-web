using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

/// <summary>
/// 登录请求
/// </summary>
public class LoginRequest
{
    [Required]
    [MaxLength(128)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// [TASK-ACCOUNT-V1] 家长邮箱注册请求（个人唯一账号）
/// code：邮箱验证码（register 用途，先调 /api/auth/email-code 获取）
/// </summary>
public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string Email { get; set; } = string.Empty;

    /// <summary>[TASK-ACCOUNT-V1] 6 位邮箱验证码</summary>
    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "验证码格式不正确")]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? DisplayName { get; set; }
}

/// <summary>
/// [TASK-ACCOUNT-V1] 邮箱验证码发送请求
/// purpose ∈ register | login | reset_password
/// </summary>
public class EmailCodeRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^(register|login|reset_password)$", ErrorMessage = "用途不合法")]
    public string Purpose { get; set; } = string.Empty;
}

/// <summary>
/// [TASK-ACCOUNT-V1] 验证码登录请求
/// </summary>
public class CodeLoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string Email { get; set; } = string.Empty;

    /// <summary>6 位邮箱验证码（login 用途）</summary>
    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "验证码格式不正确")]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// [TASK-ACCOUNT-V1] 找回密码请求（邮箱 + 验证码 + 新密码）
/// </summary>
public class PasswordResetRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string Email { get; set; } = string.Empty;

    /// <summary>6 位邮箱验证码（reset_password 用途）</summary>
    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "验证码格式不正确")]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// [TASK-ACCOUNT-V1] 登录态密码二次验证请求（解绑/换绑前置，签发一次性 Action Token）
/// </summary>
public class VerifyPasswordRequest
{
    [Required]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Token 刷新请求
/// [SEC-K5] RefreshToken 可为空：浏览器会话改由 httpOnly Cookie 携带（body 仅兼容原生客户端）
/// </summary>
public class RefreshRequest
{
    [MaxLength(512)]
    public string? RefreshToken { get; set; }
}

/// <summary>
/// 修改密码请求
/// </summary>
public class ChangePasswordRequest
{
    [Required]
    [MaxLength(128)]
    public string OldPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
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
