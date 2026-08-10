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

        // 3. 种子数据（仅当 users 表为空时）
        if (!await db.Users.AnyAsync())
        {
            await SeedDefaultAdmin(db, passwordHasher);
            logger.LogInformation("[DB] 种子数据已插入（默认管理员）");
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
