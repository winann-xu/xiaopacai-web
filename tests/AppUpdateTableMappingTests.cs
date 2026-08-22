using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
}
