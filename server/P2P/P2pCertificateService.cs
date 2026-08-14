using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XiaopacaiWeb.P2P;

/// <summary>
/// P2P TLS 自签名证书服务 — LEGACY-e 方案（复用 2.0 证书持久化逻辑）
///
/// 保障证书指纹在重启后稳定不变，确保儿童端首次配对后后续重连无需重新配对待。
/// </summary>
public class P2pCertificateService
{
    private readonly string _certDir;
    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly string? _configuredPassword;
    private readonly ILogger<P2pCertificateService> _logger;

    private X509Certificate2? _certificate;

    public P2pCertificateService(IConfiguration configuration, ILogger<P2pCertificateService> logger)
    {
        _logger = logger;

        // [SEC-P2] 接入 P2P:CertPassword 配置（此前死配置）：
        // 配置后 PFX 密码仅存环境变量，不再写 .key 文件（红线 K4：密钥不落盘）
        _configuredPassword = string.IsNullOrEmpty(configuration["P2P:CertPassword"])
            ? null
            : configuration["P2P:CertPassword"];

        // 证书目录优先从配置读取，否则使用 Data/certs/
        var configuredPath = configuration["P2P:CertPath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            // 解析相对路径为项目目录下绝对路径
            var basePath = AppContext.BaseDirectory;
            _certPath = Path.GetFullPath(Path.Combine(basePath, configuredPath));
            _certDir = Path.GetDirectoryName(_certPath)!;
            _keyPath = Path.ChangeExtension(_certPath, ".key");
        }
        else
        {
            _certDir = Path.Combine(AppContext.BaseDirectory, "Data", "certs");
            _certPath = Path.Combine(_certDir, "server.pfx");
            _keyPath = Path.Combine(_certDir, "server.pfx.key");
        }
    }

    /// <summary>
    /// 获取或创建 TLS 证书（首次运行时生成自签名证书并持久化）
    /// </summary>
    public X509Certificate2 GetOrCreateCertificate()
    {
        if (_certificate != null)
            return _certificate;

        // 确保证书目录存在
        if (!Directory.Exists(_certDir))
            Directory.CreateDirectory(_certDir);

        // 尝试从磁盘加载已有证书（LEGACY-e：指纹稳定）
        // [SEC-P2] 优先用配置密码（环境变量注入）；未配置则回退 .key 文件（兼容 2.0 旧证书）
        if (File.Exists(_certPath) && (_configuredPassword != null || File.Exists(_keyPath)))
        {
            try
            {
                var password = _configuredPassword ?? File.ReadAllText(_keyPath).Trim();
                _certificate = new X509Certificate2(_certPath, password);
                _logger.LogInformation("[P2P-Cert] 已加载持久化证书: {Path}, 指纹={Fingerprint}",
                    _certPath, ComputeFingerprint(_certificate));
                return _certificate;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[P2P-Cert] 加载持久化证书失败，将重新生成");
            }
        }

        // 生成新证书
        _certificate = CreateSelfSignedCertificate();
        PersistCertificate(_certificate);

        return _certificate;
    }

    /// <summary>
    /// 计算证书 SHA-256 指纹（十六进制小写，与 2.0 儿童端验证一致）
    /// </summary>
    public static string ComputeFingerprint(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// 获取当前证书指纹
    /// </summary>
    public string? GetFingerprint()
    {
        if (_certificate == null) return null;
        return ComputeFingerprint(_certificate);
    }

    // ========== 内部实现 ==========

    /// <summary>
    /// 持久化证书到磁盘（pfx + 密钥文件，LEGACY-e 方案）
    /// </summary>
    private void PersistCertificate(X509Certificate2 cert)
    {
        try
        {
            // [SEC-P2] 配置了 CertPassword 时：密码仅存环境变量，.key 文件不落盘（红线 K4）；
            // 未配置（开发模式）时：随机密码 + .key 文件（兼容 2.0 方案）
            var password = _configuredPassword ?? Guid.NewGuid().ToString("N");
            var pfxBytes = cert.Export(X509ContentType.Pfx, password);

            File.WriteAllBytes(_certPath, pfxBytes);
            if (_configuredPassword == null)
                File.WriteAllText(_keyPath, password);

            // 隐藏密钥文件（与 2.0 风格一致）
            if (_configuredPassword == null && !OperatingSystem.IsWindows())
            {
                // Linux 下设置文件权限（仅 owner 可读写）
                File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else if (_configuredPassword == null)
            {
                File.SetAttributes(_keyPath, FileAttributes.Hidden);
            }

            _logger.LogInformation("[P2P-Cert] 证书已持久化: {Path}, 指纹={Fingerprint}",
                _certPath, ComputeFingerprint(cert));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P-Cert] 证书持久化失败");
        }
    }

    /// <summary>
    /// 创建自签名 X.509 证书（RSA-2048, SHA-256, ServerAuth EKU）
    /// 与 2.0 LEGACY-e CreateSelfSignedCertificate 一致
    /// </summary>
    private X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);

        var subject = new X500DistinguishedName("CN=xiaopacai-web-local");

        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // 基本约束
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        // 密钥用法
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
                critical: true));

        // 增强密钥用法（服务器认证）
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
                critical: true));

        // 使用者替代名称（SAN）
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName("xiaopacai.local");

        // 添加所有活跃网络接口的 IPv4 地址（支持 LAN 直连）
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        sanBuilder.AddIpAddress(addr.Address);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[P2P-Cert] 获取网络接口 IP 失败，SAN 仅包含回环地址");
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        // 有效期：−1 天到 +1 年（与 2.0 一致，容忍时钟偏差）
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(365);

        var cert = request.CreateSelfSigned(notBefore, notAfter);

        // 重新导入为可导出的 PFX（支持持久化）
        var pfxBytes = cert.Export(X509ContentType.Pfx, "temp");
        cert.Dispose();

        var exportableCert = new X509Certificate2(pfxBytes, "temp",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        _logger.LogInformation("[P2P-Cert] 已生成自签名证书: CN=xiaopacai-web-local, 指纹={Fingerprint}",
            ComputeFingerprint(exportableCert));

        return exportableCert;
    }
}
