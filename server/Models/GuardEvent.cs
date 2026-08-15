using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-HARDENING-V1.1.1] Bug1-D/1-B：儿童端守护失守事件 + 健康度快照
///
/// - 家长端 App 通过 POST /api/guard-events 批量上传（账号级归属：按设备归属校验）；
/// - 事件类型：guard_down（守护失效）/ guard_restored（守护恢复）/ health_snapshot（健康度快照）；
/// - StartedAt/EndedAt/DurationSeconds 为客户端 epoch 秒（可选，展示用）；
/// - HealthJson 存 JSON 字符串（响应时解析为对象返回）；
/// - ReceivedAt 服务端接收时间（UTC，排序与"最近一条"依据，防客户端时间伪造）。
/// </summary>
public class GuardEvent
{
    public long Id { get; set; }

    /// <summary>儿童端设备 deviceId（对应 devices.DeviceId）</summary>
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>事件类型：guard_down | guard_restored | health_snapshot</summary>
    [Required]
    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>事件开始时间（epoch 秒，可选）</summary>
    public long? StartedAt { get; set; }

    /// <summary>事件结束时间（epoch 秒，可选）</summary>
    public long? EndedAt { get; set; }

    /// <summary>失守时长（秒，可选）</summary>
    public long? DurationSeconds { get; set; }

    /// <summary>失守原因：process_killed | swipe_killed | accessibility_disabled | ...</summary>
    [MaxLength(128)]
    public string? Reason { get; set; }

    /// <summary>恢复方式：auto_recovered | swipe_recovery | accessibility_reenabled | ...</summary>
    [MaxLength(128)]
    public string? RestoredReason { get; set; }

    /// <summary>事件发生时守护是否仍处于强制拦截状态</summary>
    public bool WasEnforcing { get; set; }

    /// <summary>健康度快照 JSON（可选，字符串存储，响应时解析为对象）</summary>
    public string? HealthJson { get; set; }

    /// <summary>服务端接收时间（UTC）</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
