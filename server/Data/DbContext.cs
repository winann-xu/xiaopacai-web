using Microsoft.EntityFrameworkCore;

namespace XiaopacaiWeb.Data;

/// <summary>
/// SQLCipher 数据库上下文 — P1 骨架（表结构见 Schema.sql）
/// P2 阶段完成 OnModelCreating 与 Entity 映射
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ========== DbSet 声明（P2 阶段创建对应 Entity 类） ==========

    // public DbSet<User> Users => Set<User>();
    // public DbSet<Device> Devices => Set<Device>();
    // public DbSet<Policy> Policies => Set<Policy>();
    // public DbSet<Announcement> Announcements => Set<Announcement>();
    // public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    // public DbSet<DailySummary> DailySummaries => Set<DailySummary>();
    // public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    // public DbSet<PairingInfo> PairingInfos => Set<PairingInfo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // P2 阶段：配置表名、索引、外键关系
    }
}
