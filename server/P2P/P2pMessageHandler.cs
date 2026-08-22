using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
using XiaopacaiWeb.Services;

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
    public async Task<(HandshakeResponse response, string? policyPushJson, string? resetPushJson, int? dbDeviceId)>
        HandleHandshake(HandshakeRequest req, string? peerFingerprint, string remoteEndPoint)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // [SEC-K3] 握手入口 IP 级失败限速（10 次失败/5 分钟），防 6 位配对码 TCP 爆破（红线 R4.2）
        var handshakeIp = ExtractIp(remoteEndPoint);
        if (RequestRateLimiter.IsBlocked($"p2p-handshake:ip:{handshakeIp}", 10, 300))
        {
            _logger.LogWarning("[P2P-Handshake][SEC-K3] IP 握手失败过多，临时拒绝: {Ip}", handshakeIp);
            // [SEC] 被限速拦截的尝试同样审计（红线 R9.1：越权/暴力尝试必须留痕）
            db.AuditLogs.Add(new AuditLog
            {
                Action = "p2p.handshake_blocked",
                TargetType = "Device",
                Detail = JsonSerializer.Serialize(new { deviceId = req.DeviceId, reason = "ip_rate_limited" }),
                IpAddress = handshakeIp,
            });
            await db.SaveChangesAsync();
            // [TASK-PRELAUNCH-FIX-RATELIMIT] 必须携带 error_code=ip_rate_limited：
            // 此前缺省导致 error_code=""，儿童端按临时失败 1s 重试，5 分钟窗口
            // 过期后 10 次/60s 重新打满 → 无限自锁闭环（122 信根因）。
            // 该分支本身不 RecordFailure（封禁期内不计次，窗口不续期），
            // 审计照记（R9.1），冷却后自动放行自愈。
            return (new HandshakeResponse
            {
                Ok = false,
                Error = "尝试次数过多，请稍后再试",
                ErrorCode = "ip_rate_limited",
                PairStatus = "unpaired",
            }, null, null, null);
        }

        // [SEC-K1] 身份前置校验：mTLS 下对端必须提交客户端证书（P2pListenerService 已强制），
        // 指纹由 TLS 层提取、不可伪造；payload 自报 certFingerprint 一律不作为信任依据（红线 R3.2）。
        // 此守卫同时防御 TLS 配置回退（如有人改回 ClientCertificateRequired=false）。
        if (string.IsNullOrEmpty(peerFingerprint))
        {
            _logger.LogWarning("[P2P-Handshake][SEC-K1] 缺少客户端证书，拒绝: {DeviceId} @ {Ip}",
                req.DeviceId, remoteEndPoint);
            return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                "缺少客户端证书，无法校验身份", "unpaired"), null, null, null);
        }

        // [TASK-OPT-12-P4-DEEPEN] 家长端中继连接：不创建 Device，仅维护 relay_sessions
        // [SEC-K2] 必须携带 /api/relay/register 签发的会话令牌并匹配，防止冒充家长端接收儿童数据（红线 R2.3）
        if (req.Relay && req.DeviceId.StartsWith("parent-"))
        {
            if (string.IsNullOrEmpty(req.SessionToken))
            {
                _logger.LogWarning("[P2P-Handshake][SEC-K2] 家长端中继连接缺少会话令牌，拒绝: {DeviceId} @ {Ip}",
                    req.DeviceId, remoteEndPoint);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "缺少中继会话令牌", "paired"), null, null, null);
            }

            var parentSession = await db.RelaySessions
                .Where(s => s.DeviceId == req.DeviceId && s.Role == "parent")
                .OrderByDescending(s => s.ConnectedAt)
                .FirstOrDefaultAsync();

            // 令牌比对：常数时间比较，防时序侧信道
            if (parentSession == null || parentSession.SessionToken == null ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(parentSession.SessionToken),
                    Encoding.UTF8.GetBytes(req.SessionToken)))
            {
                _logger.LogWarning("[P2P-Handshake][SEC-K2] 家长端中继令牌不匹配，拒绝: {DeviceId} @ {Ip}",
                    req.DeviceId, remoteEndPoint);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "中继会话未授权", "paired"), null, null, null);
            }

            // [SEC-K2] 身份绑定：TLS 客户端证书指纹必须与注册时绑定的一致
            // （注册走 JWT 鉴权，指纹在此处被密码学验证，令牌+指纹双重绑定防冒充）
            if (string.IsNullOrEmpty(parentSession.Fingerprint) ||
                !string.Equals(parentSession.Fingerprint.Trim(), peerFingerprint.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[P2P-Handshake][SEC-K2] 家长端证书指纹不匹配，拒绝: {DeviceId} @ {Ip}",
                    req.DeviceId, remoteEndPoint);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "中继会话未授权", "paired"), null, null, null);
            }

            // 令牌有效：更新已注册会话（不再新建重复行，避免 UserId 悬空行）
            parentSession.Status = "connected";
            parentSession.ConnectedAt = DateTime.UtcNow;
            parentSession.DisconnectedAt = null;
            parentSession.IpAddress = remoteEndPoint;
            await db.SaveChangesAsync();

            _logger.LogInformation("[P2P-Handshake] 家长端中继连接已授权: {DeviceId} @ {Ip}",
                req.DeviceId, remoteEndPoint);

            // [SEC-K10] 中继授权成功审计（只记设备，不记令牌，红线 R9.1/R9.2）
            db.AuditLogs.Add(new AuditLog
            {
                Action = "p2p.relay.authorized",
                TargetType = "RelaySession",
                TargetId = parentSession.Id,
                Detail = JsonSerializer.Serialize(new { deviceId = req.DeviceId, role = "parent" }),
                IpAddress = handshakeIp,
            });
            await db.SaveChangesAsync();

            return (new HandshakeResponse
            {
                Ok = true,
                PairStatus = "paired",
                SessionId = Guid.NewGuid().ToString("N")[..12],
            }, null, null, null);  // 家长端不需要策略/重置下发
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
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "需要配对码，请在家长端生成配对二维码后扫码", "unpaired",
                    errorCode: "unpaired", countFailure: false), null, null, null);
            }

            // [SEC-K3] 配对码失败次数限制：单个配对码最多 10 次失败尝试（防 10^6 爆破）
            if (RequestRateLimiter.IsBlocked($"p2p-paircode:{req.PairCode}", 10, 300))
            {
                _logger.LogWarning("[P2P-Handshake][SEC-K3] 配对码尝试次数过多，拒绝: {PairCode}", req.PairCode);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "配对码尝试次数过多", "unpaired", req.PairCode), null, null, null);
            }

            // 验证配对码
            var pairingInfo = await db.PairingInfos
                .Where(p => p.PairCode == req.PairCode && p.PairStatus == "pending")
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (pairingInfo == null || pairingInfo.ExpiresAt < DateTime.UtcNow)
            {
                _logger.LogWarning("[P2P-Handshake] 配对码无效或已过期: {PairCode}", req.PairCode);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "配对码无效或已过期，请刷新二维码", "unpaired",
                    req.PairCode, "invalid_pairing_code"), null, null, null);
            }

            // 创建新设备
            // [SEC-K1] 指纹一律取 TLS 对端证书（顶部守卫已保证非空），不接受 payload 自报值
            device = new Device
            {
                DeviceId = req.DeviceId,
                DeviceName = req.DeviceName ?? req.DeviceId,
                Platform = req.Platform ?? "android",
                IpAddress = remoteEndPoint,
                CertFingerprint = peerFingerprint,
                PairCode = req.PairCode,
                PairStatus = "paired",
                OnlineStatus = "online",
                LastSeenAt = DateTime.UtcNow,
            };

            db.Devices.Add(device);
            await db.SaveChangesAsync();

            // 更新配对信息状态
            pairingInfo.TlsFingerprint = peerFingerprint;
            pairingInfo.PairStatus = "confirmed";
            pairingInfo.ConfirmedAt = DateTime.UtcNow;
            pairingInfo.DeviceId = device.Id;
            // [REQ] 配对码归属账号 → 绑定设备 owner（扫码绑定/中继绑定）
            if (!string.IsNullOrEmpty(pairingInfo.OwnerUserId))
                device.OwnerUserId = pairingInfo.OwnerUserId;

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

            // [SEC-K10] 配对成功审计（不记配对码明文，红线 R9.1/R9.2）
            db.AuditLogs.Add(new AuditLog
            {
                Action = "p2p.pair.success",
                TargetType = "Device",
                TargetId = device.Id,
                Detail = JsonSerializer.Serialize(new { deviceId = req.DeviceId }),
                IpAddress = handshakeIp,
            });

            // 重新加载带策略的设备
            device = await db.Devices.Include(d => d.Policy).FirstAsync(d => d.Id == device.Id);

            return (new HandshakeResponse
            {
                Ok = true,
                PairStatus = "paired",
                SessionId = Guid.NewGuid().ToString("N")[..12],
            }, BuildPolicyPushMessage(device.DeviceId, device.Policy, device.AppCategories), null, device.Id);
        }

        // 2. 已解绑/已吊销设备 — 需凭新的待确认配对码重新绑定，否则拒绝
        if (device.PairStatus == "revoked" || device.PairStatus == "unpaired")
        {
            var rePairInfo = string.IsNullOrEmpty(req.PairCode) ? null : await db.PairingInfos
                .Where(p => p.PairCode == req.PairCode && p.PairStatus == "pending")
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (rePairInfo == null)
            {
                var reason = device.PairStatus == "revoked" ? "设备已被吊销" : "设备已解绑，请重新扫码绑定";
                _logger.LogWarning("[P2P-Handshake] {Reason}: {DeviceId}", reason, req.DeviceId);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    reason, device.PairStatus, req.PairCode,
                    device.PairStatus, countFailure: false), null, null, device.Id);
            }

            // [SEC] 重绑归属校验（红线 R2.1/R2.2）：设备已有归属时，
            // 配对码必须由原 owner（或未绑定时任意家长）签发，防止他人凭 deviceId+自造码接管设备
            // [TASK-PRELAUNCH-FIX-SCAN] 归属不匹配 = 确定性拒绝（device_owned_by_other），
            // 不计入限速失败计数（非爆破信号，防重试雪崩触发 K3 封禁）
            if (!string.IsNullOrEmpty(device.OwnerUserId) &&
                (string.IsNullOrEmpty(rePairInfo.OwnerUserId) ||
                 !string.Equals(rePairInfo.OwnerUserId, device.OwnerUserId, StringComparison.Ordinal)))
            {
                _logger.LogWarning("[P2P-Handshake][SEC] 重绑配对码归属不匹配，拒绝: {DeviceId} owner={Owner} codeOwner={CodeOwner}",
                    req.DeviceId, device.OwnerUserId, rePairInfo.OwnerUserId);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "设备已被其他账号绑定，请先解绑", device.PairStatus, req.PairCode,
                    "device_owned_by_other", countFailure: false), null, null, device.Id);
            }

            // 重新绑定：解除吊销/解绑状态。
            // [SEC-K1] 凭新配对码重新绑定 = 信任轮换：采纳本次对端证书指纹为新可信指纹
            device.PairStatus = "paired";
            device.PairCode = req.PairCode;
            device.IsActive = true;
            if (!string.IsNullOrEmpty(peerFingerprint))
                device.CertFingerprint = peerFingerprint;
            // [SEC] 重绑归属：设备无主时绑定配对码签发账号
            if (string.IsNullOrEmpty(device.OwnerUserId) && !string.IsNullOrEmpty(rePairInfo.OwnerUserId))
                device.OwnerUserId = rePairInfo.OwnerUserId;

            // [TASK-PRELAUNCH-FIX-SCAN] 配对码一次性消费：重绑成功即 confirmed，
            // 避免同一 pending 码被反复用于重连（与首次绑定语义一致）
            rePairInfo.TlsFingerprint = peerFingerprint;
            rePairInfo.PairStatus = "confirmed";
            rePairInfo.ConfirmedAt = DateTime.UtcNow;
            rePairInfo.DeviceId = device.Id;
        }

        // 3. 已配对设备
        // [SEC-K1] 校验证书指纹必须先于任何状态更新（红线 R3.2：禁止接受任意客户端证书）：
        // 有指纹记录 → 必须与 TLS 对端证书一致，否则拒绝（防冒充/中间人，此前此处是静默覆盖 = 违反项）；
        // [SEC] 无指纹记录（历史设备）→ 不再 TOFU 采纳（任意证书可借此冒充无指纹设备 = P1 身份冒充口）。
        // 此类设备必须走"解绑 → 重新配对"路径（带有效配对码），由重绑流程写入新指纹。
        // peerFingerprint 已由顶部守卫保证非空，且一律取 TLS 层值，payload 自报值不作信任依据。
        if (!string.IsNullOrEmpty(device.CertFingerprint))
        {
            if (!string.Equals(device.CertFingerprint.Trim(), peerFingerprint.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[P2P-Handshake][SEC-K1] 证书指纹不匹配，拒绝: {DeviceId} 期望 {Expected} 实际 {Actual}",
                    req.DeviceId, device.CertFingerprint, peerFingerprint);
                return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                    "证书指纹不匹配，请重新配对", device.PairStatus, errorCode: "fingerprint_mismatch",
                    countFailure: false), null, null, device.Id);
            }
        }
        else
        {
            _logger.LogWarning("[P2P-Handshake][SEC-K1] 设备无指纹记录，拒绝并要求重新配对: {DeviceId}",
                req.DeviceId);
            return (await RejectHandshakeAsync(db, req.DeviceId, remoteEndPoint,
                "设备缺少可信指纹，请解绑后重新配对", device.PairStatus, errorCode: "unpaired",
                countFailure: false), null, null, device.Id);
        }

        // 更新状态
        device.OnlineStatus = "online";
        device.LastSeenAt = DateTime.UtcNow;
        device.IpAddress = remoteEndPoint;
        device.DeviceName = req.DeviceName ?? device.DeviceName;

        // [TASK-PRELAUNCH-FIX-SCAN] 已配对设备重连：忽略携带的配对码（含已确认旧码/无码），
        // 一律按证书指纹放行，不再走归属校验——旧码状态残留导致的"配对码归属不匹配"误拒
        // 是生产断线重连雪崩（误拒→重试→K3 封禁）的根因。归属绑定只在两条路径发生：
        // 1) 新设备首次配对（本方法 section 1）；2) /api/relay/register 配对码路径（有归属校验）。

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

        // [REQ] 每日限额重置：离线期间家长点了重置 → 重连握手时补推，推完清空待发标记
        string? resetPushJson = null;
        if (device.PendingResetAt.HasValue)
        {
            // SQLite 读取后 Kind=Unspecified，显式按 UTC 解释，避免补推时间戳偏移 8 小时
            var pendingUtc = DateTime.SpecifyKind(device.PendingResetAt.Value, DateTimeKind.Utc);
            resetPushJson = BuildLimitResetMessage(
                device.DeviceId,
                new DateTimeOffset(pendingUtc).ToUnixTimeSeconds());
            device.PendingResetAt = null;
            await db.SaveChangesAsync();
            _logger.LogInformation("[P2P-Handshake] 补推每日限额重置: {DeviceId}", device.DeviceId);
        }

        _logger.LogInformation("[P2P-Handshake] 设备已连接: {DeviceId} ({DeviceName}), status={PairStatus}",
            req.DeviceId, req.DeviceName, device.PairStatus);

        return (new HandshakeResponse
        {
            Ok = true,
            PairStatus = device.PairStatus,
            SessionId = Guid.NewGuid().ToString("N")[..12],
        }, policyMsg, resetPushJson, device.Id);
    }

    /// <summary>
    /// [SEC-K3] 握手拒绝统一出口：记录 IP/配对码失败计数（限速）+ 审计落库（红线 R4.2/R9.1）
    /// [TASK-PRELAUNCH-FIX-SCAN] 确定性拒绝（errorCode ∈ unpaired/revoked/device_owned_by_other/
    /// fingerprint_mismatch）时 countFailure=false：它不是爆破信号，计入会导致断线重连
    /// 携带旧状态无限重试时触发 IP 封禁（生产 117 根因的放大链），审计仍落库。
    /// </summary>
    private async Task<HandshakeResponse> RejectHandshakeAsync(
        AppDbContext db, string deviceId, string remoteEndPoint, string reason,
        string pairStatus, string? pairCode = null, string? errorCode = null,
        bool countFailure = true)
    {
        var ip = ExtractIp(remoteEndPoint);
        if (countFailure)
        {
            RequestRateLimiter.RecordFailure($"p2p-handshake:ip:{ip}", 10, 300);
            if (!string.IsNullOrEmpty(pairCode))
                RequestRateLimiter.RecordFailure($"p2p-paircode:{pairCode}", 10, 300);
        }

        db.AuditLogs.Add(new AuditLog
        {
            Action = "p2p.handshake_reject",
            TargetType = "Device",
            Detail = JsonSerializer.Serialize(new { deviceId, reason, errorCode }),
            IpAddress = ip,
        });
        await db.SaveChangesAsync();

        return new HandshakeResponse
        {
            Ok = false,
            Error = reason,
            ErrorCode = errorCode,
            PairStatus = pairStatus,
        };
    }

    /// <summary>
    /// 从 "ip:port" 提取 IP（兼容 IPv4 与 [IPv6]:port）
    /// </summary>
    private static string ExtractIp(string remoteEndPoint)
    {
        if (string.IsNullOrEmpty(remoteEndPoint)) return "unknown";
        var idx = remoteEndPoint.LastIndexOf(':');
        return idx > 0 ? remoteEndPoint[..idx].Trim('[', ']') : remoteEndPoint;
    }

    // ========== Usage Report ==========

    /// <summary>
    /// 处理儿童端使用上报 — 写入 usage_records + 更新 daily_summary
    /// [TASK-PRELAUNCH-P4] 口径修正：
    /// 1. “今日”按记录携带的设备本地日期聚合（不再用 UTC 日期，避免 00:00–08:00 跨日错位）
    /// 2. usage_records 按 (设备, 包名, 日期) upsert —— 儿童端每周期上报的是当日累计值，
    ///    追加写入会把累计值重复累加导致虚高；改为覆盖更新
    /// 3. 接收儿童端上报的重置偏移（dailyResetOffsetMinutes），落库设备行
    /// 4. sync_ack 的已用/剩余改用调整后口径（与设备页一致）
    /// </summary>
    public async Task<SyncAckMessage> HandleUsageReport(
        UsageReportRequest req, int dailyResetOffsetMinutes = 0, bool offsetReported = false,
        int? todayAdjustedMinutes = null, bool adjustedReported = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == req.DeviceId);
        if (device == null)
        {
            _logger.LogWarning("[P2P-Usage] 设备未找到: {DeviceId}", req.DeviceId);
            return new SyncAckMessage { BatchId = req.BatchId, Synced = 0 };
        }

        // 解析记录（时间无效的跳过），并确定本次批次日期（设备本地日）
        var parsed = new List<(UsageRecordItem Item, DateTime StartTime, DateTime? EndTime, string DateStr)>();
        foreach (var record in req.Records)
        {
            if (!DateTime.TryParse(record.StartTime, out var startTime))
                continue;

            DateTime? endTime = null;
            if (!string.IsNullOrEmpty(record.EndTime) && DateTime.TryParse(record.EndTime, out var et))
                endTime = et;

            parsed.Add((record, startTime, endTime, startTime.ToString("yyyy-MM-dd")));
        }

        var today = parsed.Count > 0
            ? parsed[0].DateStr
            : AppClock.TodayShanghai();

        // [FIX-100] 批内去重：同键(包名,日期)只处理最后一条（儿童端发的是当日累计快照，重复键取最新值），
        // 避免同批两条同键导致唯一索引冲突/双插入
        parsed = parsed
            .GroupBy(p => $"{p.Item.AppPackage}|{p.DateStr}")
            .Select(g => g.Last())
            .ToList();

        // [TASK-PRELAUNCH-P4] upsert 键：(包名, 日期) —— 覆盖更新当日累计，避免重复累加
        var existingRecords = await db.UsageRecords
            .Where(r => r.DeviceId == device.Id)
            .ToListAsync();
        var existingByKey = existingRecords
            .GroupBy(r => $"{r.AppPackage}|{r.StartTime:yyyy-MM-dd}")
            .ToDictionary(g => g.Key, g => g.First());

        var synced = 0;
        foreach (var (record, startTime, endTime, dateStr) in parsed)
        {
            var key = $"{record.AppPackage}|{dateStr}";
            if (existingByKey.TryGetValue(key, out var existing))
            {
                existing.AppName = record.AppName;
                existing.Category = NormalizeCategory(record.Category);
                existing.StartTime = startTime;
                existing.EndTime = endTime;
                existing.DurationSeconds = record.DurationSeconds;
                existing.IsBlocked = record.IsBlocked;
            }
            else
            {
                var newRecord = new UsageRecord
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
                db.UsageRecords.Add(newRecord);
                // [FIX-100] 登记本批已插入的键：若后续同键再出现则走更新分支，防止唯一索引冲突
                existingByKey[key] = newRecord;
            }
            synced++;
        }

        // [TASK-PRELAUNCH-P4] 设备上报了重置偏移 → 落库（当日有效，覆盖服务器端估计值）
        if (offsetReported)
        {
            device.LastResetOffsetMinutes = Math.Max(0, dailyResetOffsetMinutes);
            device.LastResetDate = today;
        }
        // [FIX-100] 儿童端上报调整后今日已用（最准确口径）→ 落库，展示/ack 优先采用
        if (adjustedReported && todayAdjustedMinutes.HasValue)
        {
            device.TodayAdjustedMinutes = Math.Max(0, todayAdjustedMinutes.Value);
        }
        device.LastReportAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 更新每日汇总（按批次设备本地日期）
        await UpdateDailySummary(db, device.Id, today);

        // 计算今日使用情况（调整后口径）
        var summary = await db.DailySummaries
            .FirstOrDefaultAsync(s => s.DeviceId == device.Id && s.SummaryDate == today);

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.DeviceId == device.Id);
        var dailyLimit = policy?.DailyLimitMinutes ?? 120;
        var rawMinutes = summary?.TotalMinutes ?? 0;
        // [FIX-100] 优先儿童端上报的调整后已用（当日有效），回退服务端计算（原始累计 − 偏移）
        var adjustedMinutes = AdjustedUsageCalculator.ResolveTodayUsedMinutes(
            device.TodayAdjustedMinutes, device.LastReportAt, DateTime.UtcNow,
            rawMinutes, device.LastResetOffsetMinutes, device.LastResetDate, today);
        var remaining = Math.Max(0, dailyLimit - adjustedMinutes);
        var overtimeLocked = summary != null && adjustedMinutes >= dailyLimit;

        _logger.LogDebug("[P2P-Usage] 设备 {DeviceId} 上报 {Count} 条记录, 今日原始 {Raw}min, 调整后 {Adj}min",
            req.DeviceId, synced, rawMinutes, adjustedMinutes);

        return new SyncAckMessage
        {
            BatchId = req.BatchId,
            Synced = synced,
            TodayTotalMinutes = adjustedMinutes,
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
        // [TASK-MILESTONE-V3] B11：账号隔离——广播公告仅本账号可见
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var ownerId = device != null ? await ResolveOwnerUserIdAsync(db, device) : null;
        var hasOwner = ownerId.HasValue;
        var ownerIdInt = ownerId ?? 0;
        var hasPendingAnnouncement = device != null && await db.Announcements
            .AnyAsync(a =>
                (a.Status == "published" || a.Status == "revoked") &&
                a.UpdatedAt >= oneHourAgo &&
                ((a.TargetDeviceId == device.Id) ||
                 (a.TargetDeviceId == null && hasOwner && a.CreatedBy == ownerIdInt)));

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
    /// 构建 2.0 limit_reset 完整消息 JSON（家长在 Web 端点击“重置当日限额”后下发）
    /// payload.resetAt：服务器重置时间（Unix 秒），儿童端据此记录当日偏移，重新开始计时
    /// </summary>
    public string BuildLimitResetMessage(string deviceId, long resetAtUnix)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.LimitReset,
            ["payload"] = new Dictionary<string, object>
            {
                ["deviceId"] = deviceId,
                ["resetAt"] = resetAtUnix,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
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
        // [SEC-K7] 仅推送合法 HH:mm：历史脏数据（ISO 时间戳）按未设置跳过，避免儿童端解析异常
        var sleepStart = NormalizeTime(policy.BedtimeStart);
        var sleepEnd = NormalizeTime(policy.BedtimeEnd);
        if (sleepStart != null && sleepEnd != null)
        {
            items.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["policyType"] = "sleep_time",
                ["deviceId"] = deviceId,
                ["isActive"] = true,
                ["version"] = version,
                ["sleepStart"] = sleepStart,
                ["sleepEnd"] = sleepEnd,
                ["label"] = "就寝时段",
            }));
        }

        // [TASK-PRELAUNCH-P1] 分类限额暂不可用：一律不向儿童端下发 category_limit 策略项
        // （即使库中残留历史启用值，也避免误生效）

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

    /// <summary>
    /// 构建 2.0 announcement_push 完整消息 JSON（payload.announcements 数组）
    /// [TASK-PRELAUNCH-P3] 附加 version/content_hash/requires_ack（终端据此去重，见 docs/adr/0004）
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
                ["version"] = announcement.Version,
                ["content_hash"] = GetContentHash(announcement),
                ["requires_ack"] = announcement.Priority == "urgent",
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
    /// [TASK-MILESTONE-V3] B5 构建“清除本地公告”指令消息
    /// 客户端收到后按 announcementIds 删除本地公告记录（含已过期/已读记录）。
    /// </summary>
    public string BuildAnnouncementClearJson(IReadOnlyList<int> announcementIds)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.AnnouncementClear,
            ["payload"] = new Dictionary<string, object>
            {
                ["announcementIds"] = announcementIds,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// [FIX] 构建补推公告消息：儿童端握手连接时下发最近 3 条已发布/已撤回公告。
    /// 解决“儿童端离线期间发布的公告永远收不到”（实时推送只在在线时生效）。
    ///
    /// [TASK-MILESTONE-V3] 增强（B5/B6/B11）：
    /// - B11 账号隔离：补推仅限本账号公告（广播按 CreatedBy，定向按 TargetDeviceId）；
    /// - B6 紧急未确认公告：重连必补推，不限于最近 3 条/1 小时窗口；
    /// - B5 删除墓碑：随同步携带 7 天内 cleared_ids，客户端清除本地残留。
    /// </summary>
    public async Task<string?> BuildAnnouncementSyncJson(string deviceId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return null;

        var ownerId = await ResolveOwnerUserIdAsync(db, device);
        // 提取为值类型：Nullable<T>.Value 在 EF 表达式树中不可靠翻译
        var ownerIdInt = ownerId ?? 0;
        var hasOwner = ownerId.HasValue;

        // [TASK-MILESTONE-V3] B11：本账号广播公告 + 定向本设备的公告
        IQueryable<Announcement> AccountAnnouncements() => hasOwner
            ? db.Announcements.Where(a =>
                (a.TargetDeviceId == device.Id) ||
                (a.TargetDeviceId == null && a.CreatedBy == ownerIdInt))
            : db.Announcements.Where(a => a.TargetDeviceId == device.Id);

        // 基础补推：最近 3 条已发布/已撤回
        var recent = await AccountAnnouncements()
            .Where(a => a.Status == "published" || a.Status == "revoked")
            .OrderByDescending(a => a.UpdatedAt)
            .Take(3)
            .ToListAsync();

        // [TASK-MILESTONE-V3] B6：紧急且未确认（无本设备 ack 记录）→ 必补推，无视窗口
        var urgentUnacked = await AccountAnnouncements()
            .Where(a => a.Status == "published" && a.Priority == "urgent")
            .Where(a => !db.AnnouncementDeliveries.Any(d =>
                d.AnnouncementId == a.Id && d.DeviceId == device.Id && d.AcknowledgedAt != null))
            .ToListAsync();

        // 合并去重（紧急未确认公告可能同时命中两条查询）
        var announcements = recent.Concat(urgentUnacked)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();

        // [TASK-MILESTONE-V3] B5：7 天内删除墓碑随同步下发（离线设备也能清除本地残留）
        var tombstoneCutoff = DateTime.UtcNow.AddDays(-7);
        var clearedIds = ownerId.HasValue
            ? await db.AnnouncementTombstones.AsNoTracking()
                .Where(t => t.CreatedBy == ownerId.Value && t.DeletedAt >= tombstoneCutoff)
                .Select(t => t.AnnouncementId)
                .ToListAsync()
            : new List<int>();

        if (announcements.Count == 0 && clearedIds.Count == 0) return null;

        var list = announcements.Select(a => new Dictionary<string, object>
        {
            ["id"] = a.Id,
            ["title"] = a.Title,
            ["content"] = a.Content,
            ["priority"] = a.Priority switch
            {
                "urgent" => 2,
                "important" => 1,
                _ => 0,
            },
            ["created_at"] = new DateTimeOffset(a.CreatedAt).ToUnixTimeSeconds(),
            ["expires_at"] = a.ValidUntil.HasValue
                ? new DateTimeOffset(a.ValidUntil.Value).ToUnixTimeSeconds()
                : 0L,
            // [TASK-PRELAUNCH-P3] 去重字段（补推同口径）
            ["version"] = a.Version,
            ["content_hash"] = GetContentHash(a),
            ["requires_ack"] = a.Priority == "urgent",
        }).ToList();

        var payload = new Dictionary<string, object>
        {
            ["announcements"] = list,
            ["action"] = "sync",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        if (clearedIds.Count > 0)
            payload["cleared_ids"] = clearedIds;

        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.AnnouncementPush,
            ["payload"] = payload,
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
    /// [FIX-100] 可携带 todayAdjustedMinutes：儿童端实时累计的调整后今日已用（最准确口径）
    /// </summary>
    public async Task<SyncAckMessage> HandleUsageReportLegacy(
        string deviceId, string recordsJson, int dailyResetOffsetMinutes = 0, bool offsetReported = false,
        int? todayAdjustedMinutes = null, bool adjustedReported = false)
    {
        var request = new UsageReportRequest
        {
            DeviceId = deviceId,
            Records = ParseLegacyRecords(recordsJson),
        };
        return await HandleUsageReport(
            request, dailyResetOffsetMinutes, offsetReported, todayAdjustedMinutes, adjustedReported);
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
    /// [TASK-PRELAUNCH-P3] 每次成功推送记录送达（push_count++/last_pushed_at）
    /// [TASK-MILESTONE-V3] B11 账号隔离：广播仅推发布者账号下的设备（此前跨账号泄露）
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
                var pushed = await p2pService.SendToDevice(device.DeviceId, json);
                if (pushed)
                {
                    await RecordDeliveryPushAsync(announcement.Id, device.Id);
                    _logger.LogInformation("[P2P-Announce] 公告已推送到设备 {DeviceId}: {Title}", device.DeviceId, announcement.Title);
                }
            }
        }
        else
        {
            // 广播到发布者账号下的在线设备（B11）
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var devices = await GetBroadcastAudienceAsync(db, announcement.CreatedBy);

            var pushedCount = 0;
            foreach (var device in devices)
            {
                var pushed = await p2pService.SendToDevice(device.DeviceId, json);
                if (pushed)
                {
                    await RecordDeliveryPushAsync(announcement.Id, device.Id);
                    pushedCount++;
                }
            }
            _logger.LogInformation("[P2P-Announce] 公告已广播到 {Count}/{Total} 个账号内设备: {Title}",
                pushedCount, devices.Count, announcement.Title);
        }
    }

    /// <summary>
    /// [TASK-MILESTONE-V3] B5：公告删除后推送“清除本地公告”指令
    /// 受众与发布广播同口径（B11：发布者账号设备；定向公告仅目标设备）。
    /// 设备离线时由其重连同步的 cleared_ids 墓碑覆盖。
    /// </summary>
    public async Task PushAnnouncementClearAsync(Announcement announcement, P2pListenerService? p2pService)
    {
        if (p2pService == null) return;

        var json = BuildAnnouncementClearJson(new List<int> { announcement.Id });

        if (announcement.TargetDeviceId != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.FindAsync(announcement.TargetDeviceId.Value);
            if (device != null)
            {
                var pushed = await p2pService.SendToDevice(device.DeviceId, json);
                _logger.LogInformation("[P2P-Announce] 公告清除指令已推送到设备 {DeviceId}: id={Id}, pushed={Pushed}",
                    device.DeviceId, announcement.Id, pushed);
            }
        }
        else
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var devices = await GetBroadcastAudienceAsync(db, announcement.CreatedBy);

            var pushedCount = 0;
            foreach (var device in devices)
            {
                if (await p2pService.SendToDevice(device.DeviceId, json)) pushedCount++;
            }
            _logger.LogInformation("[P2P-Announce] 公告清除指令已广播到 {Count}/{Total} 个账号内设备: id={Id}",
                pushedCount, devices.Count, announcement.Id);
        }
    }

    /// <summary>
    /// [TASK-MILESTONE-V3] B11 广播受众解析：已配对激活设备中归属发布者账号的设备。
    /// OwnerUserId 兼容用户 ID 或用户名两种历史格式。
    /// </summary>
    private static async Task<List<Device>> GetBroadcastAudienceAsync(AppDbContext db, int createdBy)
    {
        var devices = await db.Devices
            .Where(d => d.PairStatus == "paired" && d.IsActive)
            .ToListAsync();
        if (devices.Count == 0) return devices;

        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Username);

        bool BelongsToPublisher(Device d)
        {
            if (string.IsNullOrEmpty(d.OwnerUserId)) return false;
            if (int.TryParse(d.OwnerUserId, out var uid)) return uid == createdBy;
            return users.TryGetValue(d.OwnerUserId, out var u) && u.Id == createdBy;
        }

        return devices.Where(BelongsToPublisher).ToList();
    }

    /// <summary>
    /// [TASK-APP-UPDATE-V1] D2：广播 update_available 到全部在线设备（不限账号）。
    /// 推送仅为「触发信号 + 摘要」，客户端收到后应再调 /api/update/check 拉取完整清单
    /// （url/sha256/changelog 不入推送载荷，减小广播体量、避免摘要与清单不一致）。
    /// </summary>
    public async Task<int> PushUpdateAvailable(AppUpdate update, P2pListenerService? p2pService)
    {
        if (p2pService == null) return 0;

        var json = BuildUpdateAvailableJson(update);
        var pushed = await p2pService.BroadcastToAll(json);
        _logger.LogInformation("[P2P-Update] update_available 已广播 {Count} 台在线设备: v{Version}({Code})",
            pushed, update.VersionName, update.VersionCode);
        return pushed;
    }

    /// <summary>
    /// update_available 消息体：{ updateId, versionCode, versionName, minVersionCode, publishedAt }
    /// </summary>
    public string BuildUpdateAvailableJson(AppUpdate update)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = P2pMessageType.UpdateAvailable,
            ["payload"] = new Dictionary<string, object>
            {
                ["update_id"] = update.Id,
                ["version_code"] = update.VersionCode,
                ["version_name"] = update.VersionName,
                ["min_version_code"] = update.MinVersionCode,
                ["published_at"] = update.PublishedAt.HasValue
                    ? new DateTimeOffset(update.PublishedAt.Value).ToUnixTimeSeconds()
                    : 0L,
            },
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] 送达记录 upsert：推送成功一次 push_count++（见 docs/adr/0004）
    /// </summary>
    private async Task RecordDeliveryPushAsync(int announcementId, int deviceDbId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AnnouncementDeliveries
            .FirstOrDefaultAsync(d => d.AnnouncementId == announcementId && d.DeviceId == deviceDbId);
        if (row == null)
        {
            row = new AnnouncementDelivery { AnnouncementId = announcementId, DeviceId = deviceDbId };
            db.AnnouncementDeliveries.Add(row);
        }
        row.PushCount++;
        row.LastPushedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] 处理儿童端公告已显示事件（announcement_displayed）
    /// 落库 displayed_at（保留首次显示时间），不覆盖已有值
    /// </summary>
    public async Task HandleAnnouncementDisplayed(string childDeviceId, string? announcementIdRaw, long? displayedAtUnix)
    {
        await UpdateDeliveryEventAsync(childDeviceId, announcementIdRaw, displayedAtUnix,
            setDisplayed: true, setAcked: false);
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] 处理儿童端公告确认回执（announcement_ack）
    /// 落库 acknowledged_at（保留首次确认时间），不只中继转发
    /// </summary>
    public async Task HandleAnnouncementAck(string childDeviceId, string? announcementIdRaw, long? acknowledgedAtUnix)
    {
        await UpdateDeliveryEventAsync(childDeviceId, announcementIdRaw, acknowledgedAtUnix,
            setDisplayed: false, setAcked: true);
    }

    private async Task UpdateDeliveryEventAsync(string childDeviceId, string? announcementIdRaw,
        long? unixTime, bool setDisplayed, bool setAcked)
    {
        if (!int.TryParse(announcementIdRaw, out var announcementId))
        {
            _logger.LogWarning("[P2P-Announce] 回执公告 ID 非法: {Id}（device={DeviceId}）",
                announcementIdRaw, childDeviceId);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == childDeviceId);
        if (device == null)
        {
            _logger.LogWarning("[P2P-Announce] 回执设备未注册: {DeviceId}", childDeviceId);
            return;
        }

        // [SEC] 回执归属校验：公告必须存在，且面向本设备（广播或定向本设备），
        // 否则忽略并审计（防伪造他人公告送达记录，红线 R2.2）
        var announcement = await db.Announcements
            .FirstOrDefaultAsync(a => a.Id == announcementId);
        if (announcement == null ||
            (announcement.TargetDeviceId is > 0 && announcement.TargetDeviceId != device.Id))
        {
            _logger.LogWarning("[P2P-Announce][SEC] 回执公告不存在或非本设备目标，忽略: ann={Id} device={DeviceId}",
                announcementId, childDeviceId);
            db.AuditLogs.Add(new AuditLog
            {
                Action = "p2p.announcement_ack_invalid",
                TargetType = "Announcement",
                TargetId = announcementId,
                Detail = JsonSerializer.Serialize(new { deviceId = childDeviceId }),
            });
            await db.SaveChangesAsync();
            return;
        }

        var row = await db.AnnouncementDeliveries
            .FirstOrDefaultAsync(d => d.AnnouncementId == announcementId && d.DeviceId == device.Id);
        if (row == null)
        {
            // 设备离线期间发布、重连补推前收到回执等边界：补建一行，推送次数为 0
            row = new AnnouncementDelivery { AnnouncementId = announcementId, DeviceId = device.Id };
            db.AnnouncementDeliveries.Add(row);
        }

        var eventTime = unixTime is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime.Value).UtcDateTime
            : DateTime.UtcNow;
        if (setDisplayed && row.DisplayedAt == null)
            row.DisplayedAt = eventTime;
        if (setAcked && row.AcknowledgedAt == null)
            row.AcknowledgedAt = eventTime;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] 公告内容哈希：SHA-256(title|content|priority) 前 16 位十六进制
    /// 内容未变则哈希不变，终端据此去重（见 docs/adr/0004）
    /// </summary>
    public static string GetContentHash(Announcement announcement)
    {
        if (!string.IsNullOrEmpty(announcement.ContentHash))
            return announcement.ContentHash;
        var raw = $"{announcement.Title}\n{announcement.Content}\n{announcement.Priority}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// [TASK-PRELAUNCH-P3] 计算并保存内容哈希（发布时调用）
    /// </summary>
    public static string ComputeContentHash(string title, string content, string priority)
    {
        var raw = $"{title}\n{content}\n{priority}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
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
    /// [TASK-MILESTONE-V3] B11/B13 设备归属账号解析：owner_user_id 兼容用户 ID 或用户名两种格式
    /// </summary>
    private static async Task<int?> ResolveOwnerUserIdAsync(AppDbContext db, Device device)
    {
        if (string.IsNullOrEmpty(device.OwnerUserId)) return null;
        if (int.TryParse(device.OwnerUserId, out var uid)) return uid;
        return (await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == device.OwnerUserId))?.Id;
    }

    /// <summary>
    /// 更新每日汇总（按 device + date upsert）
    /// </summary>
    private static async Task UpdateDailySummary(AppDbContext db, int deviceId, string summaryDate)
    {
        // SQLite 无法翻译 StartTime.ToString("yyyy-MM-dd")，先取该设备全部记录再内存过滤
        // （[TASK-OPT-12-P4-DEEPEN] 修复：usage_report 处理异常导致中继转发中断）
        var deviceRecords = await db.UsageRecords
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync();
        var records = deviceRecords
            .Where(r => r.StartTime.ToString("yyyy-MM-dd") == summaryDate)
            .ToList();

        if (records.Count == 0) return;

        var totalSeconds = records.Sum(r => r.DurationSeconds);
        var gameSeconds = records.Where(r => r.Category == "game").Sum(r => r.DurationSeconds);
        var socialSeconds = records.Where(r => r.Category == "social").Sum(r => r.DurationSeconds);
        var videoSeconds = records.Where(r => r.Category == "video").Sum(r => r.DurationSeconds);
        var learningSeconds = records.Where(r => r.Category == "learning").Sum(r => r.DurationSeconds);
        // [TASK-PRELAUNCH-P2] other 桶 = 总量减四类之和（动态分类不再丢出 total，保持桶和=总时长）
        var otherSeconds = totalSeconds - (gameSeconds + socialSeconds + videoSeconds + learningSeconds);
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
    /// [TASK-PRELAUNCH-P2] 不再折叠为固定四类：保留终端实际分类值（支持细分类，如 short_video/browser 等），
    /// 仅 study → learning 归一、空值归 other；报告按实际分类动态聚合
    /// </summary>
    private static string NormalizeCategory(string category)
    {
        var c = category?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c)) return "other";
        return c == "study" ? "learning" : c;
    }

    /// <summary>
    /// [SEC-K7] 时间归一化：仅合法 HH:mm 原样返回，其余（历史 ISO 时间戳等）视为 null
    /// </summary>
    private static string? NormalizeTime(string? value)
        => string.IsNullOrEmpty(value) ? null
           : TimeOnly.TryParseExact(value, "HH:mm", out _) ? value : null;

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
