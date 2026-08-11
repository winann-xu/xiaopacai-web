using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

/// <summary>
/// 应用分类条目（OPT12 需求 1）
/// 分类口径：game | social | video | learning | other（所有端统一）
/// </summary>
public class AppCategoryItem
{
    [Required]
    [MaxLength(256)]
    public string PackageName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string AppName { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Category { get; set; } = "other";
}

/// <summary>
/// 保存应用分类请求（全量覆盖）
/// </summary>
public class AppCategoriesRequest
{
    public List<AppCategoryItem> Categories { get; set; } = new();
}
