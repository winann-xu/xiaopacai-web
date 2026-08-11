using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.P2P;
using Xunit;

namespace XiaopacaiWeb.Tests.P2P;

/// <summary>
/// P2P 证书服务测试 — 自签名证书生成 / PFX 持久化 / 指纹稳定性
///
/// 覆盖：
/// - 证书生成（CN、RSA-2048、有效期、serverAuth EKU、SAN）
/// - 指纹计算（SHA-256 十六进制小写 64 字符）
/// - 持久化（PFX + 密钥文件落盘）
/// - 指纹跨实例稳定（LEGACY-e：重启后指纹不变，儿童端无需重新配对待）
/// </summary>
public class P2pCertificateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _certPath;

    public P2pCertificateServiceTests()
    {
        // 每个测试使用独立临时目录，避免证书文件互相干扰
        _tempDir = Path.Combine(Path.GetTempPath(), "xpcd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _certPath = Path.Combine(_tempDir, "server.pfx");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>
    /// 创建指向临时目录的证书服务实例
    /// </summary>
    private P2pCertificateService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["P2P:CertPath"] = _certPath, // 绝对路径，直接落盘到临时目录
            })
            .Build();

        return new P2pCertificateService(config, NullLogger<P2pCertificateService>.Instance);
    }

    // ==================== 证书生成 ====================

    [Fact]
    public void GenerateCertificate_ReturnsSelfSignedCertificate()
    {
        var service = CreateService();

        var cert = service.GetOrCreateCertificate();

        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey, "证书必须包含私钥（服务端 TLS 使用）");
        Assert.Contains("CN=xiaopacai-web-local", cert.Subject);
        // sha256WithRSA OID
        Assert.Equal("1.2.840.113549.1.1.11", cert.SignatureAlgorithm.Value);
    }

    [Fact]
    public void GenerateCertificate_IsRsa2048()
    {
        var service = CreateService();

        var cert = service.GetOrCreateCertificate();
        using var rsa = cert.GetRSAPrivateKey();

        Assert.NotNull(rsa);
        Assert.Equal(2048, rsa.KeySize);
    }

    [Fact]
    public void GenerateCertificate_Validity_OneYearWindow()
    {
        var service = CreateService();

        var cert = service.GetOrCreateCertificate();

        // 有效期：−1 天 ~ +1 年（容忍时钟偏差）
        Assert.True(cert.NotBefore <= DateTime.Now.AddDays(-0.5), "证书生效时间应早于当前（容忍时钟偏差）");
        Assert.True(cert.NotAfter >= DateTime.Now.AddDays(360), "证书有效期应覆盖约 1 年");
        Assert.True((cert.NotAfter - cert.NotBefore).TotalDays is >= 364 and <= 368);
    }

    [Fact]
    public void GenerateCertificate_HasServerAuthEku()
    {
        var service = CreateService();

        var cert = service.GetOrCreateCertificate();

        // serverAuth OID: 1.3.6.1.5.5.7.3.1
        var eku = cert.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();

        Assert.NotNull(eku);
        Assert.Contains(eku!.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void GetOrCreateCertificate_SameInstance_ReturnsCachedCertificate()
    {
        var service = CreateService();

        var first = service.GetOrCreateCertificate();
        var second = service.GetOrCreateCertificate();

        Assert.Same(first, second);
    }

    [Fact]
    public void GetFingerprint_Null_BeforeCertificateGenerated()
    {
        var service = CreateService();

        Assert.Null(service.GetFingerprint());
    }

    // ==================== 指纹计算 ====================

    [Fact]
    public void ComputeFingerprint_Format_Is64LowercaseHex()
    {
        var service = CreateService();
        var cert = service.GetOrCreateCertificate();

        var fingerprint = P2pCertificateService.ComputeFingerprint(cert);

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void ComputeFingerprint_SameCertificate_Stable()
    {
        var service = CreateService();
        var cert = service.GetOrCreateCertificate();

        var fp1 = P2pCertificateService.ComputeFingerprint(cert);
        var fp2 = P2pCertificateService.ComputeFingerprint(cert);

        Assert.Equal(fp1, fp2);
    }

    // ==================== 持久化 ====================

    [Fact]
    public void GenerateCertificate_PersistsPfxAndKeyFiles()
    {
        var service = CreateService();
        service.GetOrCreateCertificate();

        var keyPath = Path.ChangeExtension(_certPath, ".key"); // 与服务端实现一致（ChangeExtension）
        Assert.True(File.Exists(_certPath), "PFX 证书文件应落盘");
        Assert.True(File.Exists(keyPath), "证书密钥文件应落盘");

        // PFX 文件应为合法证书容器
        var password = File.ReadAllText(keyPath).Trim();
        using var loaded = new X509Certificate2(_certPath, password);
        Assert.NotNull(loaded);
        Assert.Contains("CN=xiaopacai-web-local", loaded.Subject);
    }

    [Fact]
    public void Fingerprint_AcrossServiceInstances_Stable()
    {
        // LEGACY-e 核心保证：证书持久化后，重新创建服务实例（模拟重启）指纹不变
        var service1 = CreateService();
        var cert1 = service1.GetOrCreateCertificate();
        var fingerprint1 = P2pCertificateService.ComputeFingerprint(cert1);

        // 模拟应用重启：新实例从磁盘加载
        var service2 = CreateService();
        var cert2 = service2.GetOrCreateCertificate();
        var fingerprint2 = P2pCertificateService.ComputeFingerprint(cert2);

        Assert.Equal(fingerprint1, fingerprint2);
        Assert.Equal(fingerprint1, service2.GetFingerprint());
    }

    [Fact]
    public void PersistedCertificate_CorruptKeyFile_Regenerates()
    {
        // 密钥文件损坏 → 加载失败 → 重新生成证书
        var service1 = CreateService();
        service1.GetOrCreateCertificate();

        // 破坏密钥文件（写入无效内容）
        var keyPath = Path.ChangeExtension(_certPath, ".key");
        if (File.Exists(keyPath)) File.SetAttributes(keyPath, FileAttributes.Normal);
        File.WriteAllText(keyPath, "corrupted-password-data");

        var service2 = CreateService();
        var cert2 = service2.GetOrCreateCertificate();

        Assert.NotNull(cert2);
        Assert.Contains("CN=xiaopacai-web-local", cert2.Subject);

        // 重新持久化后密钥文件被覆盖为有效内容
        var newPassword = File.ReadAllText(keyPath).Trim();
        Assert.False(string.IsNullOrEmpty(newPassword));
    }
}
