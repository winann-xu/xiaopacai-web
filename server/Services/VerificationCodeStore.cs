using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace XiaopacaiWeb.Services;

/// <summary>
/// [TASK-ACCOUNT-V1] 邮箱验证码内存存储（注册 / 验证码登录 / 找回密码共用）
///
/// - 6 位数字码，5 分钟有效，单码单用（验证后立即消费）；
/// - 同一 (邮箱, 用途) 重发作废旧码（防旧码复用）；
/// - 每邮箱未消费码数量上限（防批量灌码占内存）。
/// </summary>
public class VerificationCodeStore
{
    /// <summary>验证码有效期（秒）</summary>
    public const int CodeLifetimeSeconds = 300;

    /// <summary>单邮箱未消费码上限</summary>
    public const int MaxPendingPerEmail = 5;

    /// <summary>用途：register | login | reset_password</summary>
    public static readonly string[] Purposes = { "register", "login", "reset_password" };

    private readonly ConcurrentDictionary<string, CodeEntry> _codes = new();

    /// <summary>签发新码（重发作废同 (邮箱, 用途) 旧码），返回 6 位数字码</summary>
    public string Issue(string email, string purpose)
    {
        var key = Key(email, purpose);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codes[key] = new CodeEntry
        {
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddSeconds(CodeLifetimeSeconds),
        };
        // 清理该邮箱全部过期码
        foreach (var k in _codes.Keys.Where(k => k.StartsWith(email + "|", StringComparison.OrdinalIgnoreCase)))
        {
            if (_codes.TryGetValue(k, out var e) && e.ExpiresAt < DateTime.UtcNow)
                _codes.TryRemove(k, out _);
        }
        return code;
    }

    /// <summary>验证并消费（单码单用）；失败返回 false</summary>
    public bool VerifyAndConsume(string email, string purpose, string code)
    {
        var key = Key(email, purpose);
        if (!_codes.TryGetValue(key, out var entry))
            return false;
        var ok = !entry.Consumed &&
                 entry.ExpiresAt >= DateTime.UtcNow &&
                 string.Equals(entry.Code, code, StringComparison.Ordinal);
        if (ok)
            entry.Consumed = true;
        return ok;
    }

    /// <summary>是否存在有效未消费码（防重发轰炸的辅助判断）</summary>
    public bool HasPending(string email, string purpose)
    {
        var key = Key(email, purpose);
        return _codes.TryGetValue(key, out var entry) &&
               !entry.Consumed && entry.ExpiresAt >= DateTime.UtcNow;
    }

    private static string Key(string email, string purpose) =>
        email.Trim().ToLower() + "|" + purpose;

    private class CodeEntry
    {
        public string Code { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public bool Consumed { get; set; }
    }
}

/// <summary>
/// [TASK-ACCOUNT-V1] 密码验证一次性 Action Token（解绑/换绑二次确认用）
///
/// 登录态下验证账号密码成功后签发；5 分钟有效、单次使用、绑定 userId 防跨账号。
/// </summary>
public class ActionTokenStore
{
    /// <summary>Token 有效期（秒）</summary>
    public const int LifetimeSeconds = 300;

    private readonly ConcurrentDictionary<string, ActionTokenEntry> _tokens = new();

    public string Issue(int userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _tokens[token] = new ActionTokenEntry
        {
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(LifetimeSeconds),
        };
        return token;
    }

    /// <summary>校验并消费（单次使用）；userId 必须匹配</summary>
    public bool VerifyAndConsume(string token, int userId)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            !_tokens.TryGetValue(token, out var entry))
            return false;
        var ok = !entry.Consumed &&
                 entry.ExpiresAt >= DateTime.UtcNow &&
                 entry.UserId == userId;
        if (ok)
            entry.Consumed = true;
        return ok;
    }

    private class ActionTokenEntry
    {
        public int UserId { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool Consumed { get; set; }
    }
}
