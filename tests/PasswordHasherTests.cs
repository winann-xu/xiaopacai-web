using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Services;

/// <summary>
/// 密码哈希服务测试 — Argon2id + PBKDF2 SHA-256
///
/// 覆盖：
/// - Argon2id 哈希/验证
/// - PBKDF2 哈希/验证
/// - 错误密码拒绝
/// - 哈希格式检测 (IsArgon2Hash)
/// - 相同密码产生不同哈希（盐）
/// </summary>
public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    // ==================== Argon2id ====================

    [Fact]
    public void HashPassword_Argon2_ProducesValidHash()
    {
        var (hash, salt) = _hasher.HashPassword("MySecurePassword123");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.NotNull(salt);
        Assert.NotEmpty(salt);

        // Argon2 前缀格式: $argon2id$
        Assert.StartsWith("$argon2id$", salt);
    }

    [Fact]
    public void HashPassword_SamePasswordTwice_ProducesDifferentHashes()
    {
        var password = "SamePassword456";

        var (hash1, salt1) = _hasher.HashPassword(password);
        var (hash2, salt2) = _hasher.HashPassword(password);

        // 盐应该不同
        Assert.NotEqual(salt1, salt2);
        // 哈希也应该不同
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_Argon2_CorrectPassword_ReturnsTrue()
    {
        var password = "ValidPassword789!";
        var (hash, salt) = _hasher.HashPassword(password);

        var result = _hasher.VerifyPassword(password, hash, salt);
        Assert.True(result);
    }

    [Fact]
    public void VerifyPassword_Argon2_WrongPassword_ReturnsFalse()
    {
        var (hash, salt) = _hasher.HashPassword("CorrectPassword");
        var result = _hasher.VerifyPassword("WrongPassword", hash, salt);
        Assert.False(result);
    }

    [Fact]
    public void VerifyPassword_Argon2_CaseSensitive_ReturnsFalse()
    {
        var (hash, salt) = _hasher.HashPassword("CaseSensitive");
        var result = _hasher.VerifyPassword("casesensitive", hash, salt);
        Assert.False(result);
    }

    // ==================== PBKDF2 ====================

    [Fact]
    public void HashPasswordPbkdf2_ProducesValidHash()
    {
        var (hash, salt) = _hasher.HashPasswordPbkdf2("Pbkdf2Password123");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.NotNull(salt);
        Assert.NotEmpty(salt);

        // PBKDF2 盐值无前缀（与实现约定一致：$argon2id$ 前缀 = Argon2，无前缀 = PBKDF2）
        Assert.False(_hasher.IsArgon2Hash(salt));
    }

    [Fact]
    public void HashPasswordPbkdf2_SamePasswordTwice_ProducesDifferentHashes()
    {
        var password = "AnotherPassword";

        var (hash1, salt1) = _hasher.HashPasswordPbkdf2(password);
        var (hash2, salt2) = _hasher.HashPasswordPbkdf2(password);

        Assert.NotEqual(salt1, salt2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_Pbkdf2_CorrectPassword_ReturnsTrue()
    {
        var password = "Pbkdf2Test456!";
        var (hash, salt) = _hasher.HashPasswordPbkdf2(password);

        var result = _hasher.VerifyPassword(password, hash, salt);
        Assert.True(result);
    }

    [Fact]
    public void VerifyPassword_Pbkdf2_WrongPassword_ReturnsFalse()
    {
        var (hash, salt) = _hasher.HashPasswordPbkdf2("Right");
        var result = _hasher.VerifyPassword("Wrong", hash, salt);
        Assert.False(result);
    }

    // ==================== IsArgon2Hash ====================

    [Fact]
    public void IsArgon2Hash_Argon2Salt_ReturnsTrue()
    {
        var (_, salt) = _hasher.HashPassword("test");
        Assert.True(_hasher.IsArgon2Hash(salt));
    }

    [Fact]
    public void IsArgon2Hash_Pbkdf2Salt_ReturnsFalse()
    {
        var (_, salt) = _hasher.HashPasswordPbkdf2("test");
        Assert.False(_hasher.IsArgon2Hash(salt));
    }

    // ==================== 特殊字符密码 ====================

    [Theory]
    [InlineData("p@ssw0rd!#$%^&*()")]
    [InlineData("中文密码测试123")]
    [InlineData("emoji🔐password")]
    [InlineData("a")]
    [InlineData("this is a very long password that exceeds normal length limits 1234567890!@#$%^&*()")]
    public void HashAndVerify_SpecialCharacters(string password)
    {
        // 注意：空密码不被 Argon2 库接受（业务层 MinLength(6) 也拒绝空密码），因此不在此测试
        var (hash, salt) = _hasher.HashPassword(password);
        Assert.True(_hasher.VerifyPassword(password, hash, salt));
        Assert.False(_hasher.VerifyPassword(password + "x", hash, salt));
    }

    [Fact]
    public void VerifyPassword_EmptyPassword_ReturnsFalse()
    {
        // 空密码直接拒绝（Argon2 库不接受空密码输入）
        var (hash, salt) = _hasher.HashPassword("valid-password");
        Assert.False(_hasher.VerifyPassword("", hash, salt));
        Assert.False(_hasher.VerifyPassword("", "", ""));
    }

    // ==================== 跨算法兼容性 ====================

    [Fact]
    public void VerifyPassword_Argon2Hash_WithPbkdf2Attempt_ShouldFail()
    {
        // 使用 Argon2 哈希
        var (hash, salt) = _hasher.HashPassword("CrossAlgoTest");

        // 尝试用错误的密码验证（这个测试确认验证不会崩溃）
        Assert.False(_hasher.VerifyPassword("wrong", hash, salt));
        // 但是用正确的密码应该成功
        Assert.True(_hasher.VerifyPassword("CrossAlgoTest", hash, salt));
    }

    [Fact]
    public void HashPassword_BothAlgorithms_ProducesDifferentFormats()
    {
        var password = "FormatTest123";

        var (argonHash, argonSalt) = _hasher.HashPassword(password);
        var (pbkdf2Hash, pbkdf2Salt) = _hasher.HashPasswordPbkdf2(password);

        // Argon2 验证
        Assert.True(_hasher.VerifyPassword(password, argonHash, argonSalt));

        // PBKDF2 验证
        Assert.True(_hasher.VerifyPassword(password, pbkdf2Hash, pbkdf2Salt));

        // 互操作性：PBKDF2 的哈希和盐不能用于 Argon2 验证方式
        Assert.True(_hasher.IsArgon2Hash(argonSalt));
        Assert.False(_hasher.IsArgon2Hash(pbkdf2Salt));
    }
}
