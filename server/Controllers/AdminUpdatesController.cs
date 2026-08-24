using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.P2P;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// [TASK-APP-UPDATE-V1] 更新清单管理（仅管理员）：
/// 创建草稿 → 按 ABI 上传 APK（服务端算 SHA-256）→ 发布并广播 update_available。
/// 安全红线（ADR 0017）：versionCode 单调递增防降级；发布写审计；仅 AdminOnly。
/// </summary>
[ApiController]
[Route("api/admin/updates")]
[Authorize(Policy = "AdminOnly")]
public class AdminUpdatesController : ControllerBase
{
    /// <summary>上传体上限 150MB（单 APK ~25MB × 三 ABI 有余量）</summary>
    private const long MaxUploadBytes = 150L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly P2pMessageHandler _messageHandler;
    private readonly P2pListenerService _p2p;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminUpdatesController> _logger;

    public AdminUpdatesController(
        AppDbContext db,
        P2pMessageHandler messageHandler,
        P2pListenerService p2p,
        IWebHostEnvironment env,
        ILogger<AdminUpdatesController> logger)
    {
        _db = db;
        _messageHandler = messageHandler;
        _p2p = p2p;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/admin/updates — 清单列表（新→旧，含草稿与各 ABI 的 sha256，admin 查看用）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await _db.AppUpdates.AsNoTracking()
            .OrderByDescending(u => u.VersionCode)
            .Select(u => new
            {
                u.Id,
                u.Platform,
                u.VersionName,
                u.VersionCode,
                u.MinVersionCode,
                u.SizeBytes,
                u.Changelog,
                u.Status,
                u.Channel,
                u.PublishedAt,
                u.CreatedBy,
                u.CreatedAt,
                abiUrls = UpdatesController.ParseAbiMap(u.AbiUrls),
                abiSha256 = UpdatesController.ParseAbiMap(u.AbiSha256),
            })
            .ToListAsync();
        return Ok(items);
    }

    /// <summary>
    /// POST /api/admin/updates — 创建草稿清单
    /// [TASK-UPDATE-CHANNEL] 防降级按渠道内比较：versionCode 必须大于该渠道既有最大
    /// versionCode（单调递增，ADR 0017 安全红线 3）；stable/special 相互独立。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpdateSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VersionName))
            return BadRequest(new { error = "版本名不能为空" });
        if (request.VersionCode <= 0)
            return BadRequest(new { error = "versionCode 非法" });
        if (request.MinVersionCode < 0)
            return BadRequest(new { error = "minVersionCode 非法" });

        var channelKey = string.IsNullOrWhiteSpace(request.Channel) ? "stable" : request.Channel.Trim();
        if (!UpdatesController.SupportedChannels.Contains(channelKey))
            return BadRequest(new { error = "channel 必须为 stable / special" });

        var maxExisting = await _db.AppUpdates
            .Where(u => u.Channel == channelKey)
            .MaxAsync(u => (int?)u.VersionCode) ?? 0;
        if (request.VersionCode <= maxExisting)
        {
            return BadRequest(new { error = $"禁止降级：渠道 {channelKey} 内 versionCode 必须大于现有最大版本 {maxExisting}" });
        }

        var item = new AppUpdate
        {
            Platform = "android",
            VersionName = request.VersionName.Trim(),
            VersionCode = request.VersionCode,
            MinVersionCode = request.MinVersionCode == 0 ? request.VersionCode : request.MinVersionCode,
            Changelog = request.Changelog ?? "",
            Status = "draft",
            Channel = channelKey,
            CreatedBy = GetUserId() ?? 0,
        };
        _db.AppUpdates.Add(item);
        await _db.SaveChangesAsync();

