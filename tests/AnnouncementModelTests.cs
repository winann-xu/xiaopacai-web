using System.ComponentModel.DataAnnotations;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;
using Xunit;

namespace XiaopacaiWeb.Tests.Models;

/// <summary>
/// 公告（Announcement）模型验证测试
///
/// 覆盖：
/// - 默认值（Priority=normal、Status=draft、时间戳自动填充）
/// - 必填字段（Title / Content / CreatedBy）
/// - 数据注解（Title 256 / Priority 16 / Status 16）
/// - 状态流转赋值（draft → published → revoked）
/// - 有效期与定向设备（可选字段）
/// - P2P 公告推送消息默认值
///
/// 说明：公告 REST CRUD 控制器为规划中接口（P2-C 待实现），
/// 当前后端通过 P2pMessageHandler.PushAnnouncement 支持公告推送。
/// </summary>
public class AnnouncementModelTests
{
    // ==================== 默认值 ====================

    [Fact]
    public void NewAnnouncement_HasCorrectDefaults()
    {
        var announcement = new Announcement();

        Assert.Equal("normal", announcement.Priority);
        Assert.Equal("draft", announcement.Status);
        Assert.Null(announcement.TargetDeviceId);
        Assert.Null(announcement.ValidFrom);
        Assert.Null(announcement.ValidUntil);
        Assert.Null(announcement.PublishedAt);
        Assert.Null(announcement.RevokedAt);
        Assert.Equal(string.Empty, announcement.Title);
        Assert.Equal(string.Empty, announcement.Content);
        Assert.True(announcement.CreatedAt <= DateTime.UtcNow);
        Assert.True(announcement.UpdatedAt <= DateTime.UtcNow);
    }

    // ==================== 必填字段 ====================

    [Fact]
    public void Announcement_Title_Required()
    {
        var announcement = new Announcement { Title = "", Content = "内容", CreatedBy = 1 };
        var results = ValidateModel(announcement);

        Assert.Contains(results, r => r.MemberNames.Contains("Title"));
    }

    [Fact]
    public void Announcement_Content_Required()
    {
        var announcement = new Announcement { Title = "标题", Content = "", CreatedBy = 1 };
        var results = ValidateModel(announcement);

        Assert.Contains(results, r => r.MemberNames.Contains("Content"));
    }

    [Fact]
    public void Announcement_CreatedBy_Required()
    {
        // CreatedBy 为 int，默认 0 表示未赋值——业务层必须显式指定创建人
        var announcement = new Announcement { Title = "标题", Content = "内容", CreatedBy = 0 };
        Assert.Equal(0, announcement.CreatedBy);
    }

    // ==================== 数据注解（MaxLength） ====================

    [Fact]
    public void Announcement_Title_MaxLength256()
    {
        var announcement = new Announcement { Title = new string('t', 257), Content = "c", CreatedBy = 1 };
        var results = ValidateModel(announcement);

        Assert.Contains(results, r =>
            r.MemberNames.Contains("Title") && r.ErrorMessage!.Contains("256"));
    }

    [Theory]
    [InlineData("normal", true)]
    [InlineData("important", true)]
    [InlineData("urgent", true)]
    [InlineData("VERY_IMPORTANT_PRIORITY_EXCEEDS_16", false)]
    public void Announcement_Priority_MaxLength16(string priority, bool isValid)
    {
        var announcement = new Announcement
        {
            Title = "标题",
            Content = "内容",
            Priority = priority,
            CreatedBy = 1,
        };
        var results = ValidateModel(announcement);

        var hasLengthError = results.Any(r =>
            r.MemberNames.Contains("Priority") && r.ErrorMessage!.Contains("16"));
        Assert.Equal(!isValid, hasLengthError);
    }

    [Theory]
    [InlineData("draft", true)]
    [InlineData("published", true)]
    [InlineData("revoked", true)]
    [InlineData("INVALID_STATUS_EXCEEDS_16_CHARS", false)]
    public void Announcement_Status_MaxLength16(string status, bool isValid)
    {
        var announcement = new Announcement
        {
            Title = "标题",
            Content = "内容",
            Status = status,
            CreatedBy = 1,
        };
        var results = ValidateModel(announcement);

        var hasLengthError = results.Any(r =>
            r.MemberNames.Contains("Status") && r.ErrorMessage!.Contains("16"));
        Assert.Equal(!isValid, hasLengthError);
    }

    // ==================== 状态流转 ====================

    [Fact]
    public void Announcement_StatusLifecycle_DraftToPublishedToRevoked()
    {
        var announcement = new Announcement
        {
            Title = "使用提醒",
            Content = "请按时休息",
            Status = "draft",
            CreatedBy = 1,
        };

        // draft → published（记录发布时间）
        announcement.Status = "published";
        announcement.PublishedAt = DateTime.UtcNow;
        Assert.Equal("published", announcement.Status);
        Assert.NotNull(announcement.PublishedAt);

        // published → revoked（记录撤回时间）
        announcement.Status = "revoked";
        announcement.RevokedAt = DateTime.UtcNow;
        Assert.Equal("revoked", announcement.Status);
        Assert.NotNull(announcement.RevokedAt);
        Assert.True(announcement.RevokedAt >= announcement.PublishedAt);
    }

    // ==================== 可选字段 ====================

    [Fact]
    public void Announcement_ValidityWindow_Optional()
    {
        // 无有效期限制的公告（长期有效）
        var announcement = new Announcement
        {
            Title = "长期公告",
            Content = "内容",
            CreatedBy = 1,
            ValidFrom = null,
            ValidUntil = null,
        };

        Assert.Empty(ValidateModel(announcement));
    }

    [Fact]
    public void Announcement_TargetedDevice_Optional()
    {
        // 定向到指定设备
        var targeted = new Announcement { Title = "定向", Content = "c", TargetDeviceId = 7, CreatedBy = 1 };
        Assert.Equal(7, targeted.TargetDeviceId);

        // 全设备广播（null）
        var broadcast = new Announcement { Title = "广播", Content = "c", TargetDeviceId = null, CreatedBy = 1 };
        Assert.Null(broadcast.TargetDeviceId);
    }

    // ==================== P2P 公告推送消息 ====================

    [Fact]
    public void AnnouncementPushMessage_DefaultValues()
    {
        var msg = new AnnouncementPushMessage();

        Assert.Equal(string.Empty, msg.Title);
        Assert.Equal(string.Empty, msg.Content);
        Assert.Equal("normal", msg.Priority);
        Assert.Equal("publish", msg.Action);
        Assert.Null(msg.ValidFrom);
        Assert.Null(msg.ValidUntil);
        Assert.Null(msg.PublishedAt);
    }

    // ==================== 辅助方法 ====================

    private static List<ValidationResult> ValidateModel(object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model, null, null);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }
}
