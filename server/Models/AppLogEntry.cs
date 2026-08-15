using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-MILESTONE-V3] 需求 14：客户端上传的运行日志（账号级归属，保留最近 7 天）
///
/// - 账号级归属：家长端 App 上传的本机运行日志绑定上传者账号；
///   普通家长仅可见本账号日志，admin 可见全部（列表接口按角色过滤）；
/// - 双时间：CreatedAt 为客户端日志产生时间（展示用，服务端钳制），
///   ReceivedAt 为服务端接收时间（保留 7 天清理与排序依据，防客户端时间伪造）；
/// - 内容在客户端写入时已打码，服务端入库前二次打码（纵深防御）。
/// </summary>
public class AppLogEntry
{
    public long Id { get; set; }

    /// <summary>上传者账号 id</summary>
    public int AccountId { get; set; }

    /// <summary>级别：debug | info | warn | error</summary>
    [MaxLength(8)]
    public string Level { get; set; } = "info";

    /// <summary>模块 tag</summary>
    [MaxLength(64)]
    public string Tag { get; set; } = string.Empty;

    /// <summary>日志内容（已脱敏 + 截断）</summary>
    [MaxLength(1000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>客户端标识（机型/系统版本），区分同一账号多台设备</summary>
    [MaxLength(64)]
    public string? Client { get; set; }

    /// <summary>客户端日志产生时间（展示用）</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>服务端接收时间（保留策略/排序依据）</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
