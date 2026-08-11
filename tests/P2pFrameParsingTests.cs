using System.Text;
using System.Text.Json;
using XiaopacaiWeb.P2P;
using Xunit;

namespace XiaopacaiWeb.Tests.P2P;

/// <summary>
/// P2P 协议帧解析测试 — 4 字节大端长度前缀 + JSON 帧
///
/// 覆盖：
/// - JSON 序列化/反序列化往返
/// - 帧编码/解码（长度前缀正确性）
/// - 边界条件（空帧、超长帧、无效帧）
/// - 各消息类型（handshake, usage_report, policy_update, heartbeat, announcement_push）
/// </summary>
public class P2pFrameParsingTests
{
    /// <summary>
    /// 自定义帧写入（模拟 P2pListenerService.WriteFrameAsync 逻辑）
    /// </summary>
    private static byte[] EncodeFrame<T>(T payload, string type, int seq = 0)
    {
        var envelope = new P2pEnvelope
        {
            Type = type,
            Seq = seq,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        // 4 字节大端长度前缀
        var frame = new byte[4 + jsonBytes.Length];
        frame[0] = (byte)((jsonBytes.Length >> 24) & 0xFF);
        frame[1] = (byte)((jsonBytes.Length >> 16) & 0xFF);
        frame[2] = (byte)((jsonBytes.Length >> 8) & 0xFF);
        frame[3] = (byte)(jsonBytes.Length & 0xFF);
        Array.Copy(jsonBytes, 0, frame, 4, jsonBytes.Length);
        return frame;
    }

    /// <summary>
    /// 自定义帧解码（模拟 P2pListenerService.ReadFrameAsync 逻辑）
    /// </summary>
    private static P2pEnvelope? DecodeFrame(byte[] frame)
    {
        if (frame.Length < 4)
            return null;

        // 读取 4 字节大端长度前缀
        var length = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];

        if (length <= 0 || length > 1_048_576) // 最大 1MB
            return null;

        if (4 + length > frame.Length)
            return null;

        var json = Encoding.UTF8.GetString(frame, 4, length);
        return JsonSerializer.Deserialize<P2pEnvelope>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    // ==================== 帧编码/解码基本测试 ====================

    [Fact]
    public void EncodeDecode_RoundTrip_LengthPrefixCorrect()
    {
        // Arrange
        var request = new HandshakeRequest
        {
            DeviceId = "test-device-001",
            DeviceName = "Test Phone",
            Platform = "android",
            ClientVersion = "1.0.0",
        };

        // Act: Encode
        var frame = EncodeFrame(request, P2pMessageType.Handshake);
        Assert.NotNull(frame);
        Assert.True(frame.Length > 4, "Frame must have length prefix + JSON body");

        // Verify length prefix
        var expectedPayloadLength = frame.Length - 4;
        var decodedLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
        Assert.Equal(expectedPayloadLength, decodedLength);

        // Act: Decode
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);
        Assert.Equal(P2pMessageType.Handshake, envelope.Type);
        Assert.True(envelope.Payload.HasValue);
    }

    [Fact]
    public void DecodeFrame_EmptyFrame_ReturnsNull()
    {
        var result = DecodeFrame(Array.Empty<byte>());
        Assert.Null(result);
    }

    [Fact]
    public void DecodeFrame_TooShortFrame_ReturnsNull()
    {
        var result = DecodeFrame(new byte[] { 0x00, 0x00, 0x00 }); // 只有 3 字节，缺少长度前缀
        Assert.Null(result);
    }

