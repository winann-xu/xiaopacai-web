using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Services;

namespace XiaopacaiWeb.Data;

/// <summary>
/// 数据库扩展 — DI 注册 + 自动迁移 + 种子数据
/// </summary>
public static class DataExtensions
{
    /// <summary>
    /// 注册数据库服务：DbContext + SqlCipher + 自动迁移
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sqlCipher = scope.ServiceProvider.GetRequiredService<ISqlCipherService>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DatabaseInit");

        // 1. SQLCipher 初始化（密钥 + PRAGMA key）
        await sqlCipher.InitializeAsync();

        // 2. 自动创建/迁移数据库
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("[DB] EnsureCreated 完成");

        // 2.1 兼容已有库：EnsureCreated 不会为既有库补新表，手工补齐缺失表
        await EnsureMissingTablesAsync(db, logger);

        // 3. 种子数据（仅当 users 表为空时）
        if (!await db.Users.AnyAsync())
        {
            await SeedDefaultAdmin(db, passwordHasher);
            logger.LogInformation("[DB] 种子数据已插入（默认管理员）");
        }

        // 4. 默认系统配置（仅当配置表为空时）
        if (!await db.SystemConfigs.AnyAsync())
        {
            db.SystemConfigs.AddRange(
                new SystemConfig { Key = "notification_enabled", Value = "true" },
                new SystemConfig { Key = "data_retention_days", Value = "90" },
                new SystemConfig { Key = "backup_dir", Value = "backups" },
                new SystemConfig { Key = "web_port", Value = "5000" },
                new SystemConfig { Key = "p2p_port", Value = "9527" }
            );
            await db.SaveChangesAsync();
            logger.LogInformation("[DB] 默认系统配置已插入");
        }
    }

    /// <summary>
    /// 为已存在的数据库补齐后续版本新增的表（增量演进，不破坏既有数据）
    /// </summary>
    private static async Task EnsureMissingTablesAsync(AppDbContext db, ILogger logger)
    {
        // 检查 system_configs 表是否存在，不存在则创建
        var hasConfig = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='system_configs'"
        ).FirstOrDefaultAsync() > 0;

        if (!hasConfig)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "system_configs" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_system_configs" PRIMARY KEY,
                    "Value" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """);
            logger.LogInformation("[DB] 已补齐 system_configs 表");
        }
    }

    private static async Task SeedDefaultAdmin(AppDbContext db, IPasswordHasher hasher)
    {
        var (hash, salt) = hasher.HashPassword("admin123");

        db.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt,
            DisplayName = "管理员",
            Role = "admin",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
