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
}
