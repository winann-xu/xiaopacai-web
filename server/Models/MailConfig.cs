using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-ACCOUNT-V1-MAILCONFIG] 邮件发送配置（单行表，admin「系统设置→邮件设置」页维护）
///
/// 配置来源优先级：本表（数据库）→ 环境变量 MAIL_* 兜底 → 皆无则注册/找回发码接口明确报错。
/// Secret 类字段（AccessKeySecret / SmtpPassword）经服务端主密钥（环境变量
/// XIAOPACAI_MASTER_KEY）AES-256-GCM 加密后入库，禁止明文；GET 接口永不回显明文。
/// </summary>
[Table("mail_config")]
public class MailConfig
{
    /// <summary>单行表：恒为 1</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;

    /// <summary>发送通道：api（阿里云 DirectMail API）| smtp | ""（未配置）</summary>
    [MaxLength(16)]
    public string Channel { get; set; } = string.Empty;

    // ---- DirectMail API 模式 ----

    /// <summary>RAM AccessKey ID（明文存储，非机密）</summary>
    [MaxLength(128)]
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>RAM AccessKey Secret（AES-GCM 密文；明文永不入库/入响应）</summary>
    [MaxLength(512)]
    public string AccessKeySecretEnc { get; set; } = string.Empty;

    // ---- 发信地址（两通道共用） ----

    [MaxLength(128)]
    public string FromAddress { get; set; } = string.Empty;

    [MaxLength(64)]
    public string FromName { get; set; } = string.Empty;

    // ---- SMTP 模式 ----

    [MaxLength(128)]
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    [MaxLength(128)]
    public string SmtpUser { get; set; } = string.Empty;

    /// <summary>SMTP 密码（AES-GCM 密文）</summary>
    [MaxLength(512)]
    public string SmtpPasswordEnc { get; set; } = string.Empty;

    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>最近一次测试发送结果（成功 true / 失败 false；null=从未测试）</summary>
    public bool? LastTestOk { get; set; }

    [MaxLength(512)]
    public string? LastTestDetail { get; set; }

    public DateTime? LastTestAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>是否已配置完整（通道 + 发信地址 + 对应通道凭据齐全）</summary>
    [NotMapped]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Channel) &&
        !string.IsNullOrWhiteSpace(FromAddress) &&
        (Channel == "api"
            ? !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(AccessKeySecretEnc)
            : !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpUser) &&
              !string.IsNullOrWhiteSpace(SmtpPasswordEnc));
}
