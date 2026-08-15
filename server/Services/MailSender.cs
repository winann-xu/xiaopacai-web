using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Services;

/// <summary>
/// [TASK-ACCOUNT-V1] 邮件发送抽象（注册验证码 / 验证码登录 / 找回密码 / 测试邮件）
/// </summary>
public interface IMailSender
{
    /// <summary>是否已配置完整（未配置时注册/找回发码接口返回明确错误）</summary>
    bool IsConfigured { get; }

    /// <summary>发送一封邮件；成功返回 true，失败返回 false（错误信息见 out error）</summary>
    Task<(bool ok, string error)> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// [TASK-ACCOUNT-V1-MAILCONFIG] 邮件发送器（配置热加载）
///
/// 配置来源优先级：数据库 mail_config（admin 页配置）→ 环境变量 MAIL_* 兜底。
/// Secret 解密失败按未配置处理（宁可不可用，不用错误密钥发信）。
/// </summary>
public class MailSender : IMailSender
{
    private sealed class Resolved
    {
        public string Channel = string.Empty;   // api | smtp | ""
        public string AccessKeyId = string.Empty;
        public string AccessKeySecret = string.Empty;
        public string FromAddress = string.Empty;
        public string FromName = string.Empty;
        public string SmtpHost = string.Empty;
        public int SmtpPort = 587;
        public string SmtpUser = string.Empty;
        public string SmtpPassword = string.Empty;
        public bool SmtpUseSsl = true;

        public bool Ready =>
            !string.IsNullOrWhiteSpace(Channel) &&
            !string.IsNullOrWhiteSpace(FromAddress) &&
            (Channel == "api"
                ? !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(AccessKeySecret)
                : !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpUser) &&
                  !string.IsNullOrWhiteSpace(SmtpPassword));
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MailSender> _logger;

    public MailSender(IServiceScopeFactory scopeFactory, ILogger<MailSender> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>当前是否已配置（每次读取实时解析，保存后立即生效）</summary>
    public bool IsConfigured => Resolve().Ready;

    public async Task<(bool ok, string error)> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (!cfg.Ready)
            return (false, "邮件服务未配置，请联系管理员在「系统设置→邮件设置」完成配置");

        try
        {
            var fromName = string.IsNullOrWhiteSpace(cfg.FromName) ? "小趴菜" : cfg.FromName;
            if (cfg.Channel == "api")
            {
                await SendViaDirectMailAsync(cfg, to, $"{fromName} <{cfg.FromAddress}>", subject, htmlBody, ct);
            }
            else
            {
                await SendViaSmtpAsync(cfg, to, subject, htmlBody, ct);
            }
            _logger.LogInformation("[Mail] 已发送至 {To}（channel={Ch}）", to, cfg.Channel);
            return (true, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mail] 发送失败: {To}（channel={Ch}）", to, cfg.Channel);
            return (false, ex.Message);
        }
    }

    /// <summary>解析当前配置（DB 优先 → 环境变量兜底）</summary>
    private Resolved Resolve()
    {
        var r = new Resolved();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = db.MailConfigs.AsNoTracking().FirstOrDefault();
            if (row != null)
            {
                r.Channel = row.Channel;
                r.AccessKeyId = row.AccessKeyId;
                r.AccessKeySecret = SecretCrypto.Decrypt(row.AccessKeySecretEnc) ?? string.Empty;
                r.FromAddress = row.FromAddress;
                r.FromName = row.FromName;
                r.SmtpHost = row.SmtpHost;
                r.SmtpPort = row.SmtpPort;
                r.SmtpUser = row.SmtpUser;
                r.SmtpPassword = SecretCrypto.Decrypt(row.SmtpPasswordEnc) ?? string.Empty;
                r.SmtpUseSsl = row.SmtpUseSsl;
                if (r.Ready) return r;  // 数据库配置完整则优先生效
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mail] 读取数据库邮件配置失败，回退环境变量");
        }

