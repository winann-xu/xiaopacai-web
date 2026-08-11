namespace XiaopacaiWeb.Services;

/// <summary>
/// SQLCipher 数据库初始化服务 — 加密密钥管理 + 首次启动自动创建
/// </summary>
public interface ISqlCipherService
{
    /// <summary>初始化：确保数据库存在、加密密钥正确、Schema 最新</summary>
    Task InitializeAsync();

    /// <summary>获取当前 SQLCipher 连接字符串</summary>
    string GetConnectionString();

    /// <summary>获取明文连接字符串（不含密码）</summary>
    string GetPlainConnectionString();

    /// <summary>获取数据库加密密码</summary>
    string GetDbPassword();

    /// <summary>获取数据库文件完整路径</summary>
    string GetDatabasePath();
}
