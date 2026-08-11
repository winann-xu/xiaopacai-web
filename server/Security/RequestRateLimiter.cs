using System.Collections.Concurrent;

namespace XiaopacaiWeb.Security;

/// <summary>
/// 进程内简易 IP 级限速器（配对码生成/验证等敏感操作防暴力尝试）
/// </summary>
public static class RequestRateLimiter
{
    private static readonly ConcurrentDictionary<string, (int Count, long WindowStart)> _buckets = new();

    // [TASK-OPT-12-P4-DEEPEN] 失败计数桶（登录 / Ticket 确认类失败限速，5 次/小时）
    private static readonly ConcurrentDictionary<string, (int Count, long WindowStart)> _failureBuckets = new();

    /// <summary>
    /// 判断指定 key 在时间窗内是否允许继续（全量计数：成功失败都算一次）
    /// </summary>
    public static bool Allow(string key, int maxPerWindow, int windowSeconds)
    {
        // 回环/本机请求（127.0.0.1/::1/无 IP 的测试环境）不限制，
        // 防暴力尝试主要针对局域网/公网的真实远端 IP。
        if (IsLocalOrLoopback(key))
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

    // [TASK-OPT-12-P4-DEEPEN] ========== 失败计数限速 ==========

    /// <summary>
    /// 判断 key 是否已因失败次数过多被临时封锁（只读，不计数）
    /// </summary>
    public static bool IsBlocked(string key, int maxPerWindow, int windowSeconds)
    {
        if (IsLocalOrLoopback(key))
            return false;

        if (!_failureBuckets.TryGetValue(key, out var bucket))
            return false;

        // 时间窗已过，自动放行
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - bucket.WindowStart >= windowSeconds)
            return false;

        return bucket.Count >= maxPerWindow;
    }

    /// <summary>
    /// 记录一次失败并返回是否已超限（超限后调用方应拒绝请求，如返回 429）
    /// </summary>
    public static bool RecordFailure(string key, int maxPerWindow, int windowSeconds)
    {
        if (IsLocalOrLoopback(key))
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = _failureBuckets.AddOrUpdate(key, (1, now), (_, old) =>
        {
            if (now - old.WindowStart >= windowSeconds)
                return (1, now);
            return (old.Count + 1, old.WindowStart);
        });
        return bucket.Count >= maxPerWindow;
    }

    /// <summary>
    /// 清除指定 key（配对成功后释放，同时清理失败计数）
    /// </summary>
    public static void Clear(string key)
    {
        _buckets.TryRemove(key, out _);
        _failureBuckets.TryRemove(key, out _);
    }

    /// <summary>
    /// 判断 key 末尾的 IP 是否为回环/本机（回环请求不限制）
    /// </summary>
    private static bool IsLocalOrLoopback(string key)
    {
        var ipPart = key[(key.LastIndexOf(':') + 1)..];
        return ipPart is "127.0.0.1" or "::1" or "unknown" or "localhost" or "";
    }
}
