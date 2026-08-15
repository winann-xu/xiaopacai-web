using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Models;

namespace XiaopacaiWeb.Data;

/// <summary>
/// SQLCipher 数据库上下文 — 11 实体表（9 原有 + diagnostics + relay_sessions）
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ========== 9 个 DbSet ==========
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<DailySummary> DailySummaries => Set<DailySummary>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PairingInfo> PairingInfos => Set<PairingInfo>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<DiagnosticRecord> Diagnostics => Set<DiagnosticRecord>();
    public DbSet<RelaySession> RelaySessions => Set<RelaySession>();
    // [TASK-PRELAUNCH-P3] 公告送达/回执
    public DbSet<AnnouncementDelivery> AnnouncementDeliveries => Set<AnnouncementDelivery>();
    // [TASK-ACCOUNT-V1-MAILCONFIG] 邮件发送配置（单行表）
    public DbSet<MailConfig> MailConfigs => Set<MailConfig>();
    // [TASK-MILESTONE-V3] B5 公告删除墓碑（客户端清除本地公告，见 docs/adr/）
    public DbSet<AnnouncementTombstone> AnnouncementTombstones => Set<AnnouncementTombstone>();
    // [TASK-MILESTONE-V3] 需求 14：客户端上传的运行日志（账号级归属，保留 7 天）
    public DbSet<AppLogEntry> AppLogEntries => Set<AppLogEntry>();
    // [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护失守事件 + 健康度快照（账号级归属：按设备归属校验）
    public DbSet<GuardEvent> GuardEvents => Set<GuardEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- Users ----
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Role).HasDefaultValue("parent");
            e.Property(u => u.IsActive).HasDefaultValue(true);
            e.Property(u => u.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(u => u.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- Devices ----
        modelBuilder.Entity<Device>(e =>
        {
            e.HasIndex(d => d.DeviceId).IsUnique();
            e.Property(d => d.PairStatus).HasDefaultValue("unpaired");
            e.Property(d => d.OnlineStatus).HasDefaultValue("offline");
            e.Property(d => d.Platform).HasDefaultValue("android");
            e.Property(d => d.IsActive).HasDefaultValue(true);
            e.Property(d => d.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(d => d.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- Policies ----
        modelBuilder.Entity<Policy>(e =>
        {
            e.HasIndex(p => p.DeviceId).IsUnique();
            e.Property(p => p.DailyLimitMinutes).HasDefaultValue(120);
            e.Property(p => p.CategoryGameLimit).HasDefaultValue(-1);
            e.Property(p => p.CategorySocialLimit).HasDefaultValue(-1);
            e.Property(p => p.CategoryVideoLimit).HasDefaultValue(-1);
            e.Property(p => p.CategoryLearningLimit).HasDefaultValue(-1);
            e.Property(p => p.OvertimeAction).HasDefaultValue("full_lock");
            e.Property(p => p.IsActive).HasDefaultValue(true);
            // [TASK-MILESTONE-V3] A2 服务端权威版本号（每次保存递增）
            e.Property(p => p.Version).HasDefaultValue(1);
            e.Property(p => p.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(p => p.Device)
             .WithOne(d => d.Policy)
             .HasForeignKey<Policy>(p => p.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- Announcements ----
        modelBuilder.Entity<Announcement>(e =>
        {
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.CreatedBy);
            e.Property(a => a.Priority).HasDefaultValue("normal");
            e.Property(a => a.Status).HasDefaultValue("draft");
            e.Property(a => a.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(a => a.UpdatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(a => a.Creator)
             .WithMany()
             .HasForeignKey(a => a.CreatedBy)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(a => a.TargetDevice)
             .WithMany()
             .HasForeignKey(a => a.TargetDeviceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ---- UsageRecords ----
        modelBuilder.Entity<UsageRecord>(e =>
        {
            e.HasIndex(r => new { r.DeviceId, r.StartTime });
            e.HasIndex(r => new { r.DeviceId, r.Category });
            e.Property(r => r.Category).HasDefaultValue("other");
            e.Property(r => r.DurationSeconds).HasDefaultValue(0);
            e.Property(r => r.IsBlocked).HasDefaultValue(false);
            e.Property(r => r.CreatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(r => r.Device)
             .WithMany(d => d.UsageRecords)
             .HasForeignKey(r => r.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- DailySummaries ----
        modelBuilder.Entity<DailySummary>(e =>
        {
            e.HasIndex(s => new { s.DeviceId, s.SummaryDate }).IsUnique();
            e.HasIndex(s => s.SummaryDate);
            e.Property(s => s.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(s => s.Device)
             .WithMany(d => d.DailySummaries)
             .HasForeignKey(s => s.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- AuditLogs ----
        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasIndex(l => new { l.UserId, l.CreatedAt });
            e.HasIndex(l => new { l.Action, l.CreatedAt });
            e.Property(l => l.CreatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(l => l.User)
             .WithMany()
             .HasForeignKey(l => l.UserId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ---- PairingInfos ----
        modelBuilder.Entity<PairingInfo>(e =>
        {
            e.HasIndex(p => new { p.DeviceId, p.PairStatus });
            e.Property(p => p.PairMethod).HasDefaultValue("manual");
            e.Property(p => p.PairStatus).HasDefaultValue("pending");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("datetime('now')");

            e.HasOne(p => p.Device)
             .WithMany(d => d.PairingInfos)
             .HasForeignKey(p => p.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- RefreshTokens ----
        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasIndex(rt => rt.TokenHash).IsUnique();
            e.HasIndex(rt => new { rt.UserId, rt.IsRevoked });
            e.Property(rt => rt.CreatedAt).HasDefaultValueSql("datetime('now')");
            e.Property(rt => rt.IsRevoked).HasDefaultValue(false);

            e.HasOne(rt => rt.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(rt => rt.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- SystemConfigs ----
        modelBuilder.Entity<SystemConfig>(e =>
        {
            e.Property(c => c.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- Diagnostics（OPT12 需求 5：故障诊断上报） ----
        modelBuilder.Entity<DiagnosticRecord>(e =>
        {
            e.HasIndex(d => new { d.DeviceId, d.ReportedAt });
            e.Property(d => d.ReportedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- RelaySessions（OPT12 需求 3：云端中继会话） ----
        modelBuilder.Entity<RelaySession>(e =>
        {
            e.HasIndex(s => new { s.DeviceId, s.Status });
            e.HasIndex(s => new { s.Status, s.ConnectedAt });
            e.Property(s => s.Role).HasDefaultValue("child");
            e.Property(s => s.Status).HasDefaultValue("connected");
            e.Property(s => s.ConnectedAt).HasDefaultValueSql("datetime('now')");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- MailConfig（[TASK-ACCOUNT-V1-MAILCONFIG] 单行表） ----
        modelBuilder.Entity<MailConfig>(e =>
        {
            e.Property(m => m.SmtpPort).HasDefaultValue(587);
            e.Property(m => m.SmtpUseSsl).HasDefaultValue(true);
        });

        // ---- AnnouncementTombstones（[TASK-MILESTONE-V3] B5 公告删除墓碑） ----
        modelBuilder.Entity<AnnouncementTombstone>(e =>
        {
            e.HasIndex(t => new { t.CreatedBy, t.DeletedAt });
            e.Property(t => t.DeletedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- AppLogEntries（[TASK-MILESTONE-V3] 需求 14：运行日志，账号级 + 7 天保留） ----
        // [TASK-HARDENING-V1.1.1] Bug3-A 根因修复：显式 ToTable("app_logs")，
        // 与 DataExtensions 建表 DDL 同名（此前 EF 默认按 DbSet 属性名查 AppLogEntries
        // 表 → "no such table" 500；存量库已存在 app_logs，此修复无需迁移、直接命中）。
        modelBuilder.Entity<AppLogEntry>(e =>
        {
            e.ToTable("app_logs");
            e.HasIndex(l => new { l.AccountId, l.ReceivedAt });
            e.HasIndex(l => l.ReceivedAt);
            e.Property(l => l.ReceivedAt).HasDefaultValueSql("datetime('now')");
        });

        // ---- GuardEvents（[TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护失守事件 + 健康度快照） ----
        // [Bug3 根因防御] 必须显式 ToTable("guard_events")：与 DataExtensions 建表 DDL 同名，
        // 否则 EF 默认按 DbSet 属性名建表（GuardEvents）导致"写入表 ≠ 查询表"（app_logs 曾踩此坑）。
        modelBuilder.Entity<GuardEvent>(e =>
        {
            e.ToTable("guard_events");
            e.HasIndex(g => new { g.DeviceId, g.ReceivedAt });
            e.Property(g => g.EventType).HasMaxLength(64);
            e.Property(g => g.Reason).HasMaxLength(128);
            e.Property(g => g.RestoredReason).HasMaxLength(128);
            e.Property(g => g.ReceivedAt).HasDefaultValueSql("datetime('now')");
        });
    }
}
