using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

/// <summary>
/// [TASK-MILESTONE-V3] 需求 14：日志上传/查看控制器测试
///
/// 覆盖：
/// - 上传：账号归属绑定、批上限拒绝、空批拒绝、敏感内容入库前二次打码、空内容跳过；
/// - 查看：普通家长仅本账号、admin 全部 + 按账号/级别筛选；
/// - 保留策略：超 7 天（ReceivedAt）条目上传时被清理。
/// </summary>
public class LogsControllerTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static LogsController CreateController(AppDbContext db, int userId, string? role = null)
    {
        var controller = new LogsController(db, NullLogger<LogsController>.Instance);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role != null)
            claims.Add(new Claim(ClaimTypes.Role, role));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
        return controller;
    }

    private static List<LogItemRequest> Batch(params (string Level, string Tag, string Msg)[] items)
        => items.Select(i => new LogItemRequest { Level = i.Level, Tag = i.Tag, Msg = i.Msg }).ToList();

    // ==================== 上传 ====================

    [Fact]
    public async Task Upload_BindsToAccount_AndStoresEntries()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db, userId: 5);

        var result = await controller.Upload(new LogUploadRequest
        {
            Client = "Pixel7/14",
            Logs = Batch(("I", "App", "应用启动 v1.1.0"), ("E", "Crash", "boom")),
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = db.AppLogEntries.ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(5, r.AccountId));
        Assert.All(rows, r => Assert.Equal("Pixel7/14", r.Client));
        Assert.Contains(rows, r => r.Level == "error" && r.Tag == "Crash");
    }

    [Fact]
    public async Task Upload_RejectsEmptyBatch()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db, userId: 5);

        var result = await controller.Upload(new LogUploadRequest { Logs = new List<LogItemRequest>() });
        Assert.IsType<BadRequestObjectResult>(result);

        var result2 = await controller.Upload(new LogUploadRequest { Logs = null });
        Assert.IsType<BadRequestObjectResult>(result2);
    }

    [Fact]
    public async Task Upload_RejectsOversizedBatch()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db, userId: 5);
        var tooMany = Enumerable.Range(0, 501)
            .Select(i => new LogItemRequest { Level = "I", Tag = "T", Msg = "m" })
            .ToList();

        var result = await controller.Upload(new LogUploadRequest { Logs = tooMany });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Upload_SanitizesSensitiveContentBeforeStoring()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db, userId: 5);

        await controller.Upload(new LogUploadRequest
        {
            Logs = Batch(
                ("I", "Account", "登录成功 password=plain123"),
                ("I", "Account", "验证码 654321 已发送"),
                ("W", "Auth", "token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sigAaaaAaaaAaaaAaaaAaaa"),
                ("I", "App", "empty-message-test"),
                ("I", "App", "")
            ),
        });

        var stored = db.AppLogEntries.ToList();
        Assert.Equal(4, stored.Count); // 空内容一条被跳过
        Assert.Contains(stored, r => r.Message.Contains("password=***"));
        Assert.DoesNotContain(stored, r => r.Message.Contains("plain123"));
        Assert.Contains(stored, r => r.Message.Contains("验证码 ***"));
        Assert.DoesNotContain(stored, r => r.Message.Contains("654321"));
        // 裸 JWT 打码（客户端绕过第一层打码时服务端兜底）
        Assert.Contains(stored, r => r.Message == "token=***");
    }

    [Fact]
    public async Task Upload_NormalizesLevelWhitelist()
    {
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db, userId: 5);

        await controller.Upload(new LogUploadRequest
        {
            Logs = Batch(("D", "T", "d"), ("E", "T", "e"), ("fatal", "T", "f"), ("weird", "T", "w")),
        });

        var levels = db.AppLogEntries.Select(r => r.Level).OrderBy(l => l).ToList();
        Assert.Equal(new[] { "debug", "error", "error", "info" }, levels);
    }

    // ==================== 查看：账号隔离 ====================

    [Fact]
    public async Task List_ParentSeesOnlyOwnAccount()
    {
        var db = CreateInMemoryDbContext();
        db.AppLogEntries.AddRange(
            new AppLogEntry { AccountId = 5, Level = "info", Tag = "A", Message = "own", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow },
            new AppLogEntry { AccountId = 6, Level = "info", Tag = "A", Message = "others", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 5);
        var result = await controller.List(null, null, null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<object[]>(ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value));
        Assert.Single(items);
    }

    [Fact]
    public async Task List_AdminSeesAll_AndCanFilterByAccountAndLevel()
    {
        var db = CreateInMemoryDbContext();
        db.AppLogEntries.AddRange(
            new AppLogEntry { AccountId = 5, Level = "info", Tag = "A", Message = "m5", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow },
            new AppLogEntry { AccountId = 6, Level = "warn", Tag = "B", Message = "m6", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var admin = CreateController(db, userId: 1, role: "admin");

        var all = Assert.IsType<OkObjectResult>(await admin.List(null, null, null, null));
        var allItems = Assert.IsAssignableFrom<object[]>(all.Value!.GetType().GetProperty("items")!.GetValue(all.Value));
        Assert.Equal(2, allItems.Length);

        var filtered = Assert.IsType<OkObjectResult>(await admin.List(accountId: 6, level: "warn", from: null, to: null));
        var filteredItems = Assert.IsAssignableFrom<object[]>(filtered.Value!.GetType().GetProperty("items")!.GetValue(filtered.Value));
        Assert.Single(filteredItems);
    }

    // ==================== 保留策略：7 天 ====================

    [Fact]
    public async Task Upload_CleansUpEntriesOlderThan7Days()
    {
        var db = CreateInMemoryDbContext();
        db.AppLogEntries.AddRange(
            new AppLogEntry { AccountId = 5, Level = "info", Tag = "A", Message = "fresh", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow },
            new AppLogEntry { AccountId = 5, Level = "info", Tag = "A", Message = "expired", CreatedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow.AddDays(-8) });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 5);
        await controller.Upload(new LogUploadRequest
        {
            Logs = Batch(("I", "App", "new-entry")),
        });

        var remaining = db.AppLogEntries.ToList();
        Assert.DoesNotContain(remaining, r => r.Message == "expired");
        Assert.Contains(remaining, r => r.Message == "fresh");
        Assert.Contains(remaining, r => r.Message == "new-entry");
    }
}
