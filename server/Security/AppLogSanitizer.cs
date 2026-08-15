using System.Text.RegularExpressions;

namespace XiaopacaiWeb.Security;

/// <summary>
/// [TASK-MILESTONE-V3] 需求 14：日志脱敏（服务端纵深防御第二层）
///
/// 客户端 AppLog 写入时已打码（第一层）；服务端入库前对每一条再次打码，
/// 防任何来源（被篡改客户端/旧版本客户端）将明文密码、验证码、令牌、密钥落库。
/// 模式与 Android AppLog.maskSecrets 保持一致。
/// </summary>
public static class AppLogSanitizer
{
    /// <summary>key=value 赋值形式：password/token/secret/api-key 等，value 整体打码（保留分隔符后空格可读）</summary>
    private static readonly Regex SecretAssignment = new(
        @"(?i)((?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|auth[_-]?token)\s*[:=]\s*)[^\s,;，；]+",
        RegexOptions.Compiled);

    /// <summary>验证码/校验码标签后跟 4-8 位数字：仅数字打码，保留标签可读</summary>
    private static readonly Regex VerificationCode = new(
        @"(?i)((?:验证码|校验码|verification[\s_-]?code|sms[\s_-]?code)\s*[:=：]?\s*)\d{4,8}",
        RegexOptions.Compiled);

    /// <summary>JWT（三段 Base64URL，eyJ 开头）</summary>
    private static readonly Regex JwtToken = new(
        @"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled);

    /// <summary>64 位十六进制串（密钥/哈希常见形态）</summary>
    private static readonly Regex Hex64 = new(
        @"(?i)\b[a-f0-9]{64}\b",
        RegexOptions.Compiled);

    /// <summary>敏感信息打码（纯函数，可单测）</summary>
    public static string MaskSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var out1 = SecretAssignment.Replace(text, "$1***");
        var out2 = VerificationCode.Replace(out1, "$1***");
        var out3 = JwtToken.Replace(out2, "***");
        return Hex64.Replace(out3, "***");
    }

    /// <summary>截断（null → null；超长截断）</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
