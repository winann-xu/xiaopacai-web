namespace XiaopacaiWeb.Services;

/// <summary>
/// 密码哈希服务接口 — 支持 PBKDF2 (SHA-256 ≥600k 迭代) 和 Argon2id
/// </summary>
public interface IPasswordHasher
{
    /// <summary>使用 Argon2id 哈希密码（推荐）</summary>
    (string hash, string salt) HashPassword(string password);

    /// <summary>使用 PBKDF2 SHA-256 600k 迭代哈希密码（兼容模式）</summary>
    (string hash, string salt) HashPasswordPbkdf2(string password);

    /// <summary>验证密码（自动检测 Argon2 vs PBKDF2）</summary>
    bool VerifyPassword(string password, string storedHash, string storedSalt);

    /// <summary>检测哈希是否为 Argon2 格式</summary>
    bool IsArgon2Hash(string salt);
}