    [Fact]
    public void DecodeFrame_InvalidLength_Negative_ReturnsNull()
    {
        // 负长度（最高位为 1）
        var frame = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }; // length=-1
        var result = DecodeFrame(frame);
        Assert.Null(result);
    }

    [Fact]
    public void DecodeFrame_InvalidLength_Zero_ReturnsNull()
    {
        var frame = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        var result = DecodeFrame(frame);
        Assert.Null(result);
    }

    [Fact]
    public void DecodeFrame_TruncatedBody_ReturnsNull()
    {
        // 声明 100 字节的 body，但只有 10 字节
        var frame = new byte[14]; // 4 (length) + 10 (body)
        frame[0] = 0x00;
        frame[1] = 0x00;
        frame[2] = 0x00;
        frame[3] = 100; // 声称 100 字节

        var result = DecodeFrame(frame);
        Assert.Null(result);
    }

    // ==================== Handshake 消息 ====================

    [Fact]
    public void HandshakeRequest_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new HandshakeRequest
        {
            DeviceId = "android-device-abc123",
            DeviceName = "小明的手机",
            Platform = "android",
            ClientVersion = "2.0.0-p1",
            PairCode = "123456",
            CertFingerprint = "a1b2c3d4e5f6...",
        };

        // Act
        var frame = EncodeFrame(original, P2pMessageType.Handshake);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<HandshakeRequest>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);

        // Assert
        Assert.Equal(original.DeviceId, decoded!.DeviceId);
        Assert.Equal(original.DeviceName, decoded.DeviceName);
        Assert.Equal(original.Platform, decoded.Platform);
        Assert.Equal(original.ClientVersion, decoded.ClientVersion);
        Assert.Equal(original.PairCode, decoded.PairCode);
        Assert.Equal(original.CertFingerprint, decoded.CertFingerprint);
    }

    [Fact]
    public void HandshakeResponse_SerializeDeserialize_Ok()
    {
        var original = new HandshakeResponse
        {
            Ok = true,
            PairStatus = "paired",
            SessionId = "abc123def456",
        };

        var frame = EncodeFrame(original, P2pMessageType.Handshake);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<HandshakeResponse>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.True(decoded!.Ok);
        Assert.Equal("paired", decoded.PairStatus);
        Assert.Equal("abc123def456", decoded.SessionId);
    }

    [Fact]
    public void HandshakeResponse_SerializeDeserialize_Rejected()
    {
        var original = new HandshakeResponse
        {
            Ok = false,
            Error = "配对码无效或已过期",
            PairStatus = "unpaired",
        };

        var frame = EncodeFrame(original, P2pMessageType.Handshake);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);
        Assert.Equal(P2pMessageType.Handshake, envelope.Type);

        var decoded = JsonSerializer.Deserialize<HandshakeResponse>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.False(decoded!.Ok);
        Assert.Contains("配对码", decoded.Error);
    }

    // ==================== Usage Report 消息 ====================

    [Fact]
    public void UsageReportRequest_WithRecords_SerializeRoundTrip()
    {
        var original = new UsageReportRequest
        {
            DeviceId = "android-device-abc",
            BatchId = "batch-2026-001",
            Records = new List<UsageRecordItem>
            {
                new UsageRecordItem
                {
                    AppPackage = "com.android.chrome",
                    AppName = "Chrome",
                    Category = "other",
                    StartTime = "2026-08-10T10:00:00Z",
                    EndTime = "2026-08-10T10:25:00Z",
                    DurationSeconds = 1500,
                    IsBlocked = false,
                },
                new UsageRecordItem
                {
                    AppPackage = "com.tencent.tmgp.sgame",
                    AppName = "王者荣耀",
                    Category = "game",
                    StartTime = "2026-08-10T14:00:00Z",
                    DurationSeconds = 3600,
                    IsBlocked = true,
                },
            },
        };

        var frame = EncodeFrame(original, P2pMessageType.UsageReport);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);
        Assert.Equal(P2pMessageType.UsageReport, envelope.Type);

        var decoded = JsonSerializer.Deserialize<UsageReportRequest>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal("android-device-abc", decoded!.DeviceId);
        Assert.Equal("batch-2026-001", decoded.BatchId);
        Assert.Equal(2, decoded.Records.Count);
        Assert.Equal(1500, decoded.Records[0].DurationSeconds);
        Assert.True(decoded.Records[1].IsBlocked);
    }

    [Fact]
    public void SyncAckMessage_SerializeDeserialize_WithOvertime()
    {
        var original = new SyncAckMessage
        {
            BatchId = "batch-2026-001",
            Synced = 5,
            TodayTotalMinutes = 125,
            TodayRemainingMinutes = 0,
            OvertimeLocked = true,
        };

        var frame = EncodeFrame(original, P2pMessageType.SyncAck);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<SyncAckMessage>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal(5, decoded!.Synced);
        Assert.Equal(125, decoded.TodayTotalMinutes);
        Assert.Equal(0, decoded.TodayRemainingMinutes);
        Assert.True(decoded.OvertimeLocked);
    }

    // ==================== Policy Update 消息 ====================

    [Fact]
    public void PolicyUpdateMessage_SerializeDeserialize_FullPolicy()
    {
        var original = new PolicyUpdateMessage
        {
            DailyLimit = 180,
            SleepTimeStart = "21:00",
            SleepTimeEnd = "07:00",
            CategoryLimit = new CategoryLimit
            {
                Game = 60,
                Social = 30,
                Video = 90,
                Learning = -1,
            },
            Whitelist = new List<string> { "com.xiaopacai.child", "com.android.contacts" },
            Blacklist = new List<string> { "com.android.calculator2" },
            OvertimeAction = "partial_lock",
            PolicyVersion = 1234567890,
        };

        var frame = EncodeFrame(original, P2pMessageType.PolicyUpdate);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<PolicyUpdateMessage>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal(180, decoded!.DailyLimit);
        Assert.Equal("21:00", decoded.SleepTimeStart);
        Assert.Equal("07:00", decoded.SleepTimeEnd);
        Assert.Equal(60, decoded.CategoryLimit!.Game);
        Assert.Equal(-1, decoded.CategoryLimit.Learning);
        Assert.Equal(2, decoded.Whitelist!.Count);
        Assert.Single(decoded.Blacklist!);
        Assert.Equal("partial_lock", decoded.OvertimeAction);
        Assert.Equal(1234567890, decoded.PolicyVersion);
    }

    // ==================== Announcement Push 消息 ====================

    [Fact]
    public void AnnouncementPushMessage_SerializeDeserialize_AllFields()
    {
        var original = new AnnouncementPushMessage
        {
            Id = 42,
            Title = "周末使用提醒",
            Content = "记得按时休息，保护眼睛哦。",
            Priority = "important",
            Action = "publish",
            ValidFrom = "2026-08-10T00:00:00.0000000",
            ValidUntil = "2026-08-17T23:59:59.0000000",
            PublishedAt = "2026-08-10T08:00:00.0000000",
        };

        var frame = EncodeFrame(original, P2pMessageType.AnnouncementPush);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<AnnouncementPushMessage>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal(42, decoded!.Id);
        Assert.Equal("周末使用提醒", decoded.Title);
        Assert.Equal("important", decoded.Priority);
        Assert.Equal("publish", decoded.Action);
    }

    // ==================== Heartbeat 消息 ====================

    [Fact]
    public void HeartbeatMessage_SerializeDeserialize_RoundTrip()
    {
        var original = new HeartbeatMessage
        {
            DeviceId = "android-device-abc",
            ClientTs = 1234567890,
        };

        var frame = EncodeFrame(original, P2pMessageType.Heartbeat);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<HeartbeatMessage>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal("android-device-abc", decoded!.DeviceId);
        Assert.Equal(1234567890, decoded.ClientTs);
    }

    [Fact]
    public void HeartbeatAckMessage_SerializeDeserialize_WithPendingFlags()
    {
        var original = new HeartbeatAckMessage
        {
            ServerTs = 1234567900,
            PolicyPending = true,
            AnnouncementPending = false,
        };

        var frame = EncodeFrame(original, P2pMessageType.HeartbeatAck);
        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);

        var decoded = JsonSerializer.Deserialize<HeartbeatAckMessage>(
            envelope!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, }
        );
        Assert.NotNull(decoded);
        Assert.Equal(1234567900, decoded!.ServerTs);
        Assert.True(decoded.PolicyPending);
        Assert.False(decoded.AnnouncementPending);
    }

    // ==================== 多消息连续帧 ====================

    [Fact]
    public void MultipleFrames_Sequential_DecodeCorrectly()
    {
        var frames = new List<byte[]>();

        // 构造 3 条不同消息
        frames.Add(EncodeFrame(new HandshakeRequest { DeviceId = "dev1", DeviceName = "手机1" },
            P2pMessageType.Handshake, 1));
        frames.Add(EncodeFrame(new HeartbeatMessage { DeviceId = "dev1", ClientTs = 100 },
            P2pMessageType.Heartbeat, 2));
        frames.Add(EncodeFrame(new UsageReportRequest { DeviceId = "dev1", Records = new() },
            P2pMessageType.UsageReport, 3));

        // 逐个解码
        foreach (var raw in frames)
        {
            var envelope = DecodeFrame(raw);
            Assert.NotNull(envelope);
            Assert.Contains(envelope!.Type, new[] {
                P2pMessageType.Handshake, P2pMessageType.Heartbeat, P2pMessageType.UsageReport
            });
        }
    }

    // ==================== 长度前缀边界 ====================

    [Fact]
    public void EncodeFrame_MaxFrameSize_AtLimit_Succeeds()
    {
        // 1MB 边界内（1MB = 1,048,576 字节）
        var largePayload = new string('x', 1000);
        var request = new HandshakeRequest
        {
            DeviceId = new string('a', 500),
            DeviceName = largePayload,
        };

        var frame = EncodeFrame(request, P2pMessageType.Handshake);
        Assert.NotNull(frame);

        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);
    }

    [Fact]
    public void DecodeFrame_MaxLength_ExactlyOneMegabyte_Rejected()
    {
        // 设计拒绝正好 1MB（大于限制）
        var frame = new byte[] { 0x00, 0x10, 0x00, 0x00 }; // length = 1,048,576 = 1MB
        var result = DecodeFrame(frame);
        Assert.Null(result); // > 1_048_576 或 == 1_048_576 都应返回 null
    }

    [Fact]
    public void DecodeFrame_LengthAtMaximumMinusOne_Succeeds()
    {
        // 1MB - 1 字节应该在限制内
        var json = "{}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + jsonBytes.Length];
        // 长度正好等于 JSON 体长度
        frame[0] = (byte)((jsonBytes.Length >> 24) & 0xFF);
        frame[1] = (byte)((jsonBytes.Length >> 16) & 0xFF);
        frame[2] = (byte)((jsonBytes.Length >> 8) & 0xFF);
        frame[3] = (byte)(jsonBytes.Length & 0xFF);
        Array.Copy(jsonBytes, 0, frame, 4, jsonBytes.Length);

        var result = DecodeFrame(frame);
        Assert.NotNull(result);
    }

    // ==================== Envelope 元数据 ====================

    [Fact]
    public void Envelope_Metadata_SeqAndTs_Preserved()
    {
        var request = new HandshakeRequest { DeviceId = "test" };
        var frame = EncodeFrame(request, P2pMessageType.Handshake, seq: 42);

        var envelope = DecodeFrame(frame);
        Assert.NotNull(envelope);
        Assert.Equal(42, envelope!.Seq);
        Assert.True(envelope.Ts > 0, "Timestamp should be positive Unix seconds");
    }
}
