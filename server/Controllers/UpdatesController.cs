using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Security;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// [TASK-APP-UPDATE-V1] 公开更新检查 API — 客户端启动/重连/手动检查入口。
/// 无鉴权（设备侧无 JWT），IP 级限频（RequestRateLimiter），
/// 响应仅含更新所需字段，不泄露任何敏感配置。
/// </summary>
[ApiController]
[Route("api/update")]
public class UpdatesController : ControllerBase
{
    /// <summary>支持的 ABI 白名单（服务端仅接纳这三种 Android 构建目标；admin 上传共用）</summary>
    internal static readonly HashSet<string> SupportedAbis = new(StringComparer.OrdinalIgnoreCase)
    {
        "arm64-v8a", "armeabi-v7a", "x86_64",
    };

    /// <summary>
    /// [TASK-UPDATE-CHANNEL] 支持的分发渠道白名单：
    /// stable=正式签名线（默认）；special=特别版（ColorOS 等限制机型的 testkey 签名专用线）。
    /// 客户端必须携带自身构建渠道，服务端仅在该渠道内查找最新版本，杜绝跨签名下发。
    /// </summary>
    internal static readonly HashSet<string> SupportedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "stable", "special",
    };

    private readonly AppDbContext _db;
    private readonly ILogger<UpdatesController> _logger;

    public UpdatesController(AppDbContext db, ILogger<UpdatesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/update/check?platform=android&abi=arm64-v8a&versionCode=10200&channel=stable
    /// 返回 { hasUpdate, latestVersionCode, latestVersionName, minVersionCode, force,
    ///        url, sha256, sizeBytes, changelog, publishedAt, abiMissing, channel }
    /// force = minVersionCode &gt; 当前 versionCode（强制更新阈值判定，ADR 0017 D1）。
    /// 无当前 ABI 包时 abiMissing=true，客户端提示「暂不支持本设备」（任务书 A1）。
    /// channel 缺省为 stable（旧客户端兼容）；客户端仅会收到本渠道的版本，签名不匹配由端侧兜底拦截。
    /// </summary>
    [HttpGet("check")]
    public async Task<IActionResult> Check(
        [FromQuery] string platform,
        [FromQuery] string abi,
        [FromQuery] int versionCode,
        [FromQuery] string? channel)
    {
        // 限频：120 次/小时/IP（防启动风暴与接口滥用；本机/回环不限）
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!RequestRateLimiter.Allow($"update-check:{clientIp}", 120, 3600))
        {
            return StatusCode(429, new { error = "请求过于频繁，请稍后再试" });
        }

        // 参数校验：本期仅 android（windows 家长端预留，见 ADR 0017 D5）
        if (!string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "platform 暂仅支持 android（windows 预留中）" });
        if (string.IsNullOrWhiteSpace(abi) || !SupportedAbis.Contains(abi))
            return BadRequest(new { error = "abi 必须为 arm64-v8a / armeabi-v7a / x86_64" });
        var channelKey = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim();
        if (!SupportedChannels.Contains(channelKey))
            return BadRequest(new { error = "channel 必须为 stable / special" });
        // 语义约定（docs/app-update-v1.md §1）：versionCode=0 表示「下载中心/无客户端上下文」，
        // 恒返回最新已发布版本（force 按 minVersionCode > 0 计算，页面不使用该字段）；
        // 客户端检查必须传真实 versionCode（>0），防降级判定依赖该值。
        if (versionCode < 0)
            return BadRequest(new { error = "versionCode 非法" });

        // [TASK-UPDATE-CHANNEL] 仅在本渠道内取已发布的最新版本（versionCode 最大，天然防降级）
        var latest = await _db.AppUpdates.AsNoTracking()
            .Where(u => u.Platform == "android" && u.Status == "published" && u.Channel == channelKey)
            .OrderByDescending(u => u.VersionCode)
            .FirstOrDefaultAsync();

        // versionCode=0：下载中心场景，恒返回最新已发布版本信息（无更新时 hasUpdate=false）
        if (latest == null || (versionCode > 0 && latest.VersionCode <= versionCode))
        {
            return Ok(new
            {
                hasUpdate = false,
                latestVersionCode = latest?.VersionCode ?? 0,
                latestVersionName = latest?.VersionName ?? "",
            });
        }

        // 该版本是否包含本设备 ABI 的安装包
        var abiUrls = ParseAbiMap(latest.AbiUrls);
        var abiSha256 = ParseAbiMap(latest.AbiSha256);
        if (!abiUrls.TryGetValue(abi.ToLowerInvariant(), out var url) || string.IsNullOrWhiteSpace(url))
        {
            // 有更新但无本设备 ABI 包：如实提示，不误报可升级
            return Ok(new
            {
                hasUpdate = true,
                latestVersionCode = latest.VersionCode,
                latestVersionName = latest.VersionName,
                minVersionCode = latest.MinVersionCode,
                force = latest.MinVersionCode > versionCode,
                abiMissing = true,
                publishedAt = latest.PublishedAt,
                channel = latest.Channel,
            });
        }

        var sha256 = abiSha256.TryGetValue(abi.ToLowerInvariant(), out var s) ? s : "";
        return Ok(new
        {
            hasUpdate = true,
            latestVersionCode = latest.VersionCode,
            latestVersionName = latest.VersionName,
            minVersionCode = latest.MinVersionCode,
            force = latest.MinVersionCode > versionCode,
            url,
            sha256,
            sizeBytes = latest.SizeBytes,
            changelog = latest.Changelog,
            publishedAt = latest.PublishedAt,
            channel = latest.Channel,
        });
    }

    /// <summary>
    /// 解析 ABI 映射 JSON 列（{ "arm64-v8a": "/downloads/xx.apk" }）；损坏数据返回空表（不抛异常）。
    /// </summary>
    public static Dictionary<string, string> ParseAbiMap(string json)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}");
            return map ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
