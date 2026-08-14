using System.Text.Json.Serialization;

namespace XiaopacaiWeb.P2P;

// ============================================================================
// P2P 协议帧定义 — 4 字节大端长度前缀 + JSON 帧
// 兼容 2.0 Android 儿童端协议（LEGACY-e 方案）
// ============================================================================

/// <summary>
/// P2P 消息类型枚举
/// </summary>
public static class P2pMessageType
{
    public const string Handshake = "handshake";
    public const string PolicyUpdate = "policy_update";
    public const string UsageReport = "usage_report";
    public const string AnnouncementPush = "announcement_push";
    // [TASK-OPT-12-P4-DEEPEN] 儿童端公告确认回执（确认后中继转发给家长端）
    public const string AnnouncementAck = "announcement_ack";
    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeat_ack";
    public const string SyncAck = "sync_ack";
    // [REQ] 每日限额重置：家长在 Web 端点击“重置当日限额”后下发
    public const string LimitReset = "limit_reset";
}

/// <summary>
/// P2P 消息信封（通用外层）
/// </summary>
public class P2pEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("seq")]
    public int Seq { get; set; } = 0;

    [JsonPropertyName("ts")]
    public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }
}

// ============================================================================
// 1. Handshake — 儿童端 → 服务端（设备注册/认证）
// ============================================================================

/// <summary>
/// 儿童端握手请求
/// </summary>
public class HandshakeRequest
{
    /// <summary>设备唯一标识（Android device ID）</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称（如"小明手机"）</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>平台：android</summary>
    [JsonPropertyName("deviceType")]
    public string Platform { get; set; } = "android";

    /// <summary>客户端版本号</summary>
    [JsonPropertyName("version")]
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>配对码（6 位，首次连接或重新配对时携带）</summary>
    [JsonPropertyName("pairingCode")]
    public string? PairCode { get; set; }

    /// <summary>客户端证书指纹（首次握手传递，用于后续校验）</summary>
    [JsonPropertyName("certFingerprint")]
    public string? CertFingerprint { get; set; }

    /// <summary>客户端时间戳（秒）</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    // [TASK-OPT-12-P4-DEEPEN] 是否通过云端中继连接（true 时服务端写入 relay_sessions 会话记录）
    [JsonPropertyName("relay")]
    public bool Relay { get; set; }
}

/// <summary>
/// 服务端握手响应
/// </summary>
public class HandshakeResponse
{
    /// <summary>是否成功</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>错误信息（ok=false 时）</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>配对状态：unpaired | paired | revoked</summary>
    [JsonPropertyName("pair_status")]
    public string PairStatus { get; set; } = "unpaired";

    /// <summary>会话 ID（用于后续心跳追踪）</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }
}

// ============================================================================
// 2. Policy Update — 服务端 → 儿童端（策略下发）
// ============================================================================

/// <summary>
/// 策略更新（服务端主动下发或在 handshake 后发送）
/// </summary>
public class PolicyUpdateMessage
{
    /// <summary>每日使用限额（分钟）</summary>
    [JsonPropertyName("daily_limit")]
    public int DailyLimit { get; set; } = 120;

    /// <summary>就寝开始时间（HH:mm）</summary>
    [JsonPropertyName("sleep_time_start")]
    public string? SleepTimeStart { get; set; }

    /// <summary>就寝结束时间（HH:mm）</summary>
    [JsonPropertyName("sleep_time_end")]
    public string? SleepTimeEnd { get; set; }

    /// <summary>分类限额（分钟，-1=不限）</summary>
    [JsonPropertyName("category_limit")]
    public CategoryLimit? CategoryLimit { get; set; }

    /// <summary>应用白名单（包名数组）</summary>
    [JsonPropertyName("whitelist")]
    public List<string>? Whitelist { get; set; }

    /// <summary>应用黑名单（包名数组）</summary>
    [JsonPropertyName("blacklist")]
    public List<string>? Blacklist { get; set; }

    /// <summary>超时处理方式：full_lock | partial_lock | warn_only</summary>
    [JsonPropertyName("overtime_action")]
    public string OvertimeAction { get; set; } = "full_lock";

    /// <summary>策略版本号（用于增量同步）</summary>
    [JsonPropertyName("policy_version")]
    public long PolicyVersion { get; set; }
}

