using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

// [TASK-ACCOUNT-V1] 环境变量（主密钥 / 邮件配置）相关测试串行执行，避免并发互踩
[CollectionDefinition("AccountV1", DisableParallelization = true)]
public class AccountV1Collection { }

/// <summary>
/// [TASK-ACCOUNT-V1] 账户系统重构测试 — 邮箱注册/验证码登录/找回密码/解绑前置二次验证/
/// Secret 加解密/邮件发送器配置优先级
///
/// 覆盖（ADR 0009 A2-A8）：
/// - 注册需验证码（错码 400 / 对码成功）
/// - 发码：邮件未配置 503；login/reset 用途下未注册邮箱不发信（防枚举）
/// - 验证码登录 / 找回密码（成功 + 失败 + 吊销全部 Token）
/// - verify-password 签发一次性 Action Token（错密码 400）
/// - SecretCrypto 加解密往返 + 主密钥缺失拒绝
/// - MailSender：DB 配置优先 → 环境变量兜底 → Secret 损坏回退
/// - MailConfigController：脱敏回显 / 无主密钥拒存 Secret / 测试发送
/// </summary>
[Collection("AccountV1")]
public class AccountV1Tests
{
    // ==================== 公共工具 ====================

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static void SetUserClaims(ControllerBase controller, int userId, string role = "parent")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    private static void SetEnv(string key, string? value)
    {
        Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>临时设置环境变量，测试后恢复原值</summary>
    private static IDisposable UseEnv(string key, string? value)
    {
        var original = Environment.GetEnvironmentVariable(key);
        SetEnv(key, value);
        return new RestoreEnv(key, original);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _key;
        private readonly string? _original;
        public RestoreEnv(string key, string? original) { _key = key; _original = original; }
        public void Dispose() => SetEnv(_key, _original);
    }

    /// <summary>哈希器桩：任意密码对账成功；HashPassword 返回固定值</summary>
    private static Mock<IPasswordHasher> CreateHasherMock(bool verifyResult = true)
    {
        var mock = new Mock<IPasswordHasher>();
        mock.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(verifyResult);
        mock.Setup(h => h.HashPassword(It.IsAny<string>())).Returns(("hashed", "salt"));
        return mock;
    }

    private static Mock<IMailSender> CreateMailMock(bool configured = true, bool sendOk = true)
    {
        var mock = new Mock<IMailSender>();
        mock.SetupGet(m => m.IsConfigured).Returns(configured);
        mock.Setup(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((sendOk, sendOk ? "" : "SMTP 连接失败"));
        return mock;
    }

    private static AuthController CreateAuthController(
        AppDbContext db,
        VerificationCodeStore codes,
        ActionTokenStore tokens,
        IMailSender mail,
        Mock<IPasswordHasher>? hasherMock = null,
        Mock<IJwtService>? jwtMock = null)
    {
        hasherMock ??= CreateHasherMock();
        jwtMock ??= new Mock<IJwtService>();
        jwtMock.Setup(j => j.GenerateTokens(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(("access", "refresh", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(7)));

        var config = new ConfigurationBuilder().Build();
        return new AuthController(db, hasherMock.Object, jwtMock.Object, new TicketStore(),
            codes, tokens, mail, NullLogger<AuthController>.Instance, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // ==================== VerificationCodeStore ====================

    [Fact]
    public void CodeStore_Issue_VerifyConsume_SingleUse()
    {
        var store = new VerificationCodeStore();
        var code = store.Issue("a@b.com", "register");

        Assert.True(store.HasPending("a@b.com", "register"));
        Assert.True(store.VerifyAndConsume("a@b.com", "register", code));
        // 单码单用：第二次验证失败
        Assert.False(store.VerifyAndConsume("a@b.com", "register", code));
        Assert.False(store.HasPending("a@b.com", "register"));
    }

    [Fact]
    public void CodeStore_Reissue_InvalidatesOldCode()
    {
        var store = new VerificationCodeStore();
        var first = store.Issue("a@b.com", "login");
        var second = store.Issue("a@b.com", "login");

        Assert.NotEqual(first, second);
        // 旧码已作废
        Assert.False(store.VerifyAndConsume("a@b.com", "login", first));
        Assert.True(store.VerifyAndConsume("a@b.com", "login", second));
    }

    [Fact]
    public void CodeStore_Purposes_AreIsolated()
    {
        var store = new VerificationCodeStore();
        var code = store.Issue("a@b.com", "register");
        // 同码不同用途互不干扰（register 码不可用于 login）
        Assert.False(store.VerifyAndConsume("a@b.com", "login", code));
        Assert.True(store.VerifyAndConsume("a@b.com", "register", code));
    }

    // ==================== ActionTokenStore ====================

    [Fact]
    public void ActionTokenStore_VerifyConsume_BindsUserId()
    {
        var store = new ActionTokenStore();
        var token = store.Issue(1);

        // 跨账号使用被拒（防令牌劫持跨用户解绑）
        Assert.False(store.VerifyAndConsume(token, 2));
        Assert.True(store.VerifyAndConsume(token, 1));
        // 单次使用
        Assert.False(store.VerifyAndConsume(token, 1));
    }

    // ==================== SecretCrypto ====================

    [Fact]
    public void SecretCrypto_WithoutMasterKey_EncryptReturnsNull()
    {
        using var _ = UseEnv("XIAOPACAI_MASTER_KEY", null);
        Assert.Null(SecretCrypto.Encrypt("secret"));
        Assert.Null(SecretCrypto.Decrypt("v1:AAAA"));
        Assert.False(SecretCrypto.IsMasterKeyConfigured);
    }

    [Fact]
    public void SecretCrypto_Roundtrip_AndCorruptionSafety()
    {
        var key = new string('a', 64); // 64 位 hex
        using var _ = UseEnv("XIAOPACAI_MASTER_KEY", key);

        var cipher = SecretCrypto.Encrypt("smtp-password-123");
        Assert.NotNull(cipher);
        Assert.StartsWith("v1:", cipher);
        Assert.DoesNotContain("smtp-password-123", cipher); // 密文不含明文
        Assert.Equal("smtp-password-123", SecretCrypto.Decrypt(cipher));

        // 损坏密文 / 非 v1 前缀 → null（调用方按「未设置」处理）
        Assert.Null(SecretCrypto.Decrypt("v1:!!!not-base64!!!"));
        Assert.Null(SecretCrypto.Decrypt("plaintext"));
        Assert.Null(SecretCrypto.Decrypt(null));
    }

    // ==================== AuthController：发码 ====================

    [Fact]
    public async Task EmailCode_MailNotConfigured_Returns503()
    {
        var db = CreateInMemoryDbContext();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var mail = CreateMailMock(configured: false).Object;
        var controller = CreateAuthController(db, codes, tokens, mail);

        var result = await controller.SendEmailCode(new EmailCodeRequest { Email = "a@b.com", Purpose = "register" });

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task EmailCode_LoginPurpose_UnknownEmail_Suppressed()
    {
        // 防枚举：login 用途下未注册邮箱 → 不发信，统一应答成功文案
        var db = CreateInMemoryDbContext();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var mailMock = CreateMailMock();
        var controller = CreateAuthController(db, codes, tokens, mailMock.Object);

        var result = await controller.SendEmailCode(new EmailCodeRequest { Email = "nobody@b.com", Purpose = "login" });

        Assert.IsType<OkObjectResult>(result);
        mailMock.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        Assert.False(codes.HasPending("nobody@b.com", "login"));
    }

    [Fact]
    public async Task EmailCode_RegisterPurpose_NewEmail_SendsCode()
    {
        var db = CreateInMemoryDbContext();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var mailMock = CreateMailMock();
        var controller = CreateAuthController(db, codes, tokens, mailMock.Object);

        var result = await controller.SendEmailCode(new EmailCodeRequest { Email = "new@b.com", Purpose = "register" });

        Assert.IsType<OkObjectResult>(result);
        mailMock.Verify(m => m.SendAsync("new@b.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        Assert.True(codes.HasPending("new@b.com", "register"));
    }

    // ==================== AuthController：注册 ====================

    [Fact]
    public async Task Register_WrongCode_Returns400()
    {
        var db = CreateInMemoryDbContext();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object);

        codes.Issue("new@b.com", "register"); // 真码 ≠ 提交码
        var result = await controller.Register(new RegisterRequest
        {
            Email = "new@b.com",
            Code = "000000",
            Password = "Passw0rd1",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("验证码", bad.Value!.ToString());
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Register_ValidCode_Succeeds()
    {
        var db = CreateInMemoryDbContext();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object);

        var code = codes.Issue("New@B.com", "register"); // 混合大小写 → 归一化后仍可验证
        var result = await controller.Register(new RegisterRequest
        {
            Email = "New@B.com",
            Code = code,
            Password = "Passw0rd1",
        });

        Assert.IsType<OkObjectResult>(result);
        var user = Assert.Single(db.Users);
        Assert.Equal("new@b.com", user.Username); // 小写归一
        Assert.Equal("new@b.com", user.Email);
    }

    [Fact]
    public async Task Register_ExistingEmail_Returns400()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 1, Username = "old@b.com", PasswordHash = "h", PasswordSalt = "s" });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object);

        var code = codes.Issue("old@b.com", "register");
        var result = await controller.Register(new RegisterRequest
        {
            Email = "old@b.com",
            Code = code,
            Password = "Passw0rd1",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("已注册", bad.Value!.ToString());
        Assert.Single(db.Users);
    }

    // ==================== AuthController：验证码登录 ====================

    [Fact]
    public async Task CodeLogin_ValidCode_ReturnsToken()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 7, Username = "u@b.com", PasswordHash = "h", PasswordSalt = "s", Role = "parent", IsActive = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var jwtMock = new Mock<IJwtService>();
        jwtMock.Setup(j => j.GenerateTokens(7, "u@b.com", "parent"))
            .Returns(("access", "refresh", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(7)));
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object, jwtMock: jwtMock);

        var code = codes.Issue("u@b.com", "login");
        var result = await controller.CodeLogin(new CodeLoginRequest { Email = "u@b.com", Code = code });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("access", System.Text.Json.JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public async Task CodeLogin_WrongCode_Returns401()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 7, Username = "u@b.com", PasswordHash = "h", PasswordSalt = "s", Role = "parent", IsActive = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object);

        codes.Issue("u@b.com", "login");
        var result = await controller.CodeLogin(new CodeLoginRequest { Email = "u@b.com", Code = "000000" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // ==================== AuthController：找回密码 ====================

    [Fact]
    public async Task PasswordReset_ValidCode_ChangesPasswordAndRevokesTokens()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 9, Username = "u@b.com", PasswordHash = "old", PasswordSalt = "s", Role = "parent", IsActive = true, MustChangePassword = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var jwtMock = new Mock<IJwtService>();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object, jwtMock: jwtMock);

        var code = codes.Issue("u@b.com", "reset_password");
        var result = await controller.PasswordReset(new PasswordResetRequest
        {
            Email = "u@b.com",
            Code = code,
            NewPassword = "NewPassw0rd",
        });

        Assert.IsType<OkObjectResult>(result);
        var user = await db.Users.FindAsync(9);
        Assert.Equal("hashed", user!.PasswordHash);   // 新哈希已写入
        Assert.False(user.MustChangePassword);
        // 找回成功后吊销该账号全部 Refresh Token（所有设备重新登录）
        jwtMock.Verify(j => j.RevokeAllUserTokens(9), Times.Once);
    }

    [Fact]
    public async Task PasswordReset_WrongCode_Returns400_NoRevoke()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 9, Username = "u@b.com", PasswordHash = "old", PasswordSalt = "s", Role = "parent", IsActive = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var jwtMock = new Mock<IJwtService>();
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object, jwtMock: jwtMock);

        codes.Issue("u@b.com", "reset_password");
        var result = await controller.PasswordReset(new PasswordResetRequest
        {
            Email = "u@b.com",
            Code = "000000",
            NewPassword = "NewPassw0rd",
        });

        Assert.IsType<BadRequestObjectResult>(result);
        var user = await db.Users.FindAsync(9);
        Assert.Equal("old", user!.PasswordHash); // 未改动
        jwtMock.Verify(j => j.RevokeAllUserTokens(It.IsAny<int>()), Times.Never);
    }

    // ==================== AuthController：解绑前置二次验证 ====================

    [Fact]
    public async Task VerifyPassword_Correct_IssuesActionToken()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 3, Username = "u@b.com", PasswordHash = "h", PasswordSalt = "s", Role = "parent", IsActive = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var hasher = CreateHasherMock(verifyResult: true);
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object, hasherMock: hasher);
        SetUserClaims(controller, 3);

        var result = await controller.VerifyPassword(new VerifyPasswordRequest { Password = "right" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var token = payload.GetType().GetProperty("actionToken")!.GetValue(payload) as string;
        Assert.False(string.IsNullOrEmpty(token));
        // 令牌绑定 userId=3，可被解绑消费
        Assert.True(tokens.VerifyAndConsume(token!, 3));
    }

    [Fact]
    public async Task VerifyPassword_Wrong_Returns400()
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User { Id = 3, Username = "u@b.com", PasswordHash = "h", PasswordSalt = "s", Role = "parent", IsActive = true });
        await db.SaveChangesAsync();
        var codes = new VerificationCodeStore();
        var tokens = new ActionTokenStore();
        var hasher = CreateHasherMock(verifyResult: false);
        var controller = CreateAuthController(db, codes, tokens, CreateMailMock().Object, hasherMock: hasher);
        SetUserClaims(controller, 3);

        var result = await controller.VerifyPassword(new VerifyPasswordRequest { Password = "wrong" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ==================== MailConfigController ====================

    private static MailConfigController CreateMailConfigController(AppDbContext db, IMailSender mail)
        => new(db, mail, NullLogger<MailConfigController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task MailConfig_Get_EmptyDb_MasksSecrets()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateMailConfigController(db, CreateMailMock().Object);

        var result = await controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        // Secret 回显仅「已设置/未设置」，绝不包含明文
        Assert.Contains("accessKeySecretMasked", json);
        Assert.DoesNotContain("accessKeySecret", json.Replace("accessKeySecretMasked", ""));
        Assert.DoesNotContain("smtpPassword", json.Replace("smtpPasswordMasked", ""));
    }

    [Fact]
    public async Task MailConfig_Update_SecretWithoutMasterKey_Returns400()
    {
        using var _ = UseEnv("XIAOPACAI_MASTER_KEY", null);
        var db = CreateInMemoryDbContext();
        var controller = CreateMailConfigController(db, CreateMailMock().Object);

        var result = await controller.Update(new MailConfigUpdateRequest
        {
            Channel = "smtp",
            SmtpHost = "smtp.example.com",
            SmtpUser = "postmaster",
            SmtpPassword = "plain-secret", // 主密钥未配置 → 拒存
            FromAddress = "noreply@example.com",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("XIAOPACAI_MASTER_KEY", bad.Value!.ToString());
        Assert.Empty(db.MailConfigs);
    }

    [Fact]
    public async Task MailConfig_Update_WithMasterKey_EncryptsAndMasks()
    {
        using var _ = UseEnv("XIAOPACAI_MASTER_KEY", new string('b', 64));
        var db = CreateInMemoryDbContext();
        var controller = CreateMailConfigController(db, CreateMailMock().Object);

        var result = await controller.Update(new MailConfigUpdateRequest
        {
            Channel = "smtp",
            SmtpHost = "smtp.example.com",
            SmtpPort = 465,
            SmtpUser = "postmaster",
            SmtpPassword = "plain-secret",
            FromAddress = "noreply@example.com",
        });

        Assert.IsType<OkObjectResult>(result);
        var row = Assert.Single(db.MailConfigs);
        Assert.StartsWith("v1:", row.SmtpPasswordEnc);       // 密文入库
        Assert.DoesNotContain("plain-secret", row.SmtpPasswordEnc);

        // GET 脱敏：密码只回「已设置」，明文永不回显
        var get = Assert.IsType<OkObjectResult>(await controller.Get());
        var payload = get.Value!;
        var masked = payload.GetType().GetProperty("SmtpPasswordMasked")!.GetValue(payload) as string;
        Assert.Equal("已设置", masked);
    }

    [Fact]
    public async Task MailConfig_Update_BlankSecret_KeepsExisting()
    {
        using var _ = UseEnv("XIAOPACAI_MASTER_KEY", new string('c', 64));
        var db = CreateInMemoryDbContext();
        db.MailConfigs.Add(new MailConfig
        {
            Id = 1, Channel = "smtp", SmtpHost = "smtp.example.com", SmtpUser = "postmaster",
            SmtpPasswordEnc = SecretCrypto.Encrypt("old-secret")!, FromAddress = "noreply@example.com",
        });
        await db.SaveChangesAsync();
        var controller = CreateMailConfigController(db, CreateMailMock().Object);

        // Secret 留空 = 不变
        var result = await controller.Update(new MailConfigUpdateRequest { SmtpPort = 465 });

        Assert.IsType<OkObjectResult>(result);
        var row = await db.MailConfigs.FirstAsync();
        Assert.Equal("old-secret", SecretCrypto.Decrypt(row.SmtpPasswordEnc));
        Assert.Equal(465, row.SmtpPort);
    }

    [Fact]
    public async Task MailConfig_Test_RecordsResult()
    {
        var db = CreateInMemoryDbContext();
        var mailMock = CreateMailMock(sendOk: true);
        var controller = CreateMailConfigController(db, mailMock.Object);

        var result = await controller.Test(new MailConfigTestRequest { To = "me@example.com" });

        Assert.IsType<OkObjectResult>(result);
        mailMock.Verify(m => m.SendAsync("me@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        // 审计落库（不含 Secret）
        Assert.Contains(db.AuditLogs, l => l.Action == "mail_config_test");
    }

    // ==================== MailSender 配置优先级 ====================

    private static MailSender CreateMailSender(AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new MailSender(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailSender>.Instance);
    }

    [Fact]
    public void MailSender_NoConfig_NotConfigured()
    {
        using var _ = UseEnv("MAIL_CHANNEL", null);
        var db = CreateInMemoryDbContext();
        var sender = CreateMailSender(db);
        Assert.False(sender.IsConfigured);
    }

    [Fact]
    public void MailSender_EnvFallback_Works()
    {
        using var e1 = UseEnv("MAIL_CHANNEL", "smtp");
        using var e2 = UseEnv("MAIL_FROM_ADDRESS", "noreply@example.com");
        using var e3 = UseEnv("MAIL_SMTP_HOST", "smtp.example.com");
        using var e4 = UseEnv("MAIL_SMTP_USER", "postmaster");
        using var e5 = UseEnv("MAIL_SMTP_PASSWORD", "env-secret");

        var sender = CreateMailSender(CreateInMemoryDbContext());
        Assert.True(sender.IsConfigured);
    }

    [Fact]
    public void MailSender_DbConfigPriority_BrokenSecret_FallsBackToEnv()
    {
        // DB 配置存在但 Secret 密文损坏（解密 null）→ 视为不可用 → 回退环境变量
        using var e1 = UseEnv("MAIL_CHANNEL", "smtp");
        using var e2 = UseEnv("MAIL_FROM_ADDRESS", "noreply@example.com");
        using var e3 = UseEnv("MAIL_SMTP_HOST", "smtp.example.com");
        using var e4 = UseEnv("MAIL_SMTP_USER", "postmaster");
        using var e5 = UseEnv("MAIL_SMTP_PASSWORD", "env-secret");
        using var ek = UseEnv("XIAOPACAI_MASTER_KEY", null);

        var db = CreateInMemoryDbContext();
        db.MailConfigs.Add(new MailConfig
        {
            Id = 1, Channel = "smtp", SmtpHost = "smtp.example.com", SmtpUser = "postmaster",
            SmtpPasswordEnc = "v1:broken-cipher-text", FromAddress = "noreply@example.com",
        });
        db.SaveChanges();

        var sender = CreateMailSender(db);
        Assert.True(sender.IsConfigured); // 环境变量兜底生效
    }

    [Fact]
    public void MailSender_DbConfigPriority_DbWins()
    {
        // DB 配置完整可用 → 优先于环境变量
        using var ek = UseEnv("XIAOPACAI_MASTER_KEY", new string('d', 64));
        using var e1 = UseEnv("MAIL_CHANNEL", null); // 环境变量缺省，仅 DB 生效
        var db = CreateInMemoryDbContext();
        db.MailConfigs.Add(new MailConfig
        {
            Id = 1, Channel = "smtp", SmtpHost = "smtp.example.com", SmtpUser = "postmaster",
            SmtpPasswordEnc = SecretCrypto.Encrypt("db-secret")!, FromAddress = "noreply@example.com",
        });
        db.SaveChanges();

        var sender = CreateMailSender(db);
        Assert.True(sender.IsConfigured);
    }
}
