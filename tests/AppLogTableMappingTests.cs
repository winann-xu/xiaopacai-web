using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Models;
using Xunit;

namespace XiaopacaiWeb.Tests;

/// <summary>
/// [TASK-HARDENING-V1.1.1] Bug3-A 回归防线：
///
/// 根因：EF 默认按 DbSet 属性名建/查表（AppLogEntries），而 DataExtensions
/// 建表 DDL 使用 app_logs → 上传写入与查询分表，查询报 "no such table" 500。
/// 此前 LogsControllerTests 用 InMemory 提供程序（不校验真实表名）故未拦截。
///
/// 本文件全部用真实 SQLite（:memory:）验证：
/// 1. 存量库路径：DDL 建 app_logs 后，EF 写入 + 原生 SQL 回读同一张表；
/// 2. 新库路径：EnsureCreated 建出的表名就是 app_logs（不是 AppLogEntries）。
/// </summary>
public class AppLogTableMappingTests
{
    [Fact]
    public async Task RealSqlite_ExistingDbDdl_And_EFModel_UseSameAppLogsTable()
    {
        // 模拟存量库：DataExtensions 已按 DDL 建好 app_logs（线上现状）
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);

        var logger = NullLoggerFactory.Instance.CreateLogger("AppLogSchemaTest");
        var method = typeof(DataExtensions).GetMethod("EnsureMissingTablesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { db, logger })!);

        // EF 写入（ToTable("app_logs") → 必须落到 DDL 建出的同一张表）
        db.AppLogEntries.Add(new AppLogEntry
        {
            AccountId = 7,
            Level = "error",
            Tag = "GuardianService",
            Message = "无障碍服务已关闭",
            Client = "OPPO-X",
            CreatedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // 原生 SQL 从 app_logs 读回 → 写入与查询同一张表（修复前此处即 500）
        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM \"app_logs\"").FirstOrDefaultAsync();
        Assert.Equal(1, count);
        var message = await db.Database.SqlQueryRaw<string>(
            "SELECT \"Message\" AS Value FROM \"app_logs\" WHERE \"Id\" = 1").FirstOrDefaultAsync();
        Assert.Equal("无障碍服务已关闭", message);
    }

    [Fact]
    public async Task RealSqlite_EnsureCreated_CreatesAppLogsTable()
    {
        // 新库路径：EnsureCreated 按模型建表，表名必须是 app_logs（而非 AppLogEntries）
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);

        await db.Database.EnsureCreatedAsync();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
        Assert.Contains("app_logs", tables);
        Assert.DoesNotContain("AppLogEntries", tables);

        // 新库上 EF 写入 → 原生 SQL 同表回读（全链路）
        db.AppLogEntries.Add(new AppLogEntry
        {
            AccountId = 8,
            Level = "info",
            Tag = "App",
            Message = "启动",
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM \"app_logs\"").FirstOrDefaultAsync();
        Assert.Equal(1, count);
    }
}
