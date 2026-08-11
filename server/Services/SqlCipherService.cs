using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XiaopacaiWeb.Services;

/// <summary>
/// SQLCipher 数据库初始化服务
/// 密钥管理：首先生成随机密钥 → 存储到 Data/.dbkey → 启动时加载密钥打开加密数据库
/// </summary>
public class SqlCipherService : ISqlCipherService
{
    private readonly IConfiguration _config;
    private ILogger<SqlCipherService> _logger;
    private readonly string _dbPath;
    private readonly string _keyPath;
    private string _dbPassword = string.Empty;
    private bool _initialized = false;

    private SqlCipherService(IConfiguration config, ILogger<SqlCipherService>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<SqlCipherService>.Instance;

        var dbConfigPath = config["Database:Path"] ?? "Data/xiaopacai.db";
        // 相对路径固定解析到内容根目录（dll 所在目录），避免依赖启动时的工作目录
        var contentRoot = AppContext.BaseDirectory;
        _dbPath = Path.GetFullPath(Path.Combine(contentRoot, dbConfigPath));
        _keyPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, ".dbkey");
    }

    /// <summary>
    /// 工厂方法：从配置创建（不依赖 DI），用于 DbContext 注册前获取连接字符串
    /// </summary>
    public static SqlCipherService CreateFromConfig(IConfiguration config)
    {
        var svc = new SqlCipherService(config);
        svc.LoadOrCreateKey();
        return svc;
    }

    /// <summary>
    /// 注入 Logger（由 DI 在构造后调用）
    /// </summary>
    public void SetLogger(ILogger<SqlCipherService> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        // 1. 确保 Data 目录存在
        var dataDir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(dataDir);

        // 2. 密钥已在 CreateFromConfig 中加载，如果未加载则在此加载
        if (string.IsNullOrEmpty(_dbPassword))
            LoadOrCreateKey();

        // 3. 验证数据库连接（执行 PRAGMA key）
        await using var connection = new SqliteConnection(GetPlainConnectionString());
        await connection.OpenAsync();

        // 设置 SQLCipher 密钥
        using var keyCmd = connection.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = '{EscapeSqlString(_dbPassword)}';";
        await keyCmd.ExecuteNonQueryAsync();

        // 验证：尝试查询
        using var verifyCmd = connection.CreateCommand();
        verifyCmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        await verifyCmd.ExecuteNonQueryAsync();

        _initialized = true;
        _logger.LogInformation("[SQLCipher] 数据库已打开: {Path}", _dbPath);
    }

    public string GetConnectionString()
    {
        if (string.IsNullOrEmpty(_dbPassword))
            LoadOrCreateKey();

        return new SqliteConnectionStringBuilder(GetPlainConnectionString())
        {
            Password = _dbPassword,
        }.ConnectionString;
    }

    public string GetPlainConnectionString()
    {
        return $"Data Source={_dbPath}";
    }

    public string GetDbPassword()
    {
        if (string.IsNullOrEmpty(_dbPassword))
            LoadOrCreateKey();
        return _dbPassword;
    }

    // ========== private ==========

    private void LoadOrCreateKey()
    {
        // 从环境变量/配置读取已有密码（生产环境推荐）
        var envPassword = _config["Database:Password"];
        if (!string.IsNullOrEmpty(envPassword))
        {
            _logger.LogInformation("[SQLCipher] 使用配置中的数据库密码");
            _dbPassword = envPassword;
            return;
        }

        // 从 key 文件读取
        if (File.Exists(_keyPath))
        {
            var key = File.ReadAllText(_keyPath).Trim();
            if (!string.IsNullOrEmpty(key))
            {
                _logger.LogInformation("[SQLCipher] 从密钥文件加载: {KeyPath}", _keyPath);
                _dbPassword = key;
                return;
            }
        }

        // 首次运行：生成随机密钥
        var newKeyBytes = RandomNumberGenerator.GetBytes(32);
        var newKey = Convert.ToBase64String(newKeyBytes);
        var dataDir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(_keyPath, newKey);

        // 设置文件权限（Linux/macOS）
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("chmod", $"600 \"{_keyPath}\"");
            }
        }
        catch { /* best effort */ }

        _logger.LogWarning("[SQLCipher] 已生成新密钥并保存到 {KeyPath} — 请妥善保管！", _keyPath);
        _dbPassword = newKey;
    }

    private static string EscapeSqlString(string s) =>
        s.Replace("'", "''");
}