        // 环境变量兜底：MAIL_CHANNEL / MAIL_ACCESS_KEY_ID / MAIL_ACCESS_KEY_SECRET /
        // MAIL_FROM_ADDRESS / MAIL_FROM_NAME / MAIL_SMTP_HOST / MAIL_SMTP_PORT /
        // MAIL_SMTP_USER / MAIL_SMTP_PASSWORD / MAIL_SMTP_USE_SSL
        r.Channel = Environment.GetEnvironmentVariable("MAIL_CHANNEL") ?? string.Empty;
        r.AccessKeyId = Environment.GetEnvironmentVariable("MAIL_ACCESS_KEY_ID") ?? string.Empty;
        r.AccessKeySecret = Environment.GetEnvironmentVariable("MAIL_ACCESS_KEY_SECRET") ?? string.Empty;
        r.FromAddress = Environment.GetEnvironmentVariable("MAIL_FROM_ADDRESS") ?? string.Empty;
        r.FromName = Environment.GetEnvironmentVariable("MAIL_FROM_NAME") ?? string.Empty;
        r.SmtpHost = Environment.GetEnvironmentVariable("MAIL_SMTP_HOST") ?? string.Empty;
        r.SmtpUser = Environment.GetEnvironmentVariable("MAIL_SMTP_USER") ?? string.Empty;
        r.SmtpPassword = Environment.GetEnvironmentVariable("MAIL_SMTP_PASSWORD") ?? string.Empty;
        if (int.TryParse(Environment.GetEnvironmentVariable("MAIL_SMTP_PORT"), out var port))
            r.SmtpPort = port;
        if (Environment.GetEnvironmentVariable("MAIL_SMTP_USE_SSL") is { } ssl)
            r.SmtpUseSsl = !string.Equals(ssl, "false", StringComparison.OrdinalIgnoreCase);
        return r;
    }

    /// <summary>SMTP 通道发送</summary>
    private static async Task SendViaSmtpAsync(Resolved cfg, string to, string subject, string htmlBody, CancellationToken ct)
    {
        using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
        {
            EnableSsl = cfg.SmtpUseSsl,
            Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        using var mail = new MailMessage
        {
            From = new MailAddress(cfg.FromAddress, cfg.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        mail.To.Add(to);
        await client.SendMailAsync(mail, ct);
    }

    /// <summary>
    /// 阿里云 DirectMail API 通道（SingleSendMail，RPC 签名 HMAC-SHA1）
    /// </summary>
    private static async Task SendViaDirectMailAsync(
        Resolved cfg, string to, string replyToAddress, string subject, string htmlBody, CancellationToken ct)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action"] = "SingleSendMail",
            ["Version"] = "2015-11-23",
            ["Format"] = "JSON",
            ["AccessKeyId"] = cfg.AccessKeyId,
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureVersion"] = "1.0",
            ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["AccountName"] = cfg.FromAddress,
            ["ReplyToAddress"] = "true",
            ["AddressType"] = "1",
            ["ToAddress"] = to,
            ["Subject"] = subject,
            ["HtmlBody"] = htmlBody,
            ["FromAlias"] = cfg.FromName,
        };

        var canonical = string.Join("&", parameters.Select(kv =>
            PercentEncode(kv.Key) + "=" + PercentEncode(kv.Value)));
        var stringToSign = "POST&%2F&" + PercentEncode(canonical);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(cfg.AccessKeySecret + "&"));
        parameters["Signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        using var content = new FormUrlEncodedContent(parameters);
        using var resp = await Http.PostAsync("https://dm.aliyuncs.com/", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode || body.Contains("\"Code\""))
            throw new InvalidOperationException($"DirectMail 发送失败（{(int)resp.StatusCode}）：{Truncate(body, 300)}");
    }

    private static string PercentEncode(string value) =>
        Uri.EscapeDataString(value)
           .Replace("+", "%20").Replace("*", "%2A").Replace("%7E", "~");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
