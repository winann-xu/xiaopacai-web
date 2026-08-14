namespace XiaopacaiWeb.Security;

/// <summary>
/// [SEC-P2] 密码策略：≥8 位且含字母与数字（防弱口令/撞库，红线 R4.2）
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>校验失败返回错误信息；通过返回 null</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"密码至少 {MinLength} 位";
        if (password.Length > MaxLength)
            return $"密码过长（上限 {MaxLength} 位）";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "密码必须同时包含字母和数字";
        return null;
    }
}
