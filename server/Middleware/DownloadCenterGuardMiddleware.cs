namespace XiaopacaiWeb.Middleware;

/// <summary>
/// [SEC-K8] 下载中心/静态资源暴露面收敛：
/// - /downloads/* 仅允许白名单扩展名（安装包/授权脚本），其余一律 404
/// - 全局拒绝敏感文件扩展名（数据库/密钥/证书/备份/配置）经 wwwroot 泄露
/// - 拒绝路径穿越（URL 解码后仍含 .. 或反斜杠）
/// 静态文件中间件自身已有穿越防护，此处为纵深防御 + 白名单收口。
/// </summary>
public class DownloadCenterGuardMiddleware
{
    /// <summary>下载中心允许的扩展名（与 DownloadPage 提供的安装包/脚本一致）</summary>
    private static readonly HashSet<string> DownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apk", ".ipa", ".dmg", ".bat", ".zip",
    };

    /// <summary>全局拒绝经 wwwroot 提供的敏感扩展名（纵深防御）</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".db3", ".dbkey", ".pfx", ".pem", ".key", ".crt",
        ".bak", ".env", ".config",
    };

    private readonly RequestDelegate _next;

    public DownloadCenterGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // 路径穿越防护（URL 解码后仍含 .. 或反斜杠即拒绝）
        var decoded = Uri.UnescapeDataString(path);
        if (decoded.Contains("..") || decoded.Contains('\\'))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ext = Path.GetExtension(decoded);

        // 下载中心白名单收口
        if (path.StartsWith("/downloads/", StringComparison.OrdinalIgnoreCase) &&
            !DownloadExtensions.Contains(ext))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 敏感文件全局拒绝
        if (BlockedExtensions.Contains(ext))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
