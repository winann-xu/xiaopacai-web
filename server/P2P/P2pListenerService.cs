using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace XiaopacaiWeb.P2P;

/// <summary>
/// P2P TCP/TLS 监听服务 — LEGACY-e 方案
///
/// 监听端口默认 9527（可配置），接受 Android 儿童端 TCP+TLS 连接，
/// 使用 4 字节大端长度前缀 + JSON 帧协议进行双向通信。
///
/// 兼容 2.0 Android 儿童端协议（不修改 APK）。
/// </summary>
public class P2pListenerService : IHostedService
{
    private readonly int _listenPort;
    private readonly P2pCertificateService _certService;
    private readonly P2pMessageHandler _messageHandler;
    private readonly ILogger<P2pListenerService> _logger;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _reaperLoop;

    // [SEC] 连接数上限（防放大攻击/连接耗尽，红线 R4.3）
    private const int MaxConnectionsGlobal = 200;
    private const int MaxConnectionsPerIp = 10;
    private int _activeConnections;
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();

    // [SEC] 心跳超时回收：心跳间隔约 30s，3 倍无心跳即判定失联（静默断网无 FIN 场景）
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 活跃的客户端会话（deviceId → session info）
    /// </summary>
    private readonly ConcurrentDictionary<string, P2pSession> _sessions = new();

    public P2pListenerService(
        IConfiguration configuration,
        P2pCertificateService certService,
        P2pMessageHandler messageHandler,
        ILogger<P2pListenerService> logger)
    {
        _listenPort = configuration.GetValue<int>("P2P:ListenPort", 9527);
        _certService = certService;
        _messageHandler = messageHandler;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前活跃会话数
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// 获取指定设备的会话
    /// </summary>
    public P2pSession? GetSession(string deviceId)
    {
        _sessions.TryGetValue(deviceId, out var session);
        return session;
    }

    /// <summary>
    /// 向指定设备主动推送消息（策略更新/公告推送）
    /// </summary>
    public async Task<bool> SendToDevice(string deviceId, string messageJson)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || session.SslStream == null)
            return false;