/// <summary>
/// 分类限额
/// </summary>
public class CategoryLimit
{
    [JsonPropertyName("game")]
    public int Game { get; set; } = -1;

    [JsonPropertyName("social")]
    public int Social { get; set; } = -1;

    [JsonPropertyName("video")]
    public int Video { get; set; } = -1;

    [JsonPropertyName("learning")]
    public int Learning { get; set; } = -1;
}

// ============================================================================
// 3. Usage Report — 儿童端 → 服务端（使用时长上报）
// ============================================================================

/// <summary>
/// 使用上报请求（儿童端定期发送）
/// </summary>
public class UsageReportRequest
{
    /// <summary>设备 ID</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>上报批次号</summary>
    [JsonPropertyName("batch_id")]
    public string? BatchId { get; set; }

    /// <summary>使用记录列表</summary>
    [JsonPropertyName("records")]
    public List<UsageRecordItem> Records { get; set; } = new();
}

/// <summary>
/// 单条使用记录
/// </summary>
public class UsageRecordItem
{
    /// <summary>应用包名</summary>
    [JsonPropertyName("app_package")]
    public string AppPackage { get; set; } = string.Empty;

    /// <summary>应用显示名</summary>
    [JsonPropertyName("app_name")]
    public string AppName { get; set; } = string.Empty;

    /// <summary>分类：game | social | video | learning | other</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "other";

    /// <summary>开始时间（ISO8601）</summary>
    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty;

    /// <summary>结束时间（ISO8601）</summary>
    [JsonPropertyName("end_time")]
    public string? EndTime { get; set; }

    /// <summary>累计时长（秒）</summary>
    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; set; }

    /// <summary>是否被策略拦截</summary>
    [JsonPropertyName("is_blocked")]
    public bool IsBlocked { get; set; }
}

/// <summary>
/// 同步确认（服务端 → 儿童端）
/// </summary>
public class SyncAckMessage
{
    /// <summary>批次号（与上报一致）</summary>
    [JsonPropertyName("batch_id")]
    public string? BatchId { get; set; }

    /// <summary>已同步记录数</summary>
    [JsonPropertyName("synced")]
    public int Synced { get; set; }

    /// <summary>本日累计使用分钟数</summary>
    [JsonPropertyName("today_total_minutes")]
    public int TodayTotalMinutes { get; set; }

    /// <summary>本日剩余分钟数</summary>
    [JsonPropertyName("today_remaining_minutes")]
    public int TodayRemainingMinutes { get; set; }

    /// <summary>是否触发超时锁定</summary>
    [JsonPropertyName("overtime_locked")]
    public bool OvertimeLocked { get; set; }
}

// ============================================================================
// 4. Announcement Push — 服务端 → 儿童端（公告推送）
// ============================================================================

/// <summary>
/// 公告推送消息
/// </summary>
public class AnnouncementPushMessage
{
    /// <summary>公告 ID</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>标题</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>内容</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>优先级：normal | important | urgent</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "normal";

    /// <summary>动作：publish | revoke</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "publish";

    /// <summary>有效期开始</summary>
    [JsonPropertyName("valid_from")]
    public string? ValidFrom { get; set; }

    /// <summary>有效期结束</summary>
    [JsonPropertyName("valid_until")]
    public string? ValidUntil { get; set; }

    /// <summary>发布时间戳</summary>
    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

// ============================================================================
// 5. Heartbeat — 儿童端 ↔ 服务端（保活）
// ============================================================================

/// <summary>
/// 心跳请求（儿童端定期发送）
/// </summary>
public class HeartbeatMessage
{
    /// <summary>设备 ID</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>客户端时间戳</summary>
    [JsonPropertyName("client_ts")]
    public long ClientTs { get; set; }
}

/// <summary>
/// 心跳响应（服务端 → 儿童端）
/// </summary>
public class HeartbeatAckMessage
{
    /// <summary>服务端时间戳</summary>
    [JsonPropertyName("server_ts")]
    public long ServerTs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>是否有待下发策略</summary>
    [JsonPropertyName("policy_pending")]
    public bool PolicyPending { get; set; }

    /// <summary>是否有待推送公告</summary>
    [JsonPropertyName("announcement_pending")]
    public bool AnnouncementPending { get; set; }
}
