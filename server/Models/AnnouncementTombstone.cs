using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XiaopacaiWeb.Models;

/// <summary>
/// [TASK-MILESTONE-V3] B5 公告删除墓碑：删除公告时落一行，客户端据此清除本地记录（多端一致）。
/// - 实时路径：删除时向发布者账号下在线设备推送 announcement_clear；
/// - 离线路径：设备重连同步时下发 7 天内的墓碑 id 列表，客户端清除本地残留；
/// - 保留 7 天（与需求 14 日志口径一致），到期由同步/补偿服务顺带清理。
/// </summary>
[Table("announcement_tombstones")]
public class AnnouncementTombstone
{
    [Key]
    public int AnnouncementId { get; set; }

    /// <summary>公告创建者（账号归属，用于同步时按账号过滤）</summary>
    [Required]
    public int CreatedBy { get; set; }

    [Required]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
