using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

// ========== [TASK-ACCOUNT-V1-MAILCONFIG] 管理员邮件设置 ==========

/// <summary>
/// 邮件配置更新请求（admin）
/// Secret 字段留空 = 保持不变；显式传值时要求服务端已配置主密钥（XIAOPACAI_MASTER_KEY），
/// 否则拒绝保存（禁止明文入库）。
/// </summary>
public class MailConfigUpdateRequest
{
    /// <summary>通道：api | smtp | ""（清空配置）</summary>
    [MaxLength(16)]
    [RegularExpression(@"^(api|smtp|)$", ErrorMessage = "通道不合法")]
    public string? Channel { get; set; }

    [MaxLength(128)]
    public string? AccessKeyId { get; set; }

    /// <summary>DirectMail AccessKey Secret（留空=不变，非机密不入库）</summary>
    [MaxLength(512)]
    public string? AccessKeySecret { get; set; }

    [MaxLength(128)]
    public string? FromAddress { get; set; }

    [MaxLength(64)]
    public string? FromName { get; set; }

    [MaxLength(128)]
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int? SmtpPort { get; set; }

    [MaxLength(128)]
    public string? SmtpUser { get; set; }

    /// <summary>SMTP 密码（留空=不变）</summary>
    [MaxLength(512)]
    public string? SmtpPassword { get; set; }

    public bool? SmtpUseSsl { get; set; }
}

/// <summary>
/// 邮件配置测试发送请求（admin）
/// </summary>
public class MailConfigTestRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(128)]
    public string To { get; set; } = string.Empty;
}

/// <summary>
/// 邮件配置响应（admin；Secret 仅回显「已设置」/「未设置」，永不回明文）
/// </summary>
public class MailConfigResponse
{
    public string Channel { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>脱敏：""=未设置，"已设置"=已配置（A7）</summary>
    public string AccessKeySecretMasked { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;

    /// <summary>脱敏：""=未设置，"已设置"=已配置</summary>
    public string SmtpPasswordMasked { get; set; } = string.Empty;

    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>当前配置是否完整可用（通道 + 发信地址 + 对应凭据齐全）</summary>
    public bool IsConfigured { get; set; }

    /// <summary>服务端主密钥（XIAOPACAI_MASTER_KEY）是否已配置；未配置时前端禁止提交 Secret</summary>
    public bool MasterKeyConfigured { get; set; }

    public bool? LastTestOk { get; set; }
    public string? LastTestDetail { get; set; }
    public DateTime? LastTestAt { get; set; }
}
