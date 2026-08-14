using System.Collections.Concurrent;

namespace XiaopacaiWeb.Services;

/// <summary>
/// 一次性 Ticket 内存存储（扫码登录 / 忘记密码重置，OPT12 需求 10/12）
///
/// 单进程自托管场景下使用内存字典即可满足 P1 阶段需求；
/// TODO(P4)：多实例部署 / 进程重启恢复场景下需迁移到数据库表。
/// </summary>
public class TicketStore
{
    /// <summary>扫码登录 Ticket 有效期（90 秒）</summary>
    public const int LoginTicketLifetimeSeconds = 90;

    /// <summary>重置密码 Ticket 有效期（10 分钟）</summary>
    public const int ResetTicketLifetimeSeconds = 600;

    /// <summary>状态常量</summary>
    public const string StatusPending = "pending";
    public const string StatusConfirmed = "confirmed";
    public const string StatusExpired = "expired";

    /// <summary>[SEC-P1] 内存 Ticket 总量上限：防匿名批量生成撑爆内存（超限时拒绝创建）</summary>
    public const int MaxPendingTickets = 2000;

    // ticket → 条目
    private readonly ConcurrentDictionary<string, TicketEntry> _tickets = new();

    /// <summary>
    /// 生成扫码登录 Ticket（状态 pending）。
    /// [SEC-P1] 未过期/未消费的 Ticket 超上限时返回 null（调用方应答 429）
    /// </summary>
    public TicketEntry? CreateLoginTicket(string? clientId)
    {
        if (CountLive() >= MaxPendingTickets)
            return null;

        var entry = new TicketEntry
        {
            Ticket = Guid.NewGuid().ToString("N"),
            Kind = "login",
            ClientId = clientId,
            Status = StatusPending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(LoginTicketLifetimeSeconds),
        };
        _tickets[entry.Ticket] = entry;
        return entry;
    }

    /// <summary>
    /// 生成重置密码 Ticket（状态 pending，绑定目标账号）。
    /// [SEC-P1] 未过期/未消费的 Ticket 超上限时返回 null（调用方应答 429）
    /// </summary>
    public TicketEntry? CreateResetTicket(string username)
    {
        if (CountLive() >= MaxPendingTickets)
            return null;

        var entry = new TicketEntry
        {
            Ticket = Guid.NewGuid().ToString("N"),
            Kind = "reset",
            Username = username,
            Status = StatusPending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(ResetTicketLifetimeSeconds),
        };
        _tickets[entry.Ticket] = entry;
        return entry;
    }

    /// <summary>
    /// [SEC-P1] 存活 Ticket 计数（未过期且未消费）
    /// </summary>
    private int CountLive()
    {
        var now = DateTime.UtcNow;
        return _tickets.Count(kv =>
            !kv.Value.Consumed && kv.Value.ExpiresAt >= now);
    }

    /// <summary>
    /// 获取 Ticket 并计算当前状态（过期自动标记 expired，一次性消费）
    /// </summary>
    public TicketEntry? Get(string ticket)
    {
        if (!_tickets.TryGetValue(ticket, out var entry))
            return null;

        if (entry.Status == StatusPending && entry.ExpiresAt < DateTime.UtcNow)
        {
            entry.Status = StatusExpired;
            entry.Consumed = true;
        }

        return entry;
    }

    /// <summary>
    /// 确认 Ticket（登录确认绑定用户；重置确认绑定确认者并核对目标账号）
    /// </summary>
    public bool Confirm(string ticket, int userId, string username)
    {
        var entry = Get(ticket);
        if (entry == null || entry.Status != StatusPending)
            return false;

        if (entry.Kind == "reset" && !string.Equals(entry.Username, username, StringComparison.OrdinalIgnoreCase))
            return false; // 重置确认者必须与目标账号一致

        entry.Status = StatusConfirmed;
        entry.ConfirmedByUserId = userId;
        entry.ConfirmedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// 标记 Ticket 已消费（一次性使用，确认后 / 重置完成后调用）
    /// </summary>
    public void Consume(string ticket)
    {
        if (_tickets.TryGetValue(ticket, out var entry))
            entry.Consumed = true;
    }

    /// <summary>
    /// 清理过期 Ticket（内存占用控制，可在定时任务中调用）
    /// </summary>
    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _tickets)
        {
            if (kv.Value.Status == StatusExpired || kv.Value.ExpiresAt < now || kv.Value.Consumed)
                _tickets.TryRemove(kv.Key, out _);
        }
    }
}

/// <summary>
/// Ticket 条目
/// </summary>
public class TicketEntry
{
    public string Ticket { get; set; } = string.Empty;

    /// <summary>类型：login | reset</summary>
    public string Kind { get; set; } = "login";

    /// <summary>创建方客户端标识（扫码登录用，可选）</summary>
    public string? ClientId { get; set; }

    /// <summary>目标账号（重置密码用）</summary>
    public string? Username { get; set; }

    /// <summary>状态：pending | confirmed | expired</summary>
    public string Status { get; set; } = "pending";

    /// <summary>是否已消费（一次性）</summary>
    public bool Consumed { get; set; }

    /// <summary>确认者用户 ID（可选）</summary>
    public int? ConfirmedByUserId { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