        await AuditAsync("update.create", item.Id,
            $"{{\"versionName\":\"{item.VersionName}\",\"versionCode\":{item.VersionCode},\"minVersionCode\":{item.MinVersionCode},\"channel\":\"{item.Channel}\"}}");
        return Ok(new { item.Id });
    }

    /// <summary>
    /// POST /api/admin/updates/{id}/upload（multipart: abi + file）
    /// 上传 APK 到 wwwroot/downloads/XiaopacaiParent-{versionName}-{abi}.apk，
    /// 服务端计算 SHA-256 入库（任务书 A4）。仅草稿状态可上传。
    /// </summary>
    [HttpPost("{id:int}/upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(int id, [FromForm] string abi, IFormFile file)
    {
        var item = await _db.AppUpdates.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "更新清单不存在" });
        if (item.Status != "draft")
            return BadRequest(new { error = "仅草稿状态可上传 APK" });

        if (!UpdatesController.SupportedAbis.Contains(abi.ToLowerInvariant()))
            return BadRequest(new { error = "abi 必须为 arm64-v8a / armeabi-v7a / x86_64" });
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "未收到文件" });
        if (!Path.GetExtension(file.FileName).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "仅允许 .apk 文件" });

        var fileName = $"XiaopacaiParent-{item.VersionName}-{abi.ToLowerInvariant()}.apk";
        var downloadsDir = Path.Combine(_env.WebRootPath ?? "wwwroot", "downloads");
        Directory.CreateDirectory(downloadsDir);
        var savePath = Path.Combine(downloadsDir, fileName);

        string sha256;
        await using (var stream = file.OpenReadStream())
        {
            // 边写盘边计算 SHA-256（大文件不占额外内存）
            using var sha = SHA256.Create();
            await using var fs = System.IO.File.Create(savePath);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await fs.WriteAsync(buffer.AsMemory(0, read));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256 = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        var abiUrls = UpdatesController.ParseAbiMap(item.AbiUrls);
        var abiSha256 = UpdatesController.ParseAbiMap(item.AbiSha256);
        abiUrls[abi.ToLowerInvariant()] = $"/downloads/{fileName}";
        abiSha256[abi.ToLowerInvariant()] = sha256;
        item.AbiUrls = JsonSerializer.Serialize(abiUrls);
        item.AbiSha256 = JsonSerializer.Serialize(abiSha256);
        item.SizeBytes = new FileInfo(savePath).Length;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AuditAsync("update.upload", item.Id,
            $"{{\"abi\":\"{abi}\",\"file\":\"{fileName}\",\"sha256\":\"{sha256}\",\"sizeBytes\":{item.SizeBytes}}}");
        _logger.LogInformation("[Update] APK 已上传 {File} sha256={Sha}", fileName, sha256);
        return Ok(new { fileName, sha256, sizeBytes = item.SizeBytes });
    }

    /// <summary>
    /// POST /api/admin/updates/{id}/publish — 发布并广播 update_available 到全部在线设备（D2）。
    /// 校验至少一个 ABI 可用；离线设备由启动/重连检查补上。
    /// </summary>
    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id)
    {
        var item = await _db.AppUpdates.FindAsync(id);
        if (item == null)
            return NotFound(new { error = "更新清单不存在" });
        if (item.Status == "published")
            return BadRequest(new { error = "该版本已发布" });

        var abiUrls = UpdatesController.ParseAbiMap(item.AbiUrls);
        if (abiUrls.Count == 0)
            return BadRequest(new { error = "至少上传一个 ABI 的 APK 才能发布" });

        item.Status = "published";
        item.PublishedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // 广播（推送仅触发信号 + 摘要，客户端随后调 /api/update/check 拉全量）
        var pushed = await _messageHandler.PushUpdateAvailable(item, _p2p);

        await AuditAsync("update.publish", item.Id,
            $"{{\"versionName\":\"{item.VersionName}\",\"versionCode\":{item.VersionCode},\"minVersionCode\":{item.MinVersionCode},\"pushedOnline\":{pushed}}}");
        _logger.LogInformation("[Update] v{Version} 已发布，广播 {Pushed} 台在线设备", item.VersionName, pushed);
        return Ok(new { item.Id, pushedOnline = pushed });
    }

    private async Task AuditAsync(string action, int targetId, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = GetUserId(),
            Action = action,
            TargetType = "AppUpdate",
            TargetId = targetId,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst("sub")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>
/// 更新清单创建请求
/// </summary>
public class UpdateSaveRequest
{
    [System.ComponentModel.DataAnnotations.MaxLength(32)] public string VersionName { get; set; } = "";
    public int VersionCode { get; set; }
    public int MinVersionCode { get; set; }
    public string? Changelog { get; set; }
    /// <summary>[TASK-UPDATE-CHANNEL] 分发渠道：stable（默认）/ special（特别版）</summary>
    public string? Channel { get; set; }
}
