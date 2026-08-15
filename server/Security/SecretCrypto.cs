using System.Security.Cryptography;

namespace XiaopacaiWeb.Security;

/// <summary>
/// [TASK-ACCOUNT-V1-MAILCONFIG] Secret 字段对称加密（AES-256-GCM）
///
/// 主密钥来自环境变量 XIAOPACAI_MASTER_KEY（64 字符 hex = 32 字节）。
/// 未配置主密钥时拒绝加密（返回 null），调用方必须提示用户并拒绝保存
/// Secret 类配置——禁止明文入库（红线 R4.1）。
/// </summary>
public static class SecretCrypto
{
    private const string MasterKeyEnv = "XIAOPACAI_MASTER_KEY";

    /// <summary>主密钥是否已配置</summary>
    public static bool IsMasterKeyConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MasterKeyEnv));

    private static byte[]? GetMasterKey()
    {
        var hex = Environment.GetEnvironmentVariable(MasterKeyEnv)?.Trim();
        if (string.IsNullOrEmpty(hex) || hex.Length != 64)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 加密明文 → "v1:{base64(nonce)}:{base64(ciphertext+tag)}"；主密钥缺失返回 null
    /// </summary>
    public static string? Encrypt(string plaintext)
    {
        var key = GetMasterKey();
        if (key == null || string.IsNullOrEmpty(plaintext))
            return null;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        cipher.CopyTo(payload, nonce.Length + tag.Length);
        return "v1:" + Convert.ToBase64String(payload);
    }

    /// <summary>解密；密文格式非法或密钥缺失返回 null（调用方按「未设置」处理）</summary>
    public static string? Decrypt(string? ciphertext)
    {
        var key = GetMasterKey();
        if (key == null || string.IsNullOrWhiteSpace(ciphertext) || !ciphertext.StartsWith("v1:"))
            return null;
        try
        {
            var payload = Convert.FromBase64String(ciphertext[3..]);
            if (payload.Length < 28)
                return null;
            var nonce = payload[..12];
            var tag = payload.AsSpan(12, 16);
            var cipher = payload[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
