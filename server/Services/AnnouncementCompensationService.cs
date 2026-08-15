using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;

namespace XiaopacaiWeb.Services;

/// <summary>
/// [TASK-MILESTONE-V3] B2/B10 公告送达补偿服务（HostedService，每 30 秒扫描）
///
/// 背景：发布/撤回即时推送只覆盖在线设备，且推送可能偶发瞬时未处理（14:50 案例）。
/// 决策：服务端推送后 60 秒未收到 displayed 回执 → 对在线设备补偿重推（每设备一次）。
/// - 幂等：补偿后写 CompensatedAt 打标，不再重推；终端按 版本+内容哈希 去重，重复帧无副作用；
/// - 边界：仅补 15 分钟窗口内发布的公告（更早的由 B6 重连同步覆盖离线设备）；
/// - 账号隔离：仅推发布者账号下的在线设备（B11 同口径）；
/// - 离线设备不补（SendToDevice 仅在线会话可达），重连时走 B6 同步。
/// </summary>
public class AnnouncementCompensationService : IHostedService
{
    /// <summary>扫描周期</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>发布后等待回执的宽限期</summary>
    private static readonly TimeSpan DisplayGrace = TimeSpan.FromSeconds(60);
    /// <summary>补偿窗口：仅处理该时间内发布的公告</summary>
    private static readonly TimeSpan CompensationWindow = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly P2pMessageHandler _messageHandler;
    private readonly P2pListenerService _p2p;
    private readonly ILogger<AnnouncementCompensationService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AnnouncementCompensationService(
        IServiceScopeFactory scopeFactory,
        P2pMessageHandler messageHandler,
        P2pListenerService p2p,
        ILogger<AnnouncementCompensationService> logger)
    {
        _scopeFactory = scopeFactory;
        _messageHandler = messageHandler;
        _p2p = p2p;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 首轮延迟 30 秒，错开服务启动高峰
        try { await Task.Delay(ScanInterval, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[P2P-Compensate] 补偿扫描异常（下轮重试）");
            }

            try { await Task.Delay(ScanInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 一轮扫描：找出 60s~15min 内发布、且有设备未 displayed 且未补偿过的公告 → 补偿重推
    /// </summary>
    private async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var windowStart = now - CompensationWindow;
        var graceStart = now - DisplayGrace;

        // [TASK-MILESTONE-V3] B5 墓碑保留 7 天：到期顺带清理（客户端 7 天内重连已可清除）
        try
        {
            var tombstoneCutoff = now.AddDays(-7);
            var purged = await db.AnnouncementTombstones
                .Where(t => t.DeletedAt < tombstoneCutoff)
                .ExecuteDeleteAsync(ct);
            if (purged > 0)
                _logger.LogInformation("[P2P-Compensate] 已清理过期公告墓碑 {Count} 条", purged);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[P2P-Compensate] 墓碑清理失败（不阻断补偿扫描）");
        }

        var candidates = await db.Announcements
            .Where(a => a.Status == "published" &&
                        a.PublishedAt >= windowStart && a.PublishedAt <= graceStart)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        foreach (var announcement in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // 需补偿的设备：未显示 + 未补偿过 + 在线（会话存在）
            var pendingRows = await db.AnnouncementDeliveries
                .Where(d => d.AnnouncementId == announcement.Id &&
                            d.DisplayedAt == null && d.CompensatedAt == null)
                .ToListAsync(ct);

            var compensated = 0;
            foreach (var row in pendingRows)
            {
                var device = await db.Devices.FindAsync(new object[] { row.DeviceId }, ct);
                if (device == null || _p2p.GetSession(device.DeviceId) == null)
                    continue; // 设备不存在或离线：交由 B6 重连同步

                // [TASK-MILESTONE-V3] B11 账号隔离兜底：仅补发布者账号的设备
                // （送达行理论上是发布时写入的，但重推前再校验一次归属）
                if (announcement.TargetDeviceId == null && !BelongsToAccount(db, device, announcement.CreatedBy))
                    continue;

                var json = _messageHandler.BuildAnnouncementPushJson(announcement, "publish");
                var pushed = await _p2p.SendToDevice(device.DeviceId, json);
                if (pushed)
                {
                    row.CompensatedAt = now;
                    row.LastPushedAt = now;
                    row.UpdatedAt = now;
                    compensated++;
                }
            }

            if (compensated > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("[P2P-Compensate] 公告 {Id}（{Title}）已补偿重推 {Count} 台设备（60s 未 displayed）",
                    announcement.Id, announcement.Title, compensated);
            }
        }
    }

    /// <summary>
    /// [TASK-MILESTONE-V3] B11 归属校验（OwnerUserId 兼容用户 ID 或用户名格式）
    /// </summary>
    private static bool BelongsToAccount(AppDbContext db, Device device, int createdBy)
    {
        if (string.IsNullOrEmpty(device.OwnerUserId)) return false;
        if (int.TryParse(device.OwnerUserId, out var uid)) return uid == createdBy;
        var user = db.Users.AsNoTracking().FirstOrDefault(u => u.Username == device.OwnerUserId);
        return user != null && user.Id == createdBy;
    }
}
