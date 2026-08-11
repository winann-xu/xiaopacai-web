using XiaopacaiWeb.DTOs;

namespace XiaopacaiWeb.Services;

/// <summary>
/// JWT Token 签发与验证服务
/// </summary>
public interface IJwtService
{
    /// <summary>生成 Access + Refresh Token 对</summary>
    (string accessToken, string refreshToken, DateTime accessExpiry, DateTime refreshExpiry) GenerateTokens(int userId, string username, string role);

    /// <summary>验证 Refresh Token 并返回新 Token 对</summary>
    Task<AuthResponse?> RefreshTokens(string refreshToken);

    /// <summary>吊销用户所有 Refresh Token（logout/改密时调用）</summary>
    Task RevokeAllUserTokens(int userId);

    /// <summary>吊销单个 Refresh Token</summary>
    Task RevokeToken(string refreshToken);

    /// <summary>存储 Refresh Token 到数据库</summary>
    Task StoreRefreshToken(int userId, string refreshToken, DateTime expiresAt);

    // [TASK-OPT-12-P4-DEEPEN] 设备级鉴权

    /// <summary>生成设备级 JWT（限定 scope：diagnostics + usage_report，24 小时有效）</summary>
    (string token, DateTime expiresAt) GenerateDeviceToken(string deviceId);

    /// <summary>校验设备级 JWT：签名有效 + role=device + scope 包含指定权限 + sub 匹配设备 ID</summary>
    bool TryValidateDeviceToken(string token, string expectedDeviceId, string requiredScope);
}
