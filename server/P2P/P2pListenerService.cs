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
    public async Task<bool> SendToDevice(string deviceId, string messageType, object payload, int seq = 0)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || session.SslStream == null)
            return false;

        try
        {
            var envelope = new P2pEnvelope
            {
                Type = messageType,
                Seq = seq,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            var json = JsonSerializer.Serialize(new
            {
                type = messageType,
                seq = seq,
                ts = envelope.Ts,
                payload = payload
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await WriteFrameAsync(session.SslStream, json);
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
                _logger.LogDebug("[P2P] 新 TCP 连接: {RemoteEndPoint}",
                    tcpClient.Client.RemoteEndPoint?.ToString());

                // 每个客户端一个独立 Task
                _ = HandleConnectionAsync(tcpClient, ct);
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

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken ct)
    {
        var remoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        SslStream? sslStream = null;

        try
        {
            // 获取证书（确保已加载）
            var certificate = _certService.GetOrCreateCertificate();

            // 创建 TLS 流
            sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                // 不要求客户端证书（儿童端使用自签名证书，通过指纹校验）
                userCertificateValidationCallback: (sender, clientCert, chain, errors) => true);

            // TLS 1.2 / 1.3，不检查证书吊销
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
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
        }
    }

    // ========== 消息循环 ==========

    private async Task MessageLoopAsync(SslStream sslStream, string remoteEndPoint,
        string? peerFingerprint, CancellationToken ct)
    {
        string? deviceId = null;

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(sslStream, ct);
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

            await HandleMessageAsync(sslStream, envelope, ref deviceId, peerFingerprint, remoteEndPoint, ct);
        }

        // 连接断开，清理会话
        if (deviceId != null)
        {
            _sessions.TryRemove(deviceId, out _);
            await _messageHandler.OnDeviceDisconnected(deviceId);
            _logger.LogInformation("[P2P] 设备断开: {DeviceId} ({RemoteEndPoint})", deviceId, remoteEndPoint);
        }
    }

    /// <summary>
    /// 消息分发
    /// </summary>
    private async Task HandleMessageAsync(SslStream sslStream, P2pEnvelope envelope,
        ref string? deviceId, string? peerFingerprint, string remoteEndPoint, CancellationToken ct)
    {
        try
        {
            switch (envelope.Type)
            {
                case P2pMessageType.Handshake:
                    {
                        var req = DeserializePayload<HandshakeRequest>(envelope.Payload);
                        if (req == null) break;

                        var (response, policy, dbDeviceId) = await _messageHandler.HandleHandshake(req, peerFingerprint, remoteEndPoint);

                        if (response.Ok)
                        {
                            deviceId = req.DeviceId;
                            _sessions[deviceId] = new P2pSession
                            {
                                DeviceId = deviceId,
                                SslStream = sslStream,
                                TcpClient = null,
                                ConnectedAt = DateTime.UtcNow,
                                LastHeartbeat = DateTime.UtcNow,
                            };
                        }

                        await WriteEnvelopeAsync(sslStream, P2pMessageType.Handshake, envelope.Seq, response);

                        // 握手成功后立即下发策略
                        if (response.Ok && policy != null && deviceId != null)
                        {
                            await WriteEnvelopeAsync(sslStream, P2pMessageType.PolicyUpdate, 0, policy);
                        }
                        break;
                    }

                case P2pMessageType.UsageReport:
                    {
                        var req = DeserializePayload<UsageReportRequest>(envelope.Payload);
                        if (req == null) break;

                        var ack = await _messageHandler.HandleUsageReport(req);
                        await WriteEnvelopeAsync(sslStream, P2pMessageType.SyncAck, envelope.Seq, ack);
                        break;
                    }

                case P2pMessageType.Heartbeat:
                    {
                        var req = DeserializePayload<HeartbeatMessage>(envelope.Payload);
                        if (req != null)
                        {
                            var ack = await _messageHandler.HandleHeartbeat(req);

                            // 更新会话心跳时间
                            if (deviceId != null && _sessions.TryGetValue(deviceId, out var session))
                            {
                                session.LastHeartbeat = DateTime.UtcNow;
                            }

                            await WriteEnvelopeAsync(sslStream, P2pMessageType.HeartbeatAck, envelope.Seq, ack);
                        }
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
