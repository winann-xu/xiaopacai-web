using System.Collections.Concurrent;

namespace XiaopacaiWeb.Security;

/// <summary>
/// 进程内简易 IP 级限速器（配对码生成/验证等敏感操作防暴力尝试）
/// </summary>
public static class RequestRateLimiter
{
    private static readonly ConcurrentDictionary<string, (int Count, long WindowStart)> _buckets = new();

    /// <summary>
    /// 判断指定 key 在时间窗内是否允许继续
    /// </summary>
    public static bool Allow(string key, int maxPerWindow, int windowSeconds)
    {
        // 回环/本机请求（127.0.0.1/::1/无 IP 的测试环境）不限制，
        // 防暴力尝试主要针对局域网/公网的真实远端 IP。
        var ipPart = key[(key.LastIndexOf(':') + 1)..];
        if (ipPart is "127.0.0.1" or "::1" or "unknown" or "localhost" or "")
            return true;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = _buckets.AddOrUpdate(key, (1, now), (_, old) =>
        {
            if (now - old.WindowStart >= windowSeconds)
                return (1, now);
            return (old.Count + 1, old.WindowStart);
        });
        return bucket.Count <= maxPerWindow;
    }

    /// <summary>
    /// 清除指定 key（配对成功后释放）
    /// </summary>
    public static void Clear(string key) => _buckets.TryRemove(key, out _);
}
