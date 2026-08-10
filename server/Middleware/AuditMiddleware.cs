// 审计中间件 — P1 骨架（P2 阶段实现完整审计日志记录）

namespace XiaopacaiWeb.Middleware;

/// <summary>
/// 审计日志中间件：记录关键 API 操作到 audit_logs 表
/// </summary>
public class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // P1 阶段仅记录请求路径日志
        _logger.LogDebug("[审计] {Method} {Path} @ {Ip}",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress);

        await _next(context);
    }
}
