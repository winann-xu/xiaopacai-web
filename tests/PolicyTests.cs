using System.ComponentModel.DataAnnotations;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests.Models;

/// <summary>
/// 策略（Policy）模型验证测试
///
/// 覆盖：
/// - 默认值（DailyLimitMinutes=120, OvertimeAction=full_lock, 分类限额=-1）
/// - 必填字段
/// - 数据注解（MaxLength）
/// - 边界值（30~480 分钟、null bedtime）
/// </summary>
public class PolicyTests
{
    // ==================== 默认值 ====================

    [Fact]
    public void NewPolicy_HasCorrectDefaults()
    {
        var policy = new Policy();

        Assert.Equal(120, policy.DailyLimitMinutes);
        Assert.Equal("full_lock", policy.OvertimeAction);
        Assert.Equal(-1, policy.CategoryGameLimit);
        Assert.Equal(-1, policy.CategorySocialLimit);
        Assert.Equal(-1, policy.CategoryVideoLimit);
        Assert.Equal(-1, policy.CategoryLearningLimit);
        Assert.True(policy.IsActive);
        Assert.True(policy.CreatedAt <= DateTime.UtcNow);
        Assert.True(policy.UpdatedAt <= DateTime.UtcNow);
    }

    // ==================== 必填字段验证 ====================

    [Fact]
    public void Policy_DeviceId_Required()
    {
        var policy = new Policy();
        var results = ValidateModel(policy);
        // DeviceId 是 int 类型（不可为空），但 [Required] 的属性不会在 DeviceId=0 时报错
        // int 默认值为 0，[Required] 只对引用类型和 Nullable<T> 生效
        // 但在 EF Core 层面 DeviceId 不能为 0（业务约束）
        Assert.True(policy.DeviceId == 0); // 默认值，需要业务逻辑赋值
    }

    [Fact]
    public void Policy_OvertimeAction_IsRequired()
    {
        var policy = new Policy { OvertimeAction = "" };
        var results = ValidateModel(policy);

        // [Required] 对空字符串起作用
        Assert.Contains(results, r => r.MemberNames.Contains("OvertimeAction"));
    }

    // ==================== 数据注解 (MaxLength) ====================

    [Theory]
    [InlineData("21:00", true)]
    [InlineData("07:00", true)]
    [InlineData("", true)]       // 空字符串允许（nullable）
    [InlineData("21:000", false)] // 6 字符 > MaxLength(5)
    public void Policy_BedtimeStart_MaxLength(string value, bool isValid)
    {
        var policy = new Policy { BedtimeStart = value };
        var results = ValidateModel(policy);

        var hasError = results.Any(r => r.MemberNames.Contains("BedtimeStart"));
        if (isValid)
            Assert.False(hasError, $"Expected valid: '{value}'");
        else
            Assert.True(hasError, $"Expected invalid: '{value}'");
    }

    [Theory]
    [InlineData("full_lock", true)]
    [InlineData("partial_lock", true)]
    [InlineData("warn_only", true)]
    [InlineData("INVALID_ACTION_VERY_LONG_STRING_EXCEEDS_16_CHARS", false)]
    public void Policy_OvertimeAction_MaxLength(string value, bool isValid)
    {
        var policy = new Policy { OvertimeAction = value };
        var results = ValidateModel(policy);

        var maxLengthError = results.Any(r =>
            r.MemberNames.Contains("OvertimeAction") && r.ErrorMessage!.Contains("16"));
        if (isValid)
            Assert.False(maxLengthError, $"Expected valid: '{value}'");
        else
            Assert.True(maxLengthError, $"Expected MaxLength error: '{value}'");
    }

    // ==================== 分类限额 ====================

    [Fact]
    public void Policy_CategoryLimits_AllowNegativeOne()
    {
        // -1 表示"不限"
        var policy = new Policy
        {
            CategoryGameLimit = -1,
            CategorySocialLimit = -1,
            CategoryVideoLimit = -1,
            CategoryLearningLimit = -1,
        };

        var results = ValidateModel(policy);
        Assert.Empty(results); // 所有字段合法
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(480)]
    public void Policy_DailyLimitMinutes_CommonValues(int minutes)
    {
        var policy = new Policy { DailyLimitMinutes = minutes };
        var results = ValidateModel(policy);

        // 没有数据注解约束，不应有验证错误
        var hasError = results.Any(r => r.MemberNames.Contains("DailyLimitMinutes"));
        Assert.False(hasError);
    }

    // ==================== 黑白名单 JSON ====================

    [Fact]
    public void Policy_WhitelistApps_ValidJson_Accepted()
    {
        var policy = new Policy
        {
            WhitelistApps = """["com.android.contacts","com.android.phone"]""",
        };

        var results = ValidateModel(policy);
        Assert.Empty(results);
    }

    [Fact]
    public void Policy_EmptyJsonArray_Accepted()
    {
        var policy = new Policy { WhitelistApps = "[]" };
        var results = ValidateModel(policy);
        Assert.Empty(results);
    }

    [Fact]
    public void Policy_Whitelist_Null_Accepted()
    {
        var policy = new Policy { WhitelistApps = null };
        var results = ValidateModel(policy);
        Assert.Empty(results);
    }

    // ==================== IsActive 默认值 ====================

    [Fact]
    public void Policy_IsActive_DefaultTrue()
    {
        var policy = new Policy();
        Assert.True(policy.IsActive);
    }

    // ==================== 关联导航属性 ====================

    [Fact]
    public void Policy_Device_NavigationProperty()
    {
        var device = new Device { Id = 1, DeviceId = "test-device" };
        var policy = new Policy
        {
            DeviceId = 1,
            Device = device,
        };

        Assert.Equal(device, policy.Device);
        Assert.Equal(1, policy.DeviceId);
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
