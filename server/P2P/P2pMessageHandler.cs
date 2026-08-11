using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.P2P;

/// <summary>
/// P2P 消息处理器 — handshake / usage_report / announcement_push / heartbeat
///
/// 负责消息的业务逻辑处理：设备注册、使用记录写入、每日汇总更新、策略构建等。
/// </summary>
public class P2pMessageHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<P2pMessageHandler> _logger;

    // 策略版本计数器（递增触发儿童端重新拉取策略）
    private static long _policyVersionCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public P2pMessageHandler(IServiceScopeFactory scopeFactory, ILogger<P2pMessageHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ========== Handshake ==========

    /// <summary>
    /// 处理儿童端握手请求 — 设备注册/认证 + 返回当前策略
    /// </summary>
    public async Task<(HandshakeResponse response, PolicyUpdateMessage? policy, int? dbDeviceId)>
        HandleHandshake(HandshakeRequest req, string? peerFingerprint, string remoteEndPoint)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. 查找设备（按 device_id）
        var device = await db.Devices
            .Include(d => d.Policy)
            .FirstOrDefaultAsync(d => d.DeviceId == req.DeviceId);

        if (device == null)
        {
            // 新设备 — 需要配对码
            if (string.IsNullOrEmpty(req.PairCode))
            {
                _logger.LogWarning("[P2P-Handshake] 新设备缺少配对码: {DeviceId}", req.DeviceId);
                return (new HandshakeResponse
                {
                    Ok = false,
                    Error = "需要配对码",
                    PairStatus = "unpaired",
                }, null, null);
            }

            // 验证配对码
            var pairingInfo = await db.PairingInfos
                .Where(p => p.PairCode == req.PairCode && p.PairStatus == "pending")
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (pairingInfo == null || pairingInfo.ExpiresAt < DateTime.UtcNow)
            {
                _logger.LogWarning("[P2P-Handshake] 配对码无效或已过期: {PairCode}", req.PairCode);
                return (new HandshakeResponse
                {
                    Ok = false,
                    Error = "配对码无效或已过期",
                    PairStatus = "unpaired",
                }, null, null);
            }

            // 创建新设备
            device = new Device
            {
                DeviceId = req.DeviceId,
                DeviceName = req.DeviceName ?? req.DeviceId,
                Platform = req.Platform ?? "android",
                IpAddress = remoteEndPoint,
                CertFingerprint = peerFingerprint ?? req.CertFingerprint,
                PairCode = req.PairCode,
                PairStatus = "paired",
                OnlineStatus = "online",
                LastSeenAt = DateTime.UtcNow,
            };

            db.Devices.Add(device);
            await db.SaveChangesAsync();

            // 更新配对信息状态
            pairingInfo.TlsFingerprint = peerFingerprint ?? req.CertFingerprint;
            pairingInfo.PairStatus = "confirmed";
            pairingInfo.ConfirmedAt = DateTime.UtcNow;
            pairingInfo.DeviceId = device.Id;

            // 创建默认策略
            var policy = new Policy
            {
                DeviceId = device.Id,
                DailyLimitMinutes = 120,
                OvertimeAction = "full_lock",
            };
            db.Policies.Add(policy);

            await db.SaveChangesAsync();

            _logger.LogInformation("[P2P-Handshake] 新设备已配对: {DeviceId} ({DeviceName})", req.DeviceId, req.DeviceName);

            // 重新加载带策略的设备
            device = await db.Devices.Include(d => d.Policy).FirstAsync(d => d.Id == device.Id);

            var newPolicy = BuildPolicyUpdateMessage(device.Policy);
            return (new HandshakeResponse
            {
                Ok = true,
                PairStatus = "paired",
                SessionId = Guid.NewGuid().ToString("N")[..12],
            }, newPolicy, device.Id);
        }

        // 2. 已有设备 — 检查配对状态
        if (device.PairStatus == "revoked")
        {
            _logger.LogWarning("[P2P-Handshake] 设备已被吊销: {DeviceId}", req.DeviceId);
            return (new HandshakeResponse
            {
                Ok = false,
                Error = "设备已被吊销",
                PairStatus = "revoked",
            }, null, device.Id);
        }

        // 3. 已配对设备 — 更新状态
        device.OnlineStatus = "online";
        device.LastSeenAt = DateTime.UtcNow;
        device.IpAddress = remoteEndPoint;
        device.DeviceName = req.DeviceName ?? device.DeviceName;

        if (device.PairStatus == "unpaired" && !string.IsNullOrEmpty(req.PairCode))
        {
            // 重新配对
            device.PairStatus = "paired";
            device.PairCode = req.PairCode;
        }

        // 更新证书指纹
        if (!string.IsNullOrEmpty(peerFingerprint))
            device.CertFingerprint = peerFingerprint;
        else if (!string.IsNullOrEmpty(req.CertFingerprint) && string.IsNullOrEmpty(device.CertFingerprint))
            device.CertFingerprint = req.CertFingerprint;

        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 构建策略下发
        var policyMsg = BuildPolicyUpdateMessage(device.Policy);

        _logger.LogInformation("[P2P-Handshake] 设备已连接: {DeviceId} ({DeviceName}), status={PairStatus}",
            req.DeviceId, req.DeviceName, device.PairStatus);

        return (new HandshakeResponse
        {
            Ok = true,
            PairStatus = device.PairStatus,
            SessionId = Guid.NewGuid().ToString("N")[..12],
        }, policyMsg, device.Id);
    }

    // ========== Usage Report ==========

    /// <summary>
    /// 处理儿童端使用上报 — 写入 usage_records + 更新 daily_summary
    /// </summary>
    public async Task<SyncAckMessage> HandleUsageReport(UsageReportRequest req)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == req.DeviceId);
        if (device == null)
        {
            _logger.LogWarning("[P2P-Usage] 设备未找到: {DeviceId}", req.DeviceId);
            return new SyncAckMessage { BatchId = req.BatchId, Synced = 0 };
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var synced = 0;

        foreach (var record in req.Records)
        {
            // 解析时间
            if (!DateTime.TryParse(record.StartTime, out var startTime))
                continue;

            DateTime? endTime = null;
            if (!string.IsNullOrEmpty(record.EndTime) && DateTime.TryParse(record.EndTime, out var et))
                endTime = et;

            // 写入使用记录
            var usageRecord = new UsageRecord
            {
                DeviceId = device.Id,
                AppPackage = record.AppPackage,
                AppName = record.AppName,
                Category = NormalizeCategory(record.Category),
                StartTime = startTime,
                EndTime = endTime,
                DurationSeconds = record.DurationSeconds,
                IsBlocked = record.IsBlocked,
                CreatedAt = DateTime.UtcNow,
            };
            db.UsageRecords.Add(usageRecord);
            synced++;
        }

        await db.SaveChangesAsync();

        // 更新每日汇总
        await UpdateDailySummary(db, device.Id, today);

        // 计算今日使用情况
        var summary = await db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == device.Id && s.SummaryDate == today);

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.DeviceId == device.Id);
        var dailyLimit = policy?.DailyLimitMinutes ?? 120;
        var todayMinutes = summary?.TotalMinutes ?? 0;
        var remaining = Math.Max(0, dailyLimit - todayMinutes);
        var overtimeLocked = summary != null && summary.TotalMinutes >= dailyLimit;

        _logger.LogDebug("[P2P-Usage] 设备 {DeviceId} 上报 {Count} 条记录, 今日累计 {Min}min",
            req.DeviceId, synced, todayMinutes);

        return new SyncAckMessage
        {
            BatchId = req.BatchId,
            Synced = synced,
            TodayTotalMinutes = todayMinutes,
            TodayRemainingMinutes = remaining,
            OvertimeLocked = overtimeLocked,
        };
    }

    // ========== Heartbeat ==========

    /// <summary>
    /// 处理心跳 — 更新设备在线状态 + 检查是否有待下发内容
    /// </summary>
    public async Task<HeartbeatAckMessage> HandleHeartbeat(HeartbeatMessage req)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == req.DeviceId);
        if (device != null)
        {
            device.OnlineStatus = "online";
            device.LastSeenAt = DateTime.UtcNow;
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // 检查是否有待下发的公告（过去 1 小时内发布/撤回的，且目标设备匹配）
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var hasPendingAnnouncement = device != null && await db.Announcements
            .AnyAsync(a =>
                (a.Status == "published" || a.Status == "revoked") &&
                a.UpdatedAt >= oneHourAgo &&
                (a.TargetDeviceId == null || a.TargetDeviceId == device.Id));

        // 检查策略是否有更新（通过版本号判断，这里简化为总是 false，除非主动触发）
        var hasPolicyPending = false;

        return new HeartbeatAckMessage
        {
            ServerTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PolicyPending = hasPolicyPending,
            AnnouncementPending = hasPendingAnnouncement,
        };
    }

    // ========== 设备断线 ==========

    /// <summary>
    /// 设备断开连接时更新状态
    /// </summary>
    public async Task OnDeviceDisconnected(string deviceId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device != null)
        {
            device.OnlineStatus = "offline";
            device.LastSeenAt = DateTime.UtcNow;
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    // ========== 公告推送 ==========

    /// <summary>
    /// 公告发布/撤回后主动推送到儿童端
    /// 由 REST API 在 announcement 状态变更时调用
    /// </summary>
    public async Task PushAnnouncement(Announcement announcement, string action, P2pListenerService? p2pService)
    {
        if (p2pService == null) return;

        var msg = new AnnouncementPushMessage
        {
            Id = announcement.Id,
            Title = announcement.Title,
            Content = announcement.Content,
            Priority = announcement.Priority,
            Action = action,
            ValidFrom = announcement.ValidFrom?.ToString("o"),
            ValidUntil = announcement.ValidUntil?.ToString("o"),
            PublishedAt = announcement.PublishedAt?.ToString("o"),
        };

        if (announcement.TargetDeviceId != null)
        {
            // 定向设备
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.FindAsync(announcement.TargetDeviceId.Value);
            if (device != null)
            {
                await p2pService.SendToDevice(device.DeviceId, P2pMessageType.AnnouncementPush, msg);
                _logger.LogInformation("[P2P-Announce] 公告已推送到设备 {DeviceId}: {Title}", device.DeviceId, announcement.Title);
            }
        }
        else
        {
            // 广播到所有在线设备
            var sessions = p2pService.ActiveSessionCount; // 无法直接遍历，用另一个方式
            // 改为：遍历所有配对设备并尝试发送
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var devices = await db.Devices
                .Where(d => d.PairStatus == "paired" && d.IsActive)
                .Select(d => d.DeviceId)
                .ToListAsync();

            foreach (var deviceId in devices)
            {
                await p2pService.SendToDevice(deviceId, P2pMessageType.AnnouncementPush, msg);
            }
            _logger.LogInformation("[P2P-Announce] 公告已广播到 {Count} 个设备: {Title}", devices.Count, announcement.Title);
        }
    }

    // ========== 策略构建 ==========

    /// <summary>
    /// 从数据库 Policy 构建 P2P policy_update 消息
    /// </summary>
    public PolicyUpdateMessage BuildPolicyUpdateMessage(Policy? policy)
    {
        var version = Interlocked.Increment(ref _policyVersionCounter);

        if (policy == null)
        {
            return new PolicyUpdateMessage
            {
                DailyLimit = 120,
                OvertimeAction = "full_lock",
                PolicyVersion = version,
            };
        }

        return new PolicyUpdateMessage
        {
            DailyLimit = policy.DailyLimitMinutes,
            SleepTimeStart = policy.BedtimeStart,
            SleepTimeEnd = policy.BedtimeEnd,
            CategoryLimit = new CategoryLimit
            {
                Game = policy.CategoryGameLimit,
                Social = policy.CategorySocialLimit,
                Video = policy.CategoryVideoLimit,
                Learning = policy.CategoryLearningLimit,
            },
            Whitelist = DeserializeStringList(policy.WhitelistApps),
            Blacklist = DeserializeStringList(policy.BlacklistApps),
            OvertimeAction = policy.OvertimeAction,
            PolicyVersion = version,
        };
    }

    // ========== 内部辅助 ==========

    /// <summary>
    /// 更新每日汇总（按 device + date upsert）
    /// </summary>
    private static async Task UpdateDailySummary(AppDbContext db, int deviceId, string summaryDate)
    {
        var records = await db.UsageRecords
            .Where(r => r.DeviceId == deviceId && r.StartTime.ToString("yyyy-MM-dd") == summaryDate)
            .ToListAsync();

        if (records.Count == 0) return;

        var totalSeconds = records.Sum(r => r.DurationSeconds);
        var gameSeconds = records.Where(r => r.Category == "game").Sum(r => r.DurationSeconds);
        var socialSeconds = records.Where(r => r.Category == "social").Sum(r => r.DurationSeconds);
        var videoSeconds = records.Where(r => r.Category == "video").Sum(r => r.DurationSeconds);
        var learningSeconds = records.Where(r => r.Category == "learning").Sum(r => r.DurationSeconds);
        var otherSeconds = records.Where(r => r.Category == "other").Sum(r => r.DurationSeconds);
        var blockCount = records.Count(r => r.IsBlocked);

        var summary = await db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.SummaryDate == summaryDate);

        if (summary == null)
        {
            summary = new DailySummary
            {
                DeviceId = deviceId,
                SummaryDate = summaryDate,
            };
            db.DailySummaries.Add(summary);
        }

        summary.TotalMinutes = totalSeconds / 60;
        summary.GameMinutes = gameSeconds / 60;
        summary.SocialMinutes = socialSeconds / 60;
        summary.VideoMinutes = videoSeconds / 60;
        summary.LearningMinutes = learningSeconds / 60;
        summary.OtherMinutes = otherSeconds / 60;
        summary.OvertimeCount = 0;
        summary.BlockCount = blockCount;
        summary.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 规范化分类名称
    /// </summary>
    private static string NormalizeCategory(string category)
    {
        return category?.ToLowerInvariant() switch
        {
            "game" => "game",
            "social" => "social",
            "video" => "video",
            "learning" => "learning",
            _ => "other",
        };
    }

    /// <summary>
    /// 反序列化 JSON 字符串列表
    /// </summary>
    private static List<string>? DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }
}
