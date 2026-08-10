using Microsoft.AspNetCore.Mvc;

namespace XiaopacaiWeb.Controllers;

/// <summary>
/// 健康检查控制器 — P1 骨架验证
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// GET /api/health — 返回服务健康状态
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            version = "3.0.0-p2",
            timestamp = DateTime.UtcNow.ToString("O"),
            service = "xiaopacai-web",
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"dd\.hh\:mm\:ss")
        });
    }

    /// <summary>
    /// GET /api/health/ping — 简单连通性检查
    /// </summary>
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new { pong = true });
    }
}
