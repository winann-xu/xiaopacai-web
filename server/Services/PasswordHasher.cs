using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace XiaopacaiWeb.Services;

/// <summary>
/// 密码哈希服务 — Argon2id（推荐）+ PBKDF2 SHA-256（兼容模式）
/// </summary>
public class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 32;                      // 32 字节盐值
    private const int Pbkdf2Iterations = 600_000;         // ≥600k 迭代
    private const int Pbkdf2HashSize = 32;                // 256-bit 输出
    private const int Argon2MemorySize = 65536;           // 64 MB
    private const int Argon2Iterations = 4;               // 迭代次数
    private const int Argon2DegreeOfParallelism = 2;       // 并行度
    private const string Argon2SaltPrefix = "$argon2id$"; // 盐值前缀标记

    /// <inheritdoc />
    public (string hash, string salt) HashPassword(string password)
    {
        return HashPasswordArgon2(password);
    }

    /// <inheritdoc />
    public (string hash, string salt) HashPasswordPbkdf2(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            Pbkdf2HashSize);

        return (
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt) // 无前缀 = PBKDF2
        );
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
            return false;

        return IsArgon2Hash(storedSalt)
            ? VerifyArgon2(password, storedHash, storedSalt)
            : VerifyPbkdf2(password, storedHash, storedSalt);
    }

    /// <inheritdoc />
    public bool IsArgon2Hash(string salt) =>
        !string.IsNullOrEmpty(salt) && salt.StartsWith(Argon2SaltPrefix);

    // ---- private helpers ----

    private (string hash, string salt) HashPasswordArgon2(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);

        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = saltBytes,
            DegreeOfParallelism = Argon2DegreeOfParallelism,
            MemorySize = Argon2MemorySize,
            Iterations = Argon2Iterations,
        };

        var hashBytes = argon2.GetBytes(32); // 256-bit

        return (
            Convert.ToBase64String(hashBytes),
            Argon2SaltPrefix + Convert.ToBase64String(saltBytes)
        );
    }

    private bool VerifyArgon2(string password, string storedHash, string storedSalt)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(storedSalt.Substring(Argon2SaltPrefix.Length));

            using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
            {
                Salt = saltBytes,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations,
            };

            var hashBytes = argon2.GetBytes(32);
            var computedHash = Convert.ToBase64String(hashBytes);

            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(computedHash),
                System.Text.Encoding.UTF8.GetBytes(storedHash));
        }
        catch
        {
            return false;
        }
    }

    private bool VerifyPbkdf2(string password, string storedHash, string storedSalt)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(storedSalt);
            var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                Pbkdf2HashSize);

            return CryptographicOperations.FixedTimeEquals(
                computedHash,
                Convert.FromBase64String(storedHash));
        }
        catch
        {
            return false;
        }
    }
}