        try
        {
            await WriteFrameAsync(session.SslStream, messageJson);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[P2P] 推送消息到设备 {DeviceId} 失败", deviceId);
            _sessions.TryRemove(deviceId, out _);
            return false;
        }
    }

    // ========== IHostedService ==========

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 初始化证书（首次运行自动生成）
        _certService.GetOrCreateCertificate();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start();

        _acceptLoop = AcceptLoopAsync(_cts.Token);

        // [SEC] 心跳超时回收器：周期扫描静默失联会话（防死会话永久占用 DB 在线态/中继路由）
        _reaperLoop = ReaperLoopAsync(_cts.Token);

        _logger.LogInformation("[P2P] TCP/TLS 监听已启动 → 0.0.0.0:{Port}, TLS 1.2/1.3", _listenPort);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[P2P] 正在停止监听...");

        _cts?.Cancel();

        // 关闭所有活跃会话
        foreach (var kvp in _sessions)
        {
            try
            {
                kvp.Value.SslStream?.Close();
                kvp.Value.TcpClient?.Close();
            }
            catch { /* ignore */ }
        }
        _sessions.Clear();

        _listener?.Stop();

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
        }

        if (_reaperLoop != null)
        {
            try { await _reaperLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("[P2P] 监听已停止");
    }

    // ========== Accept 循环 ==========

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                var remoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
                _logger.LogDebug("[P2P] 新 TCP 连接: {RemoteEndPoint}", remoteEndPoint);

                // [SEC] 连接数上限：全局 + 每 IP（防连接耗尽放大攻击，红线 R4.3）
                var remoteIp = ExtractIp(remoteEndPoint);
                var ipCount = _connectionsPerIp.AddOrUpdate(remoteIp, 1, (_, c) => c + 1);
                var globalCount = Interlocked.Increment(ref _activeConnections);
                if (ipCount > MaxConnectionsPerIp || globalCount > MaxConnectionsGlobal)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    _connectionsPerIp.AddOrUpdate(remoteIp, 0, (_, c) => Math.Max(0, c - 1));
                    tcpClient.Close();
                    _logger.LogWarning("[P2P][SEC] 连接超上限被拒绝: {RemoteEndPoint} (perIp={IpCount}, global={Global})",
                        remoteEndPoint, ipCount, globalCount);
                    continue;
                }

                // 每个客户端一个独立 Task
                _ = HandleConnectionAsync(tcpClient, ct, remoteIp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // 监听器已关闭
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[P2P] Accept 异常");
            }
        }
    }

    // ========== TLS 连接处理 ==========

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken ct, string remoteIp)
    {
        var remoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        SslStream? sslStream = null;

        try
        {
            // 获取证书（确保已加载）
            var certificate = _certService.GetOrCreateCertificate();

            // 创建 TLS 流
            // [SEC-K1] 注意：TLS 回调返回 true 仅表示"接受任何客户端证书"，并非身份豁免——
            // 真实身份校验在 P2pMessageHandler.HandleHandshake 完成（cert_fingerprint 与
            // devices.cert_fingerprint 固定比对/TOFU 采纳，不匹配即拒绝，见安全基线 R3.2/R3.3）。
            // TLS 层无法将证书映射到具体设备（握手指纹字段才携带 deviceId），因此此处必须放行，
            // 由握手层做强校验；若在此回调按 CA 链拒绝，自签名设备将全部无法连接。
            sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                // 不要求客户端证书（儿童端使用自签名证书，通过指纹校验）
                userCertificateValidationCallback: (sender, clientCert, chain, errors) => true);

            // TLS 1.2 / 1.3，不检查证书吊销
            // [SEC-K1] 双向 TLS（mTLS）：强制要求客户端证书（红线 R3.2 禁止"接受任意客户端证书"）。
            // 客户端（Android 3.x）生成设备身份证书并在 TLS 层提交；无证书的旧版客户端 TLS 握手直接失败，
            // 不会进入消息层（身份校验见 P2pMessageHandler 指纹固定比对）。
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });

            _logger.LogDebug("[P2P] TLS 握手完成: {RemoteEndPoint}, 协议={Protocol}",
                remoteEndPoint, sslStream.SslProtocol);

            // 获取对端证书指纹（儿童端可能传自签名证书）
            string? peerFingerprint = null;
            if (sslStream.RemoteCertificate is X509Certificate2 peerCert)
            {
                peerFingerprint = P2pCertificateService.ComputeFingerprint(peerCert);
            }

            // 消息循环：读帧 → 处理 → 响应
            await MessageLoopAsync(sslStream, remoteEndPoint, peerFingerprint, ct);
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "[P2P] TLS 认证失败: {RemoteEndPoint}", remoteEndPoint);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "[P2P] 连接 IO 异常: {RemoteEndPoint}", remoteEndPoint);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] 连接处理异常: {RemoteEndPoint}", remoteEndPoint);
        }
        finally
        {
            try
            {
                sslStream?.Close();
                tcpClient.Close();
            }
            catch { /* ignore */ }

            // [SEC] 释放连接配额
            Interlocked.Decrement(ref _activeConnections);
            _connectionsPerIp.AddOrUpdate(remoteIp, 0, (_, c) => Math.Max(0, c - 1));
        }
    }

    // ========== 消息循环 ==========

    private async Task MessageLoopAsync(SslStream sslStream, string remoteEndPoint,
        string? peerFingerprint, CancellationToken ct)
    {
        string? deviceId = null;
        P2pSession? mySession = null;   // 本连接注册的会话（断线清理时比对引用，防竞态误删新会话）
        var unauthenticatedMessages = 0;

        while (!ct.IsCancellationRequested)
        {
            // [SEC] 帧读超时 60s：防"只发长度前缀后挂死"的慢速占用攻击（红线 R4.3）
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            frameCts.CancelAfter(TimeSpan.FromSeconds(60));
            string? frame;
            try
            {
                frame = await ReadFrameAsync(sslStream, frameCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("[P2P][SEC] 读帧超时，关闭连接: {RemoteEndPoint}", remoteEndPoint);
                break;
            }
            if (frame == null) break; // 连接关闭

            P2pEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<P2pEnvelope>(frame);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[P2P] JSON 解析失败: {RemoteEndPoint}", remoteEndPoint);
                continue;
            }

            if (envelope == null || string.IsNullOrEmpty(envelope.Type))
                continue;

            var deviceIdHolder = new DeviceIdHolder { Value = deviceId };

            // [SEC-P0] 握手门禁：未完成握手的连接只允许发 Handshake；
            // 其余消息一律拒绝并计数，累计 5 条即断开（红线 R2.2/R4.3）
            if (deviceIdHolder.Value == null && envelope.Type != P2pMessageType.Handshake)
            {
                unauthenticatedMessages++;
                _logger.LogWarning("[P2P][SEC] 未握手连接发送 {Type} 被拒绝（第 {N} 次）: {RemoteEndPoint}",
                    envelope.Type, unauthenticatedMessages, remoteEndPoint);
                if (unauthenticatedMessages >= 5) break;
                continue;
            }

            var keepConnection = await HandleMessageAsync(sslStream, envelope, deviceIdHolder,
                peerFingerprint, remoteEndPoint, ct);
            deviceId = deviceIdHolder.Value;
            if (deviceId != null && mySession == null && _sessions.TryGetValue(deviceId, out var registered))
                mySession = registered; // 握手成功后记住本连接注册的会话引用
            if (mySession != null) mySession.LastHeartbeat = DateTime.UtcNow; // 任何帧都视为存活信号
            if (!keepConnection) break;
        }

        // 连接断开，清理会话
        // [SEC] 仅当当前登记会话仍是本连接注册的那个时才移除，
        // 防止旧连接断线清理误删设备刚重连建立的新会话（会话互踢竞态）
        if (deviceId != null && mySession != null &&
            _sessions.TryGetValue(deviceId, out var current) && ReferenceEquals(current, mySession))
        {
            _sessions.TryRemove(deviceId, out _);
            await _messageHandler.OnDeviceDisconnected(deviceId);
            _logger.LogInformation("[P2P] 设备断开: {DeviceId} ({RemoteEndPoint})", deviceId, remoteEndPoint);
        }
    }

    /// <summary>
    /// 消息分发。返回 false 表示连接应关闭（握手被拒/未认证滥用）。
    /// </summary>
    private async Task<bool> HandleMessageAsync(SslStream sslStream, P2pEnvelope envelope,
        DeviceIdHolder deviceIdHolder, string? peerFingerprint, string remoteEndPoint, CancellationToken ct)
    {
        try
        {
            switch (envelope.Type)
            {
                case P2pMessageType.Handshake:
                    {
                        var req = DeserializePayload<HandshakeRequest>(envelope.Payload);
                        if (req == null) return true;

                        var (response, policyPushJson, resetPushJson, dbDeviceId) =
                            await _messageHandler.HandleHandshake(req, peerFingerprint, remoteEndPoint);

                        if (response.Ok)
                        {
                            deviceIdHolder.Value = req.DeviceId;
                            _sessions[deviceIdHolder.Value] = new P2pSession
                            {
                                DeviceId = deviceIdHolder.Value,
                                SslStream = sslStream,
                                TcpClient = null,
                                ConnectedAt = DateTime.UtcNow,
                                LastHeartbeat = DateTime.UtcNow,
                            };
                        }

                        // 2.0 协议：握手成功直接回 policy_update 完整消息（儿童端不等待 handshake_ack）
                        if (response.Ok && !string.IsNullOrEmpty(policyPushJson) && deviceIdHolder.Value != null)
                        {
                            await WriteFrameAsync(sslStream, policyPushJson);
                            // [FIX] 补推最近公告：儿童端离线期间发布的公告，重连后也能收到
                            var annJson = await _messageHandler.BuildAnnouncementSyncJson(deviceIdHolder.Value);
                            if (!string.IsNullOrEmpty(annJson))
                            {
                                await WriteFrameAsync(sslStream, annJson);
                            }
                            // [REQ] 离线期间家长点击“重置当日限额” → 重连后补推
                            if (!string.IsNullOrEmpty(resetPushJson))
                            {
                                await WriteFrameAsync(sslStream, resetPushJson);
                            }
                        }
                        else if (!response.Ok)
                        {
                            // [SEC] 握手被拒：发送拒绝回执后关闭连接（防同连接反复试探，红线 R4.2）
                            _logger.LogWarning("[P2P] 握手被拒绝: {DeviceId}, 原因={Error}",
                                req.DeviceId, response.Error);
                            try
                            {
                                await WriteFrameAsync(sslStream,
                                    $"{{\"type\":\"handshake_rejected\",\"error\":\"{EscapeJsonString(response.Error)}\"}}");
                            }
                            catch { /* ignore */ }
                            return false;
                        }
                        break;
                    }

                case P2pMessageType.UsageReport:
                    {
                        // [SEC-P0] 上报身份必须绑定握手会话：payload 自报 deviceId 与会话不一致即拒绝
                        // （防任意证书连接伪造他人设备上报，红线 R2.2/R2.3）
                        if (envelope.Payload is JsonElement usagePayload)
                        {
                            // [SEC] 上报消息级限速：每 IP 60 条/分钟（防高频灌库放大，红线 R4.3）
                            if (!XiaopacaiWeb.Security.RequestRateLimiter.Allow(
                                    $"p2p-usage:{ExtractIp(remoteEndPoint)}", 60, 60))
                            {
                                _logger.LogWarning("[P2P][SEC] 上报频率超限，丢弃: {RemoteEndPoint}", remoteEndPoint);
                                return true;
                            }

                            var payloadDeviceId = GetPayloadString(usagePayload, "deviceId");
                            var deviceId = deviceIdHolder.Value!;
                            if (!string.IsNullOrEmpty(payloadDeviceId) &&
                                !string.Equals(payloadDeviceId, deviceId, StringComparison.Ordinal))
                            {
                                _logger.LogWarning(
                                    "[P2P][SEC] usage_report deviceId 与会话不符被拒绝: session={Session}, payload={Payload} @ {Ip}",
                                    deviceId, payloadDeviceId, remoteEndPoint);
                                return false;
                            }

                            var recordsJson = GetPayloadString(usagePayload, "records") ?? "[]";
                            // [TASK-PRELAUNCH-P4] 读取儿童端上报的重置偏移（缺省 0 = 未重置）
                            var offsetReported = usagePayload.TryGetProperty(
                                "dailyResetOffsetMinutes", out var offsetEl)
                                && offsetEl.ValueKind == JsonValueKind.Number;
                            var offsetVal = 0L;
                            if (offsetReported) offsetEl.TryGetInt64(out offsetVal);
                            // [FIX-100] 读取儿童端上报的调整后今日已用（最准确口径；缺省 null = 未上报）
                            var adjustedReported = usagePayload.TryGetProperty(
                                "todayAdjustedMinutes", out var adjustedEl)
                                && adjustedEl.ValueKind == JsonValueKind.Number;
                            int? adjustedVal = null;
                            if (adjustedReported && adjustedEl.TryGetInt64(out var adjustedRaw))
                                adjustedVal = (int)Math.Min(int.MaxValue, adjustedRaw);
                            var ack = await _messageHandler.HandleUsageReportLegacy(
                                deviceId, recordsJson,
                                offsetReported ? (int)Math.Min(int.MaxValue, offsetVal) : 0,
                                offsetReported,
                                adjustedVal,
                                adjustedReported);
                            await WriteFrameAsync(sslStream, _messageHandler.BuildSyncAckJson(ack.Synced));

                            // [TASK-OPT-12-P4-DEEPEN] 中继转发：儿童端使用上报实时转发给绑定家长端
                            await _messageHandler.RelayMessageToParent(
                                deviceId, EnvelopeToJson(envelope), this);
                        }
                        break;
                    }

                // [TASK-OPT-12-P4-DEEPEN] 儿童端公告确认回执 → 中继转发给绑定家长端
                // [TASK-PRELAUNCH-P3] 同时落库 acknowledged_at（不只中继，见 docs/adr/0004）
                case P2pMessageType.AnnouncementAck:
                    {
                        if (deviceIdHolder.Value != null)
                        {
                            if (envelope.Payload is JsonElement ackPayload)
                            {
                                var announcementId = GetPayloadString(ackPayload, "announcementId");
                                long.TryParse(GetPayloadString(ackPayload, "acknowledgedAt"), out var ackedAt);
                                await _messageHandler.HandleAnnouncementAck(
                                    deviceIdHolder.Value, announcementId, ackedAt > 0 ? ackedAt : null);
                            }
                            await _messageHandler.RelayMessageToParent(
                                deviceIdHolder.Value, EnvelopeToJson(envelope), this);
                        }
                        break;
                    }

                // [TASK-PRELAUNCH-P3] 儿童端公告已显示事件 → 落库 displayed_at + 中继家长端
                case P2pMessageType.AnnouncementDisplayed:
                    {
                        if (deviceIdHolder.Value != null)
                        {
                            if (envelope.Payload is JsonElement displayedPayload)
                            {
                                var announcementId = GetPayloadString(displayedPayload, "announcementId");
                                long.TryParse(GetPayloadString(displayedPayload, "displayedAt"), out var shownAt);
                                await _messageHandler.HandleAnnouncementDisplayed(
                                    deviceIdHolder.Value, announcementId, shownAt > 0 ? shownAt : null);
                            }
                            await _messageHandler.RelayMessageToParent(
                                deviceIdHolder.Value, EnvelopeToJson(envelope), this);
                        }
                        break;
                    }

                case P2pMessageType.Heartbeat:
                    {
                        // Android 心跳 payload 仅含 timestamp，deviceId 从握手会话中获取
                        if (deviceIdHolder.Value != null)
                        {
                            await _messageHandler.HandleHeartbeat(new HeartbeatMessage
                            {
                                DeviceId = deviceIdHolder.Value,
                            });

                            if (_sessions.TryGetValue(deviceIdHolder.Value, out var session))
                            {
                                session.LastHeartbeat = DateTime.UtcNow;
                            }
                        }
                        await WriteFrameAsync(sslStream, _messageHandler.BuildHeartbeatAckJson());
                        break;
                    }

                default:
                    _logger.LogDebug("[P2P] 未知消息类型: {Type} from {RemoteEndPoint}", envelope.Type, remoteEndPoint);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] 消息处理异常: type={Type}", envelope.Type);
        }
        return true;
    }

    // ========== 帧协议（4 字节大端长度前缀 + JSON） ==========

    /// <summary>
    /// 读取一帧（阻塞直到完整帧到达或连接关闭）
    /// </summary>
    private static async Task<string?> ReadFrameAsync(SslStream stream, CancellationToken ct)
    {
        // 读取 4 字节长度前缀（大端序）
        var lenBuf = new byte[4];
        var bytesRead = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
        if (bytesRead < 4) return null; // 连接关闭

        // 大端序 → int
        if (BitConverter.IsLittleEndian)
            Array.Reverse(lenBuf);
        var frameLength = BitConverter.ToInt32(lenBuf, 0);

        // 合理性校验（最大 1MB）
        if (frameLength <= 0 || frameLength > 1_048_576)
        {
            throw new InvalidOperationException($"非法的帧长度: {frameLength}");
        }

        // 读取帧体
        var bodyBuf = new byte[frameLength];
        bytesRead = await ReadExactAsync(stream, bodyBuf, 0, frameLength, ct);
        if (bytesRead < frameLength) return null;

        return Encoding.UTF8.GetString(bodyBuf, 0, frameLength);
    }

    /// <summary>
    /// 写入一帧（4 字节大端长度前缀 + JSON）
    /// </summary>
    private static async Task WriteFrameAsync(SslStream stream, string json)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var lenBytes = BitConverter.GetBytes(bodyBytes.Length);

        // 大端序
        if (BitConverter.IsLittleEndian)
            Array.Reverse(lenBytes);

        var frame = new byte[4 + bodyBytes.Length];
        Buffer.BlockCopy(lenBytes, 0, frame, 0, 4);
        Buffer.BlockCopy(bodyBytes, 0, frame, 4, bodyBytes.Length);

        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    /// <summary>
    /// 写入信封消息
    /// </summary>
    private static async Task WriteEnvelopeAsync(SslStream stream, string type, int seq, object payload)
    {
        var envelope = new
        {
            type = type,
            seq = seq,
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            payload = payload,
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await WriteFrameAsync(stream, json);
    }

    /// <summary>
    /// 精确读取指定字节数
    /// </summary>
    private static async Task<int> ReadExactAsync(SslStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (bytesRead == 0) break; // 连接关闭
            totalRead += bytesRead;
        }
        return totalRead;
    }

    // ========== 辅助 ==========

    /// <summary>
    /// [SEC] JSON 字符串转义（拒绝回执帧拼接用）
    /// </summary>
    private static string EscapeJsonString(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");

    /// <summary>
    /// 从 "ip:port" / "[v6]:port" 中提取纯 IP
    /// </summary>
    private static string ExtractIp(string remoteEndPoint)
    {
        var s = remoteEndPoint.Trim();
        if (s.StartsWith('['))
        {
            var idx = s.IndexOf(']');
            return idx > 0 ? s[1..idx] : s;
        }
        var lastColon = s.LastIndexOf(':');
        return lastColon > 0 ? s[..lastColon] : s;
    }

    /// <summary>
    /// [SEC] 心跳超时回收器：每 60s 扫描一次，静默失联（无 FIN）的会话强制回收，
    /// 避免死会话永久占用 _sessions 与 DB 在线态/中继路由（红线 4.4 会话生命周期）。
    /// 家长端中继会话（parent- 前缀）不参与回收（其存活由 relay_sessions 状态管理）。
    /// </summary>
    private async Task ReaperLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTime.UtcNow;
            foreach (var kv in _sessions)
            {
                if (kv.Key.StartsWith("parent-", StringComparison.Ordinal)) continue;
                if (now - kv.Value.LastHeartbeat <= HeartbeatTimeout) continue;

                _logger.LogWarning("[P2P][SEC] 心跳超时，回收会话: {DeviceId}（最后心跳 {Last}）",
                    kv.Key, kv.Value.LastHeartbeat);
                try { kv.Value.SslStream?.Close(); } catch { /* ignore */ }
                if (_sessions.TryRemove(kv.Key, out var removed) && ReferenceEquals(removed, kv.Value))
                    await _messageHandler.OnDeviceDisconnected(kv.Key);
            }
        }
    }

    private static T? DeserializePayload<T>(System.Text.Json.JsonElement? payload)
    {
        if (payload == null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(payload.Value.GetRawText(), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// 读取 payload 中的字符串字段（大小写容错：优先精确匹配，其次尝试 camelCase 变体）
    /// </summary>
    private static string? GetPayloadString(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }
        return null;
    }

    // [TASK-OPT-12-P4-DEEPEN] ========== 中继转发辅助 ==========

    /// <summary>
    /// 将信封重新序列化为 JSON（用于中继转发给家长端，字段名与原帧一致）
    /// </summary>
    private static string EnvelopeToJson(P2pEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}

/// <summary>
/// P2P 客户端会话信息
/// </summary>
public class P2pSession
{
    public string DeviceId { get; set; } = string.Empty;
    public System.Net.Security.SslStream? SslStream { get; set; }
    public TcpClient? TcpClient { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}

/// <summary>
/// 连接级 deviceId 可变持有者（异步方法内无法使用 ref 参数，改用引用类型传递）
/// </summary>
public sealed class DeviceIdHolder
{
    public string? Value { get; set; }
}
