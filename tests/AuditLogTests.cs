using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Middleware;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests.Models;

/// <summary>
/// 审计日志测试 — AuditLog 模型 + 审计中间件管道
///
/// 覆盖：
/// - AuditLog 模型默认值 / 必填字段 / MaxLength 注解
/// - 审计中间件：请求正常透传、不阻断业务、日志记录不抛异常
///
/// 说明：P2-D 阶段审计中间件仅为骨架（记录调试日志），
/// 完整审计落库（写入 audit_logs 表）为规划中功能，届时补充 DB 断言测试。
/// </summary>
public class AuditLogTests
{
    // ==================== AuditLog 模型 ====================

    [Fact]
    public void AuditLog_HasCorrectDefaults()
    {
        var log = new AuditLog();

        Assert.Equal(string.Empty, log.Action);
        Assert.Null(log.UserId);
        Assert.Null(log.TargetType);
        Assert.Null(log.TargetId);
        Assert.Null(log.Detail);
        Assert.Null(log.IpAddress);
        Assert.Null(log.UserAgent);
        Assert.True(log.CreatedAt <= DateTime.UtcNow);
        Assert.True(log.CreatedAt >= DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void AuditLog_Action_Required()
    {
        var log = new AuditLog { Action = "" };
        var results = ValidateModel(log);

        Assert.Contains(results, r => r.MemberNames.Contains("Action"));
    }

    [Fact]
    public void AuditLog_Action_MaxLength64()
    {
        var log = new AuditLog { Action = new string('a', 65) };
        var results = ValidateModel(log);

        Assert.Contains(results, r =>
            r.MemberNames.Contains("Action") && r.ErrorMessage!.Contains("64"));
    }

    [Fact]
    public void AuditLog_FieldLengths_Enforced()
    {
        // 各字段长度上限：TargetType=32 / IpAddress=45 / UserAgent=256
        var log = new AuditLog
        {
            Action = "login",
            TargetType = new string('t', 33),
            IpAddress = new string('i', 46),
            UserAgent = new string('u', 257),
        };
        var results = ValidateModel(log);

        Assert.Contains(results, r => r.MemberNames.Contains("TargetType"));
        Assert.Contains(results, r => r.MemberNames.Contains("IpAddress"));
        Assert.Contains(results, r => r.MemberNames.Contains("UserAgent"));
    }

    [Fact]
    public void AuditLog_ValidEntry_PassesValidation()
    {
        var log = new AuditLog
        {
            UserId = 1,
            Action = "policy.update",
            TargetType = "Policy",
            TargetId = 42,
            Detail = """{"dailyLimit":120}""",
            IpAddress = "192.168.1.10",
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64)",
            CreatedAt = DateTime.UtcNow,
        };

        Assert.Empty(ValidateModel(log));
    }

    // ==================== 审计中间件 ====================

    [Fact]
    public async Task AuditMiddleware_InvokesNextAndCompletes()
    {
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var middleware = new AuditMiddleware(next, NullLogger<AuditMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled, "中间件必须透传请求到下游");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuditMiddleware_HandlesMissingRemoteIp_NoThrow()
    {
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new AuditMiddleware(next, NullLogger<AuditMiddleware>.Instance);
        var context = new DefaultHttpContext(); // 无 RemoteIpAddress

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AuditMiddleware_MultipleRequests_Stable()
    {
        RequestDelegate next = ctx => Task.CompletedTask;
        var middleware = new AuditMiddleware(next, NullLogger<AuditMiddleware>.Instance);

        // 连续多次请求不应有任何状态泄漏
        for (var i = 0; i < 10; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/health";
            await middleware.InvokeAsync(context);
        }
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
