using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// [TASK-ACCOUNT-V1-MAILCONFIG] 管理员邮件设置（仅 admin）
///
/// GET   /api/admin/mail-config        查看配置（Secret 脱敏）
/// PUT   /api/admin/mail-config        保存配置（保存即热生效；Secret 留空=不变）
/// POST  /api/admin/mail-config/test   发送测试邮件（写入 LastTest*）
/// </summary>
[ApiController]
[Route("api/admin/mail-config")]
[Authorize(Policy = "AdminOnly")]
public class MailConfigController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMailSender _mail;
    private readonly ILogger<MailConfigController> _logger;

    public MailConfigController(AppDbContext db, IMailSender mail, ILogger<MailConfigController> logger)
    {
        _db = db;
        _mail = mail;
        _logger = logger;
    }

    /// <summary>查看配置（Secret 永不回明文）</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var row = await _db.MailConfigs.AsNoTracking().FirstOrDefaultAsync();
        return Ok(BuildResponse(row));
    }

    /// <summary>保存配置（热生效）</summary>
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] MailConfigUpdateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // 限速：10 次/小时（按用户，防配置接口被刷）
        var userId = GetUserId();
        if (userId != null && RequestRateLimiter.IsBlocked($"mailconfig:user:{userId}", 10, 3600))
            return StatusCode(429, new { error = "操作过于频繁，请 1 小时后再试" });

        var hasSecretUpdate = !string.IsNullOrEmpty(request.AccessKeySecret) ||
                              !string.IsNullOrEmpty(request.SmtpPassword);
        if (hasSecretUpdate && !SecretCrypto.IsMasterKeyConfigured)
        {
            _logger.LogWarning("[MailConfig] 未配置主密钥却尝试保存 Secret");
            return BadRequest(new { error = "服务端未配置加密主密钥（XIAOPACAI_MASTER_KEY），无法保存密钥类配置" });
        }

        var row = await _db.MailConfigs.FirstOrDefaultAsync();
        var isNew = row == null;
        row ??= new MailConfig { Id = 1 };

        // 非 Secret 字段：显式传值即更新（含清空）
        if (request.Channel != null) row.Channel = request.Channel.Trim().ToLower();
        if (request.AccessKeyId != null) row.AccessKeyId = request.AccessKeyId.Trim();
        if (request.FromAddress != null) row.FromAddress = request.FromAddress.Trim();
        if (request.FromName != null) row.FromName = request.FromName.Trim();
        if (request.SmtpHost != null) row.SmtpHost = request.SmtpHost.Trim();
        if (request.SmtpPort.HasValue) row.SmtpPort = request.SmtpPort.Value;
        if (request.SmtpUser != null) row.SmtpUser = request.SmtpUser.Trim();
        if (request.SmtpUseSsl.HasValue) row.SmtpUseSsl = request.SmtpUseSsl.Value;

        // Secret 字段：留空=不变；传值则加密入库
        if (!string.IsNullOrEmpty(request.AccessKeySecret))
        {
            var enc = SecretCrypto.Encrypt(request.AccessKeySecret);
            if (enc == null)
                return BadRequest(new { error = "服务端未配置加密主密钥（XIAOPACAI_MASTER_KEY），无法保存密钥类配置" });
            row.AccessKeySecretEnc = enc;
        }
        if (!string.IsNullOrEmpty(request.SmtpPassword))
        {
            var enc = SecretCrypto.Encrypt(request.SmtpPassword);
            if (enc == null)
                return BadRequest(new { error = "服务端未配置加密主密钥（XIAOPACAI_MASTER_KEY），无法保存密钥类配置" });
            row.SmtpPasswordEnc = enc;
        }

        row.UpdatedAt = DateTime.UtcNow;
        if (isNew)
            _db.MailConfigs.Add(row);
        await _db.SaveChangesAsync();

        // 审计（不含任何 Secret 明文）
        await AuditAsync("mail_config_update", userId,
            $"{{\"channel\":\"{row.Channel}\",\"configured\":{row.IsConfigured}}}");

        _logger.LogInformation("[MailConfig] 配置已更新（channel={Ch}，configured={C}）", row.Channel, row.IsConfigured);
        return Ok(BuildResponse(row));
    }

    /// <summary>发送测试邮件（使用当前已保存配置）</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] MailConfigTestRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetUserId();
        // 限速：5 次/小时（按用户，防测试接口被刷成发信机）
        if (userId != null && RequestRateLimiter.IsBlocked($"mailconfig-test:user:{userId}", 5, 3600))
            return StatusCode(429, new { error = "测试过于频繁，请 1 小时后再试" });

        var to = request.To.Trim().ToLower();
        var (ok, error) = await _mail.SendAsync(to, "【小趴菜】邮件发送测试", BuildTestEmailHtml());

        // 记录最近测试结果
        var row = await _db.MailConfigs.FirstOrDefaultAsync();
        if (row != null)
        {
            row.LastTestOk = ok;
            row.LastTestDetail = ok ? $"发送成功（{to}）" : Truncate(error, 500);
            row.LastTestAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        await AuditAsync("mail_config_test", userId,
            $"{{\"to\":\"{to}\",\"ok\":{ok.ToString().ToLower()}}}");

        if (!ok)
            return BadRequest(new { error = $"测试发送失败：{error}" });
        return Ok(new { message = "测试邮件已发送" });
    }

    private static MailConfigResponse BuildResponse(MailConfig? row)
    {
        var resp = new MailConfigResponse { MasterKeyConfigured = SecretCrypto.IsMasterKeyConfigured };
        if (row == null) return resp;

        resp.Channel = row.Channel;
        resp.AccessKeyId = row.AccessKeyId;
        resp.AccessKeySecretMasked = string.IsNullOrEmpty(row.AccessKeySecretEnc) ? "" : "已设置";
        resp.FromAddress = row.FromAddress;
        resp.FromName = row.FromName;
        resp.SmtpHost = row.SmtpHost;
        resp.SmtpPort = row.SmtpPort;
        resp.SmtpUser = row.SmtpUser;
        resp.SmtpPasswordMasked = string.IsNullOrEmpty(row.SmtpPasswordEnc) ? "" : "已设置";
        resp.SmtpUseSsl = row.SmtpUseSsl;
        resp.IsConfigured = row.IsConfigured;
        resp.LastTestOk = row.LastTestOk;
        resp.LastTestDetail = row.LastTestDetail;
        resp.LastTestAt = row.LastTestAt;
        return resp;
    }

    private static string BuildTestEmailHtml() =>
        """
        <div style="max-width:480px;margin:0 auto;font-family:'Microsoft YaHei',sans-serif;color:#303133">
          <h2 style="color:#67C23A">邮件配置测试成功</h2>
          <p>这是一封来自小趴菜管理后台的测试邮件，说明当前邮件发送配置可用。</p>
          <p style="color:#909399;font-size:13px">若您未执行测试操作，请忽略本邮件。</p>
        </div>
        """;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "...");

    private async Task AuditAsync(string action, int? userId, string detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            TargetType = "mail_config",
            Detail = detail,
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            UserAgent = HttpContext?.Request?.Headers.UserAgent.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
