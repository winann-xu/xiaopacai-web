using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using XiaopacaiWeb.Controllers;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests;

/// <summary>
/// [TASK-APP-UPDATE-V1] app_updates 表映射回归防线（沿用 app_logs Bug3-A 教训）：
/// 1. 存量库路径：DataExtensions 按 DDL 建 app_updates 后，EF 写入 + 原生 SQL 回读同一张表；
/// 2. 新库路径：EnsureCreated 建出的表名就是 app_updates；
/// 3. ABI 映射 JSON 列解析（正常/空串/损坏数据）。
/// </summary>
public class AppUpdateTableMappingTests
{
    [Fact]
    public async Task RealSqlite_ExistingDbDdl_And_EFModel_UseSameAppUpdatesTable()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);

        var logger = NullLoggerFactory.Instance.CreateLogger("AppUpdateSchemaTest");
        var method = typeof(DataExtensions).GetMethod("EnsureMissingTablesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { db, logger })!);

        db.AppUpdates.Add(new AppUpdate
        {
            Platform = "android",
            VersionName = "1.2.0",
            VersionCode = 10200,
            MinVersionCode = 10200,
            AbiUrls = "{\"arm64-v8a\":\"/downloads/XiaopacaiParent-1.2.0-arm64-v8a.apk\"}",
            AbiSha256 = "{\"arm64-v8a\":\"abc123\"}",
            Changelog = "更新说明",
            Status = "draft",
            CreatedBy = 1,
        });
        await db.SaveChangesAsync();

        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM \"app_updates\"").FirstOrDefaultAsync();
        Assert.Equal(1, count);
        var version = await db.Database.SqlQueryRaw<string>(
            "SELECT \"VersionName\" AS Value FROM \"app_updates\" WHERE \"Id\" = 1").FirstOrDefaultAsync();
        Assert.Equal("1.2.0", version);
    }

    [Fact]
    public void ParseAbiMap_ValidJson_ReturnsMap()
    {
        var map = UpdatesController.ParseAbiMap(
            "{\"arm64-v8a\":\"/downloads/a.apk\",\"x86_64\":\"/downloads/x.apk\"}");
        Assert.Equal(2, map.Count);
        Assert.Equal("/downloads/a.apk", map["arm64-v8a"]);
    }

    [Fact]
    public void ParseAbiMap_EmptyOrBroken_ReturnsEmpty()
    {
        Assert.Empty(UpdatesController.ParseAbiMap(""));
        Assert.Empty(UpdatesController.ParseAbiMap("not-json"));
        Assert.Empty(UpdatesController.ParseAbiMap(null!));
    }

    // =====================================================================
    // [TASK-APP-UPDATE-V1] 公开检查接口语义回归：
    // - versionCode=0（下载中心）→ 恒返回最新已发布版本（文档约定，曾实现为 400）；
    // - versionCode 不低于最新 → hasUpdate=false（客户端防降级兜底）。
    // =====================================================================
    private static async Task<(SqliteConnection conn, AppDbContext db, UpdatesController controller)> CreateCheckFixtureAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // AppUpdate.CreatedBy 外键指向 users；先建测试账号（FK 约束）
        db.Users.Add(new User
        {
            Id = 1,
            Username = "update-tester",
            PasswordHash = "x",
            PasswordSalt = "y",
            DisplayName = "测试",
            Role = "admin",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        db.AppUpdates.Add(new AppUpdate
        {
            Platform = "android",
            VersionName = "1.2.0",
            VersionCode = 10200,
            MinVersionCode = 10200,
            AbiUrls = "{\"arm64-v8a\":\"/downloads/XiaopacaiParent-1.2.0-arm64-v8a.apk\"}",
            AbiSha256 = "{\"arm64-v8a\":\"de3f51e77e024df0d8e39bd0de561e579d6afaf47630f60693a9c4db0fdc1d8c\"}",
            SizeBytes = 8231020,
            Changelog = "自动更新闭环",
            Status = "published",
            PublishedAt = DateTime.UtcNow,
            CreatedBy = 1,
        });
        await db.SaveChangesAsync();

        var controller = new UpdatesController(db, NullLogger<UpdatesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (conn, db, controller);
    }

    [Fact]
    public async Task Check_ZeroVersionCode_ReturnsLatestPublished()
    {
        var (conn, db, controller) = await CreateCheckFixtureAsync();
        try
        {
            var result = await controller.Check("android", "arm64-v8a", 0, "stable") as OkObjectResult;
            Assert.NotNull(result);
            var value = result!.Value!;
            var hasUpdate = value.GetType().GetProperty("hasUpdate")?.GetValue(value);
            var latestCode = value.GetType().GetProperty("latestVersionCode")?.GetValue(value);
            Assert.Equal(true, hasUpdate);
            Assert.Equal(10200, latestCode);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Check_CurrentNotBelowLatest_NoUpdate()
    {
        var (conn, db, controller) = await CreateCheckFixtureAsync();
        try
        {
            var result = await controller.Check("android", "arm64-v8a", 10200, "stable") as OkObjectResult;
            Assert.NotNull(result);
            var value = result!.Value!;
            var hasUpdate = value.GetType().GetProperty("hasUpdate")?.GetValue(value);
            Assert.Equal(false, hasUpdate);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // =====================================================================
    // [TASK-UPDATE-CHANNEL] 渠道隔离语义回归：
    // - 缺省 channel → stable（旧客户端兼容）；
    // - stable 设备只见 stable 最新版，special 设备只见 special 最新版，互不串线；
    // - 非法 channel → 400。
    // =====================================================================
    [Fact]
    public async Task Check_ChannelIsolation_StableAndSpecialNeverMix()
    {
        var (conn, db, controller) = await CreateCheckFixtureAsync();
        try
        {
            // stable 1.2.0(10200) 已由 fixture 建好；再建 special 1.3.3-testkey(10304)
            db.AppUpdates.Add(new AppUpdate
            {
                Platform = "android",
                VersionName = "1.3.3-testkey",
                VersionCode = 10304,
                MinVersionCode = 10304,
                AbiUrls = "{\"arm64-v8a\":\"/downloads/XiaopacaiParent-1.3.3-testkey-arm64-v8a.apk\"}",
                AbiSha256 = "{\"arm64-v8a\":\"edfab919c51ae65375726dba0f036217e533a5ee01e18f275b09785b56f69c0f\"}",
                Changelog = "ColorOS 特别版",
                Status = "published",
                PublishedAt = DateTime.UtcNow,
                Channel = "special",
                CreatedBy = 1,
            });
            await db.SaveChangesAsync();

            var stable = await controller.Check("android", "arm64-v8a", 0, "stable") as OkObjectResult;
            var special = await controller.Check("android", "arm64-v8a", 0, "special") as OkObjectResult;
            var legacyDefault = await controller.Check("android", "arm64-v8a", 0, null) as OkObjectResult;

            Assert.NotNull(stable);
            Assert.Equal(10200, GetProp(stable!.Value!, "latestVersionCode"));
            Assert.Equal("stable", GetProp(stable!.Value!, "channel"));

            Assert.NotNull(special);
            Assert.Equal(10304, GetProp(special!.Value!, "latestVersionCode"));
            Assert.Equal("special", GetProp(special!.Value!, "channel"));

            Assert.NotNull(legacyDefault);
            Assert.Equal(10200, GetProp(legacyDefault!.Value!, "latestVersionCode"));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Check_InvalidChannel_BadRequest()
    {
        var (conn, db, controller) = await CreateCheckFixtureAsync();
        try
        {
            var result = await controller.Check("android", "arm64-v8a", 0, "beta");
            Assert.IsType<BadRequestObjectResult>(result);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static object? GetProp(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);
}
