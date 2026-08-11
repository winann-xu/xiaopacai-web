using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.DTOs;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 设备 REST API — 应用分类管理（OPT12 需求 1）
///
/// GET/PUT /api/devices/{id}/app-categories — 查看/修改设备应用分类
/// TODO(P4)：完整设备 CRUD（列表/详情/状态管理）在后续阶段补齐，此处仅实现应用分类端点。
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize(Policy = "ParentOrAdmin")]
public class DevicesController : ControllerBase
{
    // 分类口径统一：game/social/video/learning/other（与 P2P 协议、儿童端一致）
    private static readonly HashSet<string> ValidCategories =
        new() { "game", "social", "video", "learning", "other" };

    private readonly AppDbContext _db;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(AppDbContext db, ILogger<DevicesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/devices/{id}/app-categories — 查看设备应用分类列表
    /// </summary>
    [HttpGet("{id:int}/app-categories")]
    public async Task<IActionResult> GetAppCategories(int id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        var categories = DeserializeCategories(device.AppCategories);

        return Ok(new
        {
            deviceId = device.DeviceId,
            categories,
        });
    }

    /// <summary>
    /// PUT /api/devices/{id}/app-categories — 保存设备应用分类（全量覆盖）
    /// 保存后由策略下发链路推送到儿童端；TODO(P4)：触发 policy_push 携带 app_categories。
    /// </summary>
    [HttpPut("{id:int}/app-categories")]
    public async Task<IActionResult> PutAppCategories(int id, [FromBody] AppCategoriesRequest request)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null)
            return NotFound(new { error = "设备不存在" });

        // 校验分类值合法性
        var invalid = request.Categories
            .Where(c => !ValidCategories.Contains(c.Category.ToLowerInvariant()))
            .Select(c => c.PackageName)
            .ToList();

        if (invalid.Count > 0)
            return BadRequest(new { error = $"非法分类值: {string.Join(", ", invalid)}" });

        // 归一化后落库（JSON 数组）
        var normalized = request.Categories
            .Select(c => new AppCategoryItem
            {
                PackageName = c.PackageName,
                AppName = c.AppName ?? string.Empty,
                Category = c.Category.ToLowerInvariant(),
            })
            .ToList();

        device.AppCategories = JsonSerializer.Serialize(normalized);
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Devices] 设备 {DeviceId} 应用分类已保存 {Count} 条",
            device.DeviceId, normalized.Count);

        return Ok(new
        {
            deviceId = device.DeviceId,
            categories = normalized,
            message = "应用分类已保存",
        });
    }

    // ========== 辅助 ==========

    /// <summary>
    /// 反序列化应用分类 JSON（容错：损坏数据返回空列表）
    /// </summary>
    private static List<AppCategoryItem> DeserializeCategories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<AppCategoryItem>();
        try
        {
            return JsonSerializer.Deserialize<List<AppCategoryItem>>(json) ?? new List<AppCategoryItem>();
        }
        catch (JsonException)
        {
            return new List<AppCategoryItem>();
        }
    }
}
