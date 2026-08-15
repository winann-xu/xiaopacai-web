using XiaopacaiWeb.Security;
using Xunit;

namespace XiaopacaiWeb.Tests.Security;

/// <summary>
/// [TASK-MILESTONE-V3] 需求 14：日志脱敏单元测试（服务端第二层打码）
///
/// 与 Android AppLog.maskSecrets 同模式：密码/令牌赋值、验证码数字、
/// 裸 JWT、64 位 hex；普通文本放行。
/// </summary>
public class AppLogSanitizerTests
{
    [Fact]
    public void MaskSecrets_MasksSecretAssignments()
    {
        Assert.Equal("password=***", AppLogSanitizer.MaskSecrets("password=abc123"));
        Assert.Equal("token: ***", AppLogSanitizer.MaskSecrets("token: abc.def"));
        Assert.Equal("api_key=***", AppLogSanitizer.MaskSecrets("api_key=sk-1234567890"));
        Assert.Equal("secret=***，连接失败", AppLogSanitizer.MaskSecrets("secret=xyz，连接失败"));
        Assert.Equal("PASSWORD=***", AppLogSanitizer.MaskSecrets("PASSWORD=hunter2"));
    }

    [Fact]
    public void MaskSecrets_MasksVerificationCodes()
    {
        Assert.Equal("验证码 ***，5 分钟内有效", AppLogSanitizer.MaskSecrets("验证码 123456，5 分钟内有效"));
        Assert.Equal("verification code: ***", AppLogSanitizer.MaskSecrets("verification code: 8888"));
        Assert.Equal("校验码***", AppLogSanitizer.MaskSecrets("校验码123456"));
        Assert.Equal("SMS code ***", AppLogSanitizer.MaskSecrets("SMS code 246810"));
        // 裸 "code:" 无验证码语义（HTTP code 等），不误伤
        Assert.Equal("HTTP code 500", AppLogSanitizer.MaskSecrets("HTTP code 500"));
    }

    [Fact]
    public void MaskSecrets_MasksJwtAndHex64()
    {
        Assert.Equal("***",
            AppLogSanitizer.MaskSecrets("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sigAaaaAaaaAaaaAaaaAaaa"));
        Assert.Equal("***", AppLogSanitizer.MaskSecrets(new string('a', 64)));
        Assert.Equal("密钥 ***", AppLogSanitizer.MaskSecrets($"密钥 {new string('f', 64)}"));
    }

    [Fact]
    public void MaskSecrets_KeepsNormalText()
    {
        var text = "设备列表已同步: 3 台";
        Assert.Equal(text, AppLogSanitizer.MaskSecrets(text));
        Assert.Equal("心跳 15 分钟", AppLogSanitizer.MaskSecrets("心跳 15 分钟"));
    }

    [Fact]
    public void MaskSecrets_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AppLogSanitizer.MaskSecrets(null!));
        Assert.Equal(string.Empty, AppLogSanitizer.MaskSecrets(""));
    }

    [Fact]
    public void Truncate_TrimsOverlong()
    {
        Assert.Equal("abc", AppLogSanitizer.Truncate("abcdef", 3));
        Assert.Equal("abc", AppLogSanitizer.Truncate("abc", 3));
        Assert.Null(AppLogSanitizer.Truncate(null, 3));
        Assert.Null(AppLogSanitizer.Truncate("", 3));
    }
}
