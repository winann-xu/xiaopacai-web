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
    /// 处理握手请求 — 设备注册/认证 + 返回当前策略
    ///
    /// 家长端中继连接（deviceId 以 "parent-" 开头且 relay=true）：跳过 devices 表操作，
    /// 仅注册 relay_sessions（role=parent），用于接收中继转发的子设备消息。
    /// </summary>
    public async Task<(HandshakeResponse response, string? policyPushJson, int? dbDeviceId)>
        HandleHandshake(HandshakeRequest req, string? peerFingerprint, string remoteEndPoint)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // [TASK-OPT-12-P4-DEEPEN] 家长端中继连接：不创建 Device，仅维护 relay_sessions
        if (req.Relay && req.DeviceId.StartsWith("parent-"))
        {
            _logger.LogInformation("[P2P-Handshake] 家长端中继连接: {DeviceId} @ {Ip}",
                req.DeviceId, remoteEndPoint);

            db.RelaySessions.Add(new RelaySession
            {
                DeviceId = req.DeviceId,
                Role = "parent",
                IpAddress = remoteEndPoint,
                Status = "connected",
                ConnectedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            return (new HandshakeResponse
            {
                Ok = true,
                PairStatus = "paired",
                SessionId = Guid.NewGuid().ToString("N")[..12],
            }, null, null);  // 家长端不需要策略下发
        }

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

            // [TASK-OPT-12-P4-DEEPEN] 中继连接：握手成功后写入 relay_sessions 会话记录
            if (req.Relay)
            {
                db.RelaySessions.Add(new RelaySession
                {
                    DeviceId = req.DeviceId,
                    Role = "child",
                    IpAddress = remoteEndPoint,
                    Status = "connected",
                    ConnectedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();

            _logger.LogInformation("[P2P-Handshake] 新设备已配对: {DeviceId} ({DeviceName})", req.DeviceId, req.DeviceName);

            // 重新加载带策略的设备
            device = await db.Devices.Include(d => d.Policy).FirstAsync(d => d.Id == device.Id);

            return (new HandshakeResponse
            {
                Ok = true,
                PairStatus = "paired",
                SessionId = Guid.NewGuid().ToString("N")[..12],
            }, BuildPolicyPushMessage(device.DeviceId, device.Policy, device.AppCategories), device.Id);
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

        // [TASK-OPT-12-P4-DEEPEN] 中继连接：写入 relay_sessions 会话记录
        if (req.Relay)
        {
            db.RelaySessions.Add(new RelaySession
            {
                DeviceId = req.DeviceId,
                Role = "child",
                IpAddress = remoteEndPoint,
                Status = "connected",
                ConnectedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        // 构建策略下发
        var policyMsg = BuildPolicyPushMessage(device.DeviceId, device.Policy, device.AppCategories);

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
    /// 设备断开连接时更新状态（含中继会话状态）
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

        // [TASK-OPT-12-P4-DEEPEN] 更新该设备最近一条在线中继会话为断开
        var relaySession = await db.RelaySessions
            .Where(r => r.DeviceId == deviceId && r.Status == "connected")
            .OrderByDescending(r => r.ConnectedAt)
            .FirstOrDefaultAsync();
        if (relaySession != null)
        {
            relaySession.Status = "disconnected";
            relaySession.DisconnectedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    // [TASK-OPT-12-P4-DEEPEN] ========== 消息中继转发 ==========

    /// <summary>
    /// 将儿童端消息中继转发给绑定家长端（家长端 APP 通过云端中继连接）
    ///
    /// 查找链路：儿童端 devices.owner_user_id → 家长账号 → relay_sessions 中该账号下在线家长端会话
    /// （ParentDeviceId 为家长端 APP 自己的设备 ID，家长端握手时以该 ID 注册会话）
    /// </summary>
    public async Task RelayMessageToParent(string childDeviceId, string messageJson, P2pListenerService? p2pService)
    {
        if (p2pService == null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. 找到儿童设备绑定的家长账号（owner_user_id 兼容存用户 ID 或用户名的两种格式）
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == childDeviceId);
        if (device == null || string.IsNullOrEmpty(device.OwnerUserId))
            return;

        int? ownerUserId = int.TryParse(device.OwnerUserId, out var uid)
            ? uid
            : (await db.Users.FirstOrDefaultAsync(u => u.Username == device.OwnerUserId))?.Id;
        if (ownerUserId == null)
            return;

        // 2. 找该家长账号下最近在线的家长端中继会话
        var parentSession = await db.RelaySessions
            .Where(r => r.Role == "parent" && r.UserId == ownerUserId.Value && r.Status == "connected")
            .OrderByDescending(r => r.ConnectedAt)
            .FirstOrDefaultAsync();
        if (parentSession == null)
            return;

        // 3. 转发给家长端（家长端在线则实时收到；离线静默丢弃）
        await p2pService.SendToDevice(parentSession.DeviceId, messageJson);
        _logger.LogDebug("[P2P-Relay] 已中继转发 {Bytes} 字节到家长端 {ParentDevice}",
            messageJson.Length, parentSession.DeviceId);
    }

    // ========== 2.0 协议格式构建（兼容 Android 儿童端） ==========

    /// <summary>
    /// 构建 2.0 policy_update 完整消息 JSON（payload.policies 为 PolicyConfig JSON 字符串数组）
    /// appCategoriesJson 可选：devices.app_categories JSON 列，随策略一并下发（payload.app_categories）
    /// </summary>
    public string BuildPolicyPushMessage(string deviceId, Policy? policy, string? appCategoriesJson = null)
    {
        var policies = BuildPolicyConfigItems(deviceId, policy);
        var payload = new Dictionary<string, object>
        {
            ["deviceId"] = deviceId,
            ["policies"] = policies,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        // [TASK-OPT-12-P4-DEEPEN] 携带设备应用分类（下发格式：packageName/appName/category）
        var appCategories = ParseAppCategories(appCategoriesJson);
        if (appCategories is { Count: > 0 })
            payload["app_categories"] = appCategories;

        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.PolicyUpdate,
            ["payload"] = payload,
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// 将 Web 端 Policy 模型转换为 2.0 PolicyConfig JSON 字符串数组（5 类策略）
    /// </summary>
    public List<string> BuildPolicyConfigItems(string deviceId, Policy? policy)
    {
        var version = Interlocked.Increment(ref _policyVersionCounter);
        var items = new List<string>();
        if (policy == null)
        {
        items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["policyType"] = "daily_limit",
            ["deviceId"] = deviceId,
            ["isActive"] = true,
            ["version"] = version,
            ["limitMinutes"] = 120,
            ["restrictMode"] = "full",
            ["label"] = "每日限额",
        }));
            return items;
        }

        // 每日限额
        items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["policyType"] = "daily_limit",
            ["deviceId"] = deviceId,
            ["isActive"] = true,
            ["version"] = version,
            ["limitMinutes"] = policy.DailyLimitMinutes,
            ["restrictMode"] = policy.OvertimeAction switch
            {
                "partial_lock" => "partial",
                "warn_only" => "warn",
                _ => "full",
            },
            ["label"] = "每日限额",
        }));

        // 就寝时段
        if (!string.IsNullOrEmpty(policy.BedtimeStart) && !string.IsNullOrEmpty(policy.BedtimeEnd))
        {
            items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["policyType"] = "sleep_time",
                ["deviceId"] = deviceId,
                ["isActive"] = true,
                ["version"] = version,
                ["sleepStart"] = policy.BedtimeStart,
                ["sleepEnd"] = policy.BedtimeEnd,
                ["label"] = "就寝时段",
            }));
        }

        // 分类限额（仅启用即 >=0 的项）
        AddCategoryLimit(items, deviceId, version, "game", policy.CategoryGameLimit);
        AddCategoryLimit(items, deviceId, version, "social", policy.CategorySocialLimit);
        AddCategoryLimit(items, deviceId, version, "video", policy.CategoryVideoLimit);
        AddCategoryLimit(items, deviceId, version, "learning", policy.CategoryLearningLimit);

        // 白名单
        var whitelist = DeserializeStringList(policy.WhitelistApps);
        if (whitelist is { Count: > 0 })
        {
            items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["policyType"] = "whitelist",
                ["deviceId"] = deviceId,
                ["isActive"] = true,
                ["version"] = version,
                ["packageNames"] = whitelist,
                ["label"] = "白名单",
            }));
        }

        // 黑名单
        var blacklist = DeserializeStringList(policy.BlacklistApps);
        if (blacklist is { Count: > 0 })
        {
            items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["policyType"] = "blacklist",
                ["deviceId"] = deviceId,
                ["isActive"] = true,
                ["version"] = version,
                ["packageNames"] = blacklist,
                ["label"] = "黑名单",
            }));
        }

        return items;
    }

    private static void AddCategoryLimit(List<string> items, string deviceId, long version,
        string category, int minutes)
    {
        if (minutes < 0) return;
        items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["policyType"] = "category_limit",
            ["deviceId"] = deviceId,
            ["isActive"] = true,
            ["version"] = version,
            ["category"] = category,
            ["categoryLimitMinutes"] = minutes,
            ["label"] = category switch
            {
                "game" => "游戏限额",
                "social" => "社交限额",
                "video" => "视频限额",
                "learning" => "学习限额",
                _ => "分类限额",
            },
        }));
    }

    /// <summary>
    /// 构建 2.0 announcement_push 完整消息 JSON（payload.announcements 数组）
    /// </summary>
    public string BuildAnnouncementPushJson(Announcement announcement, string action)
    {
        var announcements = new List<Dictionary<string, object>>
        {
            new()
            {
                ["id"] = announcement.Id,
                ["title"] = announcement.Title,
                ["content"] = announcement.Content,
                ["priority"] = announcement.Priority switch
                {
                    "urgent" => 2,
                    "important" => 1,
                    _ => 0,
                },
                ["created_at"] = new DateTimeOffset(announcement.CreatedAt).ToUnixTimeSeconds(),
                ["expires_at"] = announcement.ValidUntil.HasValue
                    ? new DateTimeOffset(announcement.ValidUntil.Value).ToUnixTimeSeconds()
                    : 0L,
            },
        };

        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.AnnouncementPush,
            ["payload"] = new Dictionary<string, object>
            {
                ["announcements"] = announcements,
                ["action"] = action,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// 构建 2.0 sync_ack 完整消息 JSON
    /// </summary>
    public string BuildSyncAckJson(int syncedCount)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.SyncAck,
            ["payload"] = new Dictionary<string, object>
            {
                ["syncedCount"] = syncedCount,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// 构建 2.0 heartbeat_ack 完整消息 JSON
    /// </summary>
    public string BuildHeartbeatAckJson()
        => $"{{\"type\":\"{P2pMessageType.HeartbeatAck}\",\"payload\":{{}}}}";

    /// <summary>
    /// 处理 2.0 儿童端 usage_report（records 为 JSON 字符串，元素含 packageName/appName/date/totalMinutes/category）
    /// </summary>
    public async Task<SyncAckMessage> HandleUsageReportLegacy(string deviceId, string recordsJson)
    {
        var request = new UsageReportRequest
        {
            DeviceId = deviceId,
            Records = ParseLegacyRecords(recordsJson),
        };
        return await HandleUsageReport(request);
    }

    private static List<UsageRecordItem> ParseLegacyRecords(string recordsJson)
    {
        var result = new List<UsageRecordItem>();
        if (string.IsNullOrWhiteSpace(recordsJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(recordsJson);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var item = new UsageRecordItem
                {
                    AppPackage = GetString(element, "packageName") ?? string.Empty,
                    AppName = GetString(element, "appName") ?? string.Empty,
                    Category = GetString(element, "category") ?? "other",
                    IsBlocked = GetBool(element, "isBlocked"),
                };

                // date: YYYY-MM-DD；totalMinutes: 分钟
                var date = GetString(element, "date");
                if (!string.IsNullOrEmpty(date))
                    item.StartTime = date.Length >= 10 ? date[..10] + "T00:00:00Z" : date;

                var minutes = GetLong(element, "totalMinutes");
                item.DurationSeconds = (int)Math.Min(int.MaxValue, minutes * 60);
                result.Add(item);
            }
        }
        catch (JsonException)
        {
            // 忽略非法记录
        }
        return result;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (element.TryGetProperty(name, out var num) && num.ValueKind == JsonValueKind.Number)
            return num.GetRawText();
        return null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(value.GetString(), out var l) => l,
            _ => 0,
        };
    }

    // ========== 公告推送 ==========

    /// <summary>
    /// 公告发布/撤回后主动推送到儿童端
    /// 由 REST API 在 announcement 状态变更时调用
    /// </summary>
    public async Task PushAnnouncement(Announcement announcement, string action, P2pListenerService? p2pService)
    {
        if (p2pService == null) return;

        var json = BuildAnnouncementPushJson(announcement, action);

        if (announcement.TargetDeviceId != null)
        {
            // 定向设备
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.FindAsync(announcement.TargetDeviceId.Value);
            if (device != null)
            {
                await p2pService.SendToDevice(device.DeviceId, json);
                _logger.LogInformation("[P2P-Announce] 公告已推送到设备 {DeviceId}: {Title}", device.DeviceId, announcement.Title);
            }
        }
        else
        {
            // 广播到所有在线设备
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var devices = await db.Devices
                .Where(d => d.PairStatus == "paired" && d.IsActive)
                .Select(d => d.DeviceId)
                .ToListAsync();

            foreach (var deviceId in devices)
            {
                await p2pService.SendToDevice(deviceId, json);
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

    // [TASK-OPT-12-P4-DEEPEN] ========== 应用分类解析 ==========

    /// <summary>
    /// 解析设备 app_categories JSON（兼容 PascalCase / camelCase 字段名，损坏数据返回 null）
    /// 输出格式：{"packageName": "...", "appName": "...", "category": "game"} 数组
    /// </summary>
    private static List<Dictionary<string, object>>? ParseAppCategories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<Dictionary<string, object>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["packageName"] = GetString(item, "packageName") ?? GetString(item, "PackageName") ?? string.Empty,
                    ["appName"] = GetString(item, "appName") ?? GetString(item, "AppName") ?? string.Empty,
                    ["category"] = GetString(item, "category") ?? GetString(item, "Category") ?? "other",
                });
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
