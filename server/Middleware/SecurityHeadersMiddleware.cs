namespace XiaopacaiWeb.Middleware;

/// <summary>
/// [SEC-K6] 安全响应头（HSTS 由 UseHsts 中间件负责，此处补齐其余基线头）：
/// - X-Content-Type-Options: nosniff（防 MIME 嗅探）
/// - X-Frame-Options: DENY（防点击劫持）
/// - Referrer-Policy: no-referrer（防 Referrer 泄露内部路径/Token）
/// - Permissions-Policy：禁用摄像头/麦克风/定位等敏感 API
/// - Content-Security-Policy：同源脚本/连接；style-src 'unsafe-inline'
///   为 Element Plus 动态注入样式所需；img-src data: 为二维码图片所需
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

        await _next(context);
    }
}
