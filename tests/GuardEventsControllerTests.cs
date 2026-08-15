using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests.Controllers;

/// <summary>
/// [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护失守事件 + 健康度控制器测试
///
/// 覆盖：
/// - 上传 + 查询回读（含 healthJson 解析为对象、ReceivedAt 倒序）；
/// - 表名一致性：真实 SQLite 上「DataExtensions 建表 DDL」与「EF 模型 ToTable("guard_events")」
///   落在同一张表（Bug3 app_logs 表名不匹配的回归防线）；
/// - 账号隔离（[SEC-K2]）：家长 A 不能查/传家长 B 账号设备的守卫事件（403/404）；
/// - admin 全量查询 + 按设备过滤 + 任意已存在设备可上传；
/// - health 接口：返回最近一条含 health 的事件，无数据返回双 null。
/// </summary>
public class GuardEventsControllerTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static GuardEventsController CreateController(AppDbContext db, int userId, string? role = null)
    {
        var controller = new GuardEventsController(db, NullLogger<GuardEventsController>.Instance);
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

    private static object? Prop(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    private static JsonElement Health(string json) => JsonDocument.Parse(json).RootElement;

    private static GuardEventUploadRequest Request(string deviceId, params GuardEventItemRequest[] items) =>
        new() { DeviceId = deviceId, Events = items.ToList() };

    private static void SeedDevice(AppDbContext db, string deviceId, int? ownerUserId)
    {
        db.Devices.Add(new Device
        {
            DeviceName = $"设备-{deviceId}",
            DeviceId = deviceId,
            OwnerUserId = ownerUserId?.ToString(),
            PairStatus = "paired",
        });
    }

    // ==================== 上传 + 查询回读 ====================

    [Fact]
    public async Task Upload_AndList_RoundTrip_WithParsedHealth()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 5);

        var upload = await controller.Upload(Request("XP-A-01",
            new GuardEventItemRequest
            {
                Event = "guard_down", StartTs = 1755250000, EndTs = 1755250300,
                DurationSec = 300, Reason = "process_killed", WasEnforcing = true,
            },
            new GuardEventItemRequest
            {
                Event = "health_snapshot", StartTs = 1755250000,
                Health = Health("{\"score\":83,\"readyCount\":5,\"totalCount\":6,\"status\":\"good\",\"guardDown\":false}"),
            }));
        Assert.IsType<OkObjectResult>(upload);

        var result = await controller.List(deviceId: "XP-A-01", limit: 50);
        var ok = Assert.IsType<OkObjectResult>(result);
        var events = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(ok.Value!, "events")!).Cast<object>().ToList();
        Assert.Equal(2, events.Count);

        // ReceivedAt 倒序：后上传的 health_snapshot 在前
        Assert.Equal("health_snapshot", Prop(events[0], "eventType"));
        Assert.Equal("guard_down", Prop(events[1], "eventType"));
        Assert.Equal("XP-A-01", Prop(events[1], "deviceId"));
        Assert.Equal(1755250000L, Prop(events[1], "startedAt"));
        Assert.Equal(1755250300L, Prop(events[1], "endedAt"));
        Assert.Equal(300L, Prop(events[1], "durationSeconds"));
        Assert.Equal("process_killed", Prop(events[1], "reason"));
        Assert.True((bool)Prop(events[1], "wasEnforcing")!);

        // healthJson 字符串存储，响应时解析为对象
        var health = Assert.IsType<JsonElement>(Prop(events[0], "healthJson"));
        Assert.Equal(83, health.GetProperty("score").GetInt32());
        Assert.Equal("good", health.GetProperty("status").GetString());
        Assert.Null(Prop(events[1], "healthJson"));
    }

    [Fact]
    public async Task Upload_ValidationRules()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 5);

        // events 为空 / 缺失
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(new GuardEventUploadRequest { DeviceId = "XP-A-01" }));
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(Request("XP-A-01")));

        // events 超 100 条
        var tooMany = Enumerable.Range(0, 101).Select(_ => new GuardEventItemRequest { Event = "guard_down" }).ToList();
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(new GuardEventUploadRequest { DeviceId = "XP-A-01", Events = tooMany }));

        // deviceId 缺失
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(Request(null!,
            new GuardEventItemRequest { Event = "guard_down" })));

        // event 字段缺失 → 400
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(Request("XP-A-01",
            new GuardEventItemRequest { StartTs = 1755250000 })));

        // 负数时间戳/时长 → 400
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(Request("XP-A-01",
            new GuardEventItemRequest { Event = "guard_down", StartTs = -1 })));
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(Request("XP-A-01",
            new GuardEventItemRequest { Event = "guard_down", DurationSec = -5 })));

        // 设备不存在 → 404
        var notFound = Assert.IsAssignableFrom<ObjectResult>(await controller.Upload(Request("XP-NOPE",
            new GuardEventItemRequest { Event = "guard_down" })));
        Assert.Equal(404, notFound.StatusCode);

        // 校验失败不落库
        Assert.Empty(db.GuardEvents.ToList());
    }

    // ==================== 账号隔离（[SEC-K2]） ====================

    [Fact]
    public async Task Upload_ParentForbiddenForOtherAccountsDevice()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5); // 家长 5 的设备
        SeedDevice(db, "XP-B-01", ownerUserId: 6); // 家长 6 的设备
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 5);

        // 家长 5 上传到家长 6 的设备 → 403
        var forbidden = Assert.IsAssignableFrom<ObjectResult>(await controller.Upload(Request("XP-B-01",
            new GuardEventItemRequest { Event = "guard_down" })));
        Assert.Equal(403, forbidden.StatusCode);

        // 自己设备 → 200
        Assert.IsType<OkObjectResult>(await controller.Upload(Request("XP-A-01",
            new GuardEventItemRequest { Event = "guard_restored", RestoredReason = "swipe_recovery" })));
        Assert.Single(db.GuardEvents.ToList());
    }

    [Fact]
    public async Task List_ParentSeesOnlyOwnDevice_AndCannotQueryOthers()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5);
        SeedDevice(db, "XP-B-01", ownerUserId: 6);
        await db.SaveChangesAsync();
        db.GuardEvents.AddRange(
            new GuardEvent { DeviceId = "XP-A-01", EventType = "guard_down", ReceivedAt = DateTime.UtcNow },
            new GuardEvent { DeviceId = "XP-B-01", EventType = "guard_restored", ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 5);

        // 明确指定他人设备 → 403
        var forbidden = Assert.IsAssignableFrom<ObjectResult>(await controller.List(deviceId: "XP-B-01", limit: 50));
        Assert.Equal(403, forbidden.StatusCode);

        // 指定自己的设备 → 仅本设备
        var own = Assert.IsType<OkObjectResult>(await controller.List(deviceId: "XP-A-01", limit: 50));
        var ownEvents = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(own.Value!, "events")!).Cast<object>().ToList();
        Assert.Single(ownEvents);
        Assert.Equal("XP-A-01", Prop(ownEvents[0], "deviceId"));

        // 不指定设备 → 强制收敛到本账号设备（不含他人设备事件）
        var all = Assert.IsType<OkObjectResult>(await controller.List(deviceId: null, limit: 50));
        var allEvents = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(all.Value!, "events")!).Cast<object>().ToList();
        Assert.Single(allEvents);
    }

    [Fact]
    public async Task Admin_SeesAll_CanFilterByDevice_AndUploadToAnyExistingDevice()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5);
        SeedDevice(db, "XP-B-01", ownerUserId: 6);
        await db.SaveChangesAsync();
        db.GuardEvents.AddRange(
            new GuardEvent { DeviceId = "XP-A-01", EventType = "guard_down", ReceivedAt = DateTime.UtcNow },
            new GuardEvent { DeviceId = "XP-B-01", EventType = "guard_restored", ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var admin = CreateController(db, userId: 1, role: "admin");

        // admin 不指定设备 → 全量
        var all = Assert.IsType<OkObjectResult>(await admin.List(deviceId: null, limit: 50));
        var allEvents = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(all.Value!, "events")!).Cast<object>().ToList();
        Assert.Equal(2, allEvents.Count);

        // admin 指定设备 → 过滤
        var filtered = Assert.IsType<OkObjectResult>(await admin.List(deviceId: "XP-A-01", limit: 50));
        var filteredEvents = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(filtered.Value!, "events")!).Cast<object>().ToList();
        Assert.Single(filteredEvents);

        // admin 可向任意已存在设备上传（无需归属匹配）
        Assert.IsType<OkObjectResult>(await admin.Upload(Request("XP-B-01",
            new GuardEventItemRequest { Event = "guard_down", Reason = "swipe_killed" })));
        Assert.Equal(3, db.GuardEvents.Count());
    }

    // ==================== health 接口 ====================

    [Fact]
    public async Task Health_ReturnsLatestEventWithHealth_AndNullWhenNoData()
    {
        var db = CreateInMemoryDbContext();
        SeedDevice(db, "XP-A-01", ownerUserId: 5);
        SeedDevice(db, "XP-B-01", ownerUserId: 5);
        await db.SaveChangesAsync();
        db.GuardEvents.AddRange(
            // 无 health 的最新事件：不应被选中
            new GuardEvent { DeviceId = "XP-A-01", EventType = "guard_down", ReceivedAt = DateTime.UtcNow.AddMinutes(1) },
            new GuardEvent { DeviceId = "XP-A-01", EventType = "health_snapshot", HealthJson = "{\"score\":55,\"status\":\"attention\"}", ReceivedAt = DateTime.UtcNow.AddMinutes(2) },
            new GuardEvent { DeviceId = "XP-A-01", EventType = "health_snapshot", HealthJson = "{\"score\":91,\"status\":\"good\"}", ReceivedAt = DateTime.UtcNow.AddMinutes(3) },
            // XP-B-01：仅失守事件、无任何 health → 双 null
            new GuardEvent { DeviceId = "XP-B-01", EventType = "guard_down", ReceivedAt = DateTime.UtcNow.AddMinutes(4) });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 5);

        // 返回最近一条含 health 的事件（score 91，忽略更新的 guard_down）
        var ok = Assert.IsType<OkObjectResult>(await controller.LatestHealth("XP-A-01"));
        var health = Assert.IsType<JsonElement>(Prop(ok.Value!, "health")!);
        Assert.Equal(91, health.GetProperty("score").GetInt32());
        Assert.NotNull(Prop(ok.Value!, "updatedAt"));

        // 无任何含 health 事件 → 双 null
        var empty = Assert.IsType<OkObjectResult>(await controller.LatestHealth("XP-B-01"));
        Assert.Null(Prop(empty.Value!, "health"));
        Assert.Null(Prop(empty.Value!, "updatedAt"));

        // 他人设备 → 403；不存在设备 → 404
        SeedDevice(db, "XP-C-01", ownerUserId: 6);
        await db.SaveChangesAsync();
        var forbidden = Assert.IsAssignableFrom<ObjectResult>(await controller.LatestHealth("XP-C-01"));
        Assert.Equal(403, forbidden.StatusCode);
        var notFound = Assert.IsAssignableFrom<ObjectResult>(await controller.LatestHealth("XP-NOPE"));
        Assert.Equal(404, notFound.StatusCode);
    }

    // ==================== 真实 SQLite：表名一致性（Bug3 回归防线） ====================

    [Fact]
    public async Task RealSqlite_DataExtensionsDdl_And_EFModel_UseSameGuardEventsTable()
    {
        // 真实 SQLite 文件库（内存连接）：验证「DataExtensions 建表 DDL」与
        // 「EF 模型 ToTable("guard_events")」落在同一张表 —— Bug3（app_logs 表名不匹配）的回归防线。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);

        // 1. 既有库路径：执行 DataExtensions.EnsureMissingTablesAsync 真实建表 DDL
        var logger = NullLoggerFactory.Instance.CreateLogger("GuardEventsSchemaTest");
        var method = typeof(DataExtensions).GetMethod("EnsureMissingTablesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var created = Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { db, logger })!);
        await created;

        // 2. EF 模型写入（ToTable("guard_events") → 必须落到同名表）
        db.GuardEvents.Add(new GuardEvent
        {
            DeviceId = "XP-REAL-01",
            EventType = "guard_down",
            StartedAt = 1755250000,
            EndedAt = 1755250300,
            DurationSeconds = 300,
            Reason = "process_killed",
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // 3. 原生 SQL 从 guard_events 读回 → 证明写入与查询同一张表
        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM \"guard_events\"").FirstOrDefaultAsync();
        Assert.Equal(1, count);
        var reason = await db.Database.SqlQueryRaw<string>(
            "SELECT \"Reason\" AS Value FROM \"guard_events\" WHERE \"Id\" = 1").FirstOrDefaultAsync();
        Assert.Equal("process_killed", reason);

        // 4. 新库路径：EnsureCreated 按模型建表，表名同样必须是 guard_events
        // 注：EnsureDeleted 不会清空 ChangeTracker，重建前清跟踪避免主键冲突
        db.ChangeTracker.Clear();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
        Assert.Contains("guard_events", tables);

        // 5. 新库上 EF 写入 → 原生 SQL 同表回读（全链路）
        db.GuardEvents.Add(new GuardEvent
        {
            DeviceId = "XP-REAL-02",
            EventType = "health_snapshot",
            HealthJson = "{\"score\":83,\"status\":\"good\"}",
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var count2 = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM \"guard_events\"").FirstOrDefaultAsync();
        Assert.Equal(1, count2);
    }
}
