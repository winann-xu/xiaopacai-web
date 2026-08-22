using Microsoft.EntityFrameworkCore;
using XiaopacaiWeb.Models;
using XiaopacaiWeb.Security;
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

        // 3. [TASK-ACCOUNT-V1] 管理员邮箱引导（仅当 users 表为空时）：
        // 不再播种 admin123/parent123 种子账号；未配置 ADMIN_EMAIL/ADMIN_INITIAL_PASSWORD
        // 时拒绝创建（安全优先，宁可无法登录也不落默认口令）
        if (!await db.Users.AnyAsync())
        {
            await BootstrapAdminFromEnvAsync(db, passwordHasher, logger);
        }

        // 3.1 [TASK-ACCOUNT-V1] 孤儿设备启动迁移：OwnerUserId 为空的已绑定设备
        // 强制回到 unpaired（清除 PairCode），杜绝无归属设备悬挂占用（A5 归属纪律）
        await CleanupOrphanDevicesAsync(db, logger);

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

        // diagnostics 表（OPT12 需求 5：故障诊断上报）
        var hasDiagnostics = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='diagnostics'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasDiagnostics)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "diagnostics" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "DeviceId" TEXT NOT NULL,
                    "AppVersion" TEXT NULL,
                    "AndroidVersion" TEXT NULL,
                    "DeviceModel" TEXT NULL,
                    "Manufacturer" TEXT NULL,
                    "PermissionStatus" TEXT NULL,
                    "ServiceStatus" TEXT NULL,
                    "RecentCrashes" TEXT NULL,
                    "P2pHistory" TEXT NULL,
                    "DbSizeBytes" INTEGER NULL,
                    "NetworkType" TEXT NULL,
                    "ReportedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_diagnostics_DeviceId_ReportedAt"
                    ON "diagnostics" ("DeviceId", "ReportedAt");
                """);
            logger.LogInformation("[DB] 已补齐 diagnostics 表");
        }

        // relay_sessions 表（OPT12 需求 3：云端中继会话）
        var hasRelay = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='relay_sessions'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasRelay)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "relay_sessions" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "DeviceId" TEXT NOT NULL,
                    "Role" TEXT NOT NULL DEFAULT 'child',
                    "UserId" INTEGER NULL,
                    "IpAddress" TEXT NULL,
                    "Status" TEXT NOT NULL DEFAULT 'connected',
                    "ConnectedAt" TEXT NOT NULL DEFAULT (datetime('now')),
                    "DisconnectedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_relay_sessions_DeviceId_Status"
                    ON "relay_sessions" ("DeviceId", "Status");
                CREATE INDEX IF NOT EXISTS "IX_relay_sessions_Status_ConnectedAt"
                    ON "relay_sessions" ("Status", "ConnectedAt");
                """);
            logger.LogInformation("[DB] 已补齐 relay_sessions 表");
        }

        // [TASK-PRELAUNCH-P3] 公告送达/回执表（见 docs/adr/0004）
        var hasDeliveries = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='announcement_deliveries'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasDeliveries)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "announcement_deliveries" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "AnnouncementId" INTEGER NOT NULL,
                    "DeviceId" INTEGER NOT NULL,
                    "PushCount" INTEGER NOT NULL DEFAULT 0,
                    "LastPushedAt" TEXT NULL,
                    "DisplayedAt" TEXT NULL,
                    "AcknowledgedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL DEFAULT (datetime('now')),
                    "UpdatedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "idx_deliveries_ann_device"
                    ON "announcement_deliveries" ("AnnouncementId", "DeviceId");
                """);
            logger.LogInformation("[DB] 已补齐 announcement_deliveries 表");
        }

        // devices 表补列（已存在库 EnsureCreated 不补新列；列已存在时忽略异常）
        await TryAddColumnAsync(db, "devices", "app_categories", "TEXT NULL", logger);
        await TryAddColumnAsync(db, "devices", "owner_user_id", "TEXT NULL", logger);
        // [TASK-OPT-12-P4-DEEPEN] 设备级访问令牌（诊断上报鉴权）
        await TryAddColumnAsync(db, "devices", "device_token", "TEXT NULL", logger);
        // [REQ] 每日限额重置：离线时挂起待下发，重连握手补推
        await TryAddColumnAsync(db, "devices", "PendingResetAt", "TEXT NULL", logger);
        // [REQ] 配对码归属账号：扫码/中继绑定时写入设备 owner
        await TryAddColumnAsync(db, "pairing_info", "OwnerUserId", "TEXT NULL", logger);
        // [TASK-PRELAUNCH-P3] 公告去重字段（发布代数/内容哈希）
        await TryAddColumnAsync(db, "announcements", "Version", "INTEGER NOT NULL DEFAULT 0", logger);
        await TryAddColumnAsync(db, "announcements", "ContentHash", "TEXT NOT NULL DEFAULT ''", logger);
        // [TASK-PRELAUNCH-P4] 时间额度口径：重置偏移/偏移日期/最近上报时间
        await TryAddColumnAsync(db, "devices", "LastResetOffsetMinutes", "INTEGER NOT NULL DEFAULT 0", logger);
        await TryAddColumnAsync(db, "devices", "LastResetDate", "TEXT NULL", logger);
        await TryAddColumnAsync(db, "devices", "LastReportAt", "TEXT NULL", logger);
        // [FIX-100] 儿童端上报的调整后今日已用（优先展示口径）
        await TryAddColumnAsync(db, "devices", "TodayAdjustedMinutes", "INTEGER NULL", logger);
        // [SEC-K2] 中继会话令牌（家长端 P2P 握手凭据，/api/relay/register 签发后轮换）
        await TryAddColumnAsync(db, "relay_sessions", "SessionToken", "TEXT NULL", logger);
        // [SEC-K2] 注册时绑定的客户端证书指纹（P2P 握手与 TLS 对端指纹比对）
        await TryAddColumnAsync(db, "relay_sessions", "Fingerprint", "TEXT NULL", logger);
        // [SEC-P1] 强制改密标记（种子账号/管理员重置口令后置 true）
        await TryAddColumnAsync(db, "users", "MustChangePassword", "INTEGER NOT NULL DEFAULT 0", logger);
        // [TASK-MILESTONE-V3] A2 策略服务端权威版本号（保存递增；既有行按 1 起步）
        await TryAddColumnAsync(db, "policies", "Version", "INTEGER NOT NULL DEFAULT 1", logger);
        // [TASK-MILESTONE-V3] B2/B10 公告补偿重推时间（60 秒未 displayed 补推一次后打标）
        await TryAddColumnAsync(db, "announcement_deliveries", "CompensatedAt", "TEXT NULL", logger);

        // [TASK-MILESTONE-V3] B5 公告删除墓碑表（客户端清除本地公告，保留 7 天）
        var hasTombstones = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='announcement_tombstones'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasTombstones)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "announcement_tombstones" (
                    "AnnouncementId" INTEGER NOT NULL CONSTRAINT "PK_announcement_tombstones" PRIMARY KEY,
                    "CreatedBy" INTEGER NOT NULL,
                    "DeletedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_announcement_tombstones_CreatedBy_DeletedAt"
                    ON "announcement_tombstones" ("CreatedBy", "DeletedAt");
                """);
            logger.LogInformation("[DB] 已补齐 announcement_tombstones 表");
        }

        // [TASK-MILESTONE-V3] 需求 14：客户端运行日志表（账号级归属，保留 7 天）
        var hasAppLogs = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='app_logs'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasAppLogs)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "app_logs" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "AccountId" INTEGER NOT NULL,
                    "Level" TEXT NOT NULL DEFAULT 'info',
                    "Tag" TEXT NOT NULL DEFAULT '',
                    "Message" TEXT NOT NULL DEFAULT '',
                    "Client" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL DEFAULT (datetime('now')),
                    "ReceivedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_app_logs_AccountId_ReceivedAt"
                    ON "app_logs" ("AccountId", "ReceivedAt");
                CREATE INDEX IF NOT EXISTS "IX_app_logs_ReceivedAt"
                    ON "app_logs" ("ReceivedAt");
                """);
            logger.LogInformation("[DB] 已补齐 app_logs 表");
        }

        // [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护失守事件 + 健康度快照表
        // [Bug3 根因防御] 表名/列名必须与 AppDbContext OnModelCreating 中
        // GuardEvent 的 ToTable("guard_events") 映射完全一致，否则写入/查询分表。
        var hasGuardEvents = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='guard_events'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasGuardEvents)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "guard_events" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "DeviceId" TEXT NOT NULL,
                    "EventType" TEXT NOT NULL,
                    "StartedAt" INTEGER NULL,
                    "EndedAt" INTEGER NULL,
                    "DurationSeconds" INTEGER NULL,
                    "Reason" TEXT NULL,
                    "RestoredReason" TEXT NULL,
                    "WasEnforcing" INTEGER NOT NULL DEFAULT 0,
                    "HealthJson" TEXT NULL,
                    "ReceivedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_guard_events_DeviceId_ReceivedAt"
                    ON "guard_events" ("DeviceId", "ReceivedAt");
                """);
            logger.LogInformation("[DB] 已补齐 guard_events 表");
        }

        // [TASK-APP-UPDATE-V1] App 更新清单表（表名/列名与 AppUpdate 的 ToTable("app_updates") 映射一致）
        var hasAppUpdates = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='app_updates'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasAppUpdates)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "app_updates" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "Platform" TEXT NOT NULL DEFAULT 'android',
                    "VersionName" TEXT NOT NULL,
                    "VersionCode" INTEGER NOT NULL,
                    "MinVersionCode" INTEGER NOT NULL,
                    "AbiUrls" TEXT NOT NULL DEFAULT '',
                    "AbiSha256" TEXT NOT NULL DEFAULT '',
                    "SizeBytes" INTEGER NOT NULL DEFAULT 0,
                    "Changelog" TEXT NOT NULL DEFAULT '',
                    "Status" TEXT NOT NULL DEFAULT 'draft',
                    "Channel" TEXT NOT NULL DEFAULT 'stable',
                    "PublishedAt" TEXT NULL,
                    "CreatedBy" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL DEFAULT (datetime('now')),
                    "UpdatedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS "IX_app_updates_Platform_Status"
                    ON "app_updates" ("Platform", "Status");
                CREATE INDEX IF NOT EXISTS "IX_app_updates_VersionCode"
                    ON "app_updates" ("VersionCode");
                """);
            logger.LogInformation("[DB] 已补齐 app_updates 表");
        }

        // [SEC-P1] 清理 RefreshTokens 明文列：历史行置空（验证仅走 TokenHash），
        // 防止库文件被窃后明文 token 直接可用（红线 R4.3）
        await PurgePlaintextRefreshTokensAsync(db, logger);

        // [FIX-100] usage_records 去重迁移：P4 前历史重复行导致 raw SUM 虚高。
        // 按 (DeviceId, AppPackage, 日期) 保留 Id 最大（最新）一条，删除其余，再建唯一索引防复发。
        await DedupUsageRecordsAsync(db, logger);

        // [TASK-ACCOUNT-V1-MAILCONFIG] 邮件配置单行表
        var hasMailConfig = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='mail_config'"
        ).FirstOrDefaultAsync() > 0;
        if (!hasMailConfig)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "mail_config" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_mail_config" PRIMARY KEY,
                    "Channel" TEXT NOT NULL DEFAULT '',
                    "AccessKeyId" TEXT NOT NULL DEFAULT '',
                    "AccessKeySecretEnc" TEXT NOT NULL DEFAULT '',
                    "FromAddress" TEXT NOT NULL DEFAULT '',
                    "FromName" TEXT NOT NULL DEFAULT '',
                    "SmtpHost" TEXT NOT NULL DEFAULT '',
                    "SmtpPort" INTEGER NOT NULL DEFAULT 587,
                    "SmtpUser" TEXT NOT NULL DEFAULT '',
                    "SmtpPasswordEnc" TEXT NOT NULL DEFAULT '',
                    "SmtpUseSsl" INTEGER NOT NULL DEFAULT 1,
                    "LastTestOk" INTEGER NULL,
                    "LastTestDetail" TEXT NULL,
                    "LastTestAt" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """);
            logger.LogInformation("[DB] 已补齐 mail_config 表");
        }
    }

    /// <summary>
    /// [SEC-P1] 启动时清空 RefreshTokens.Token 明文列（历史数据一次性清理，此后不再写入）
    /// </summary>
    private static async Task PurgePlaintextRefreshTokensAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            var purged = await db.Database.ExecuteSqlRawAsync(
                """UPDATE "RefreshTokens" SET "Token" = '' WHERE "Token" IS NOT NULL AND "Token" != ''""");
            if (purged > 0)
                logger.LogWarning("[DB][SEC] 已清理 {Purged} 行 RefreshToken 明文（验证仅用 TokenHash）", purged);
        }
        catch (Exception ex)
        {
            // 失败不阻断启动：新写入路径已不再落明文
            logger.LogWarning(ex, "[DB][SEC] RefreshToken 明文清理失败（不阻断启动）");
        }
    }

    /// <summary>
    /// [FIX-100] usage_records 按 (DeviceId, AppPackage, StartTime 日期) 去重：
    /// 保留每组 Id 最大（最新）一行，删除历史重复行；随后创建唯一索引防止再次出现重复。
    /// StartTime 以 TEXT 存储（yyyy-MM-dd HH:mm:ss…），取前 10 位即日期。
    /// </summary>
    private static async Task DedupUsageRecordsAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM "usage_records"
                WHERE "Id" NOT IN (
                    SELECT MAX("Id")
                    FROM "usage_records"
                    GROUP BY "DeviceId", "AppPackage", substr("StartTime", 1, 10)
                )
                """);
            if (deleted > 0)
                logger.LogWarning("[DB][FIX-100] usage_records 清理历史重复行 {Deleted} 条", deleted);

            // 唯一索引（表达式索引，SQLite 支持）：同设备同包名同日期仅一行
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "idx_usage_records_device_package_date"
                ON "usage_records" ("DeviceId", "AppPackage", substr("StartTime", 1, 10))
                """);
            logger.LogInformation("[DB][FIX-100] usage_records 唯一索引已就绪（DeviceId, AppPackage, 日期）");
        }
        catch (Exception ex)
        {
            // 失败不阻断启动：重复防护仍由应用层 upsert 保证
            logger.LogWarning(ex, "[DB][FIX-100] usage_records 去重/索引处理失败（不阻断启动）");
        }
    }

    /// <summary>
    /// 尝试给表补列，列已存在时静默忽略
    /// </summary>
    private static async Task TryAddColumnAsync(
        AppDbContext db, string table, string column, string ddl, ILogger logger)
    {
        try
        {
            // 表名/列名均为内部常量（非用户输入），EF1002 可安全抑制
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl}");
#pragma warning restore EF1002
            logger.LogInformation("[DB] {Table} 表已补 {Column} 列", table, column);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column"))
        {
            // 列已存在，忽略
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DB] 补列失败 {Table}.{Column}", table, column);
        }
    }

    /// <summary>
    /// [TASK-ACCOUNT-V1] A1 管理员邮箱引导（users 表为空时）：
    /// 环境变量 ADMIN_EMAIL + ADMIN_INITIAL_PASSWORD 均配置才创建 admin 账号
    /// （Username=邮箱、MustChangePassword=true）；未配置则拒绝创建并输出明确指引
    /// （安全优先：不再播种 admin123/parent123 默认口令账号）。
    /// </summary>
    private static async Task BootstrapAdminFromEnvAsync(
        AppDbContext db, IPasswordHasher hasher, ILogger logger)
    {
        var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL")?.Trim().ToLower();
        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_INITIAL_PASSWORD");

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning(
                "[DB][SEC] users 表为空且未配置 ADMIN_EMAIL / ADMIN_INITIAL_PASSWORD，" +
                "拒绝创建默认账号（安全优先）。请配置后重启完成管理员引导。");
            return;
        }

        var policyError = PasswordPolicy.Validate(adminPassword);
        if (policyError != null)
        {
            logger.LogWarning("[DB][SEC] ADMIN_INITIAL_PASSWORD 不满足密码策略（{Err}），拒绝创建管理员账号", policyError);
            return;
        }

        var (hash, salt) = hasher.HashPassword(adminPassword);
        db.Users.Add(new User
        {
            Username = adminEmail,
            Email = adminEmail,
            PasswordHash = hash,
            PasswordSalt = salt,
            DisplayName = "管理员",
            Role = "admin",
            IsActive = true,
            // [SEC-P1] 初始口令由环境变量下发，首次登录强制改密（红线 R4.2）
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("[DB] 管理员账号已按 ADMIN_EMAIL 引导创建: {Email}", adminEmail);
    }

    /// <summary>
    /// [TASK-ACCOUNT-V1] A5 孤儿设备启动迁移：OwnerUserId 为空且 PairStatus != unpaired
    /// 的设备回到 unpaired 并清除 PairCode（无归属设备不得保持绑定态，杜绝悬挂占用）。
    /// </summary>
    private static async Task CleanupOrphanDevicesAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            var orphans = await db.Devices
                .Where(d => (d.OwnerUserId == null || d.OwnerUserId == "") && d.PairStatus != "unpaired")
                .ToListAsync();
            if (orphans.Count == 0)
                return;

            foreach (var d in orphans)
            {
                d.PairStatus = "unpaired";
                d.PairCode = null;
                d.UpdatedAt = DateTime.UtcNow;
                db.AuditLogs.Add(new AuditLog
                {
                    Action = "orphan_device_cleanup",
                    TargetType = "Device",
                    TargetId = d.Id,
                    Detail = $"{{\"deviceId\":\"{d.DeviceId}\",\"prevStatus\":\"paired/revoked\"}}",
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
            logger.LogWarning("[DB][ACCOUNT-V1] 孤儿设备清理：{Count} 台无归属设备已重置为 unpaired", orphans.Count);
        }
        catch (Exception ex)
        {
            // 失败不阻断启动（审计无归属状态兜底仍在 API 层）
            logger.LogWarning(ex, "[DB][ACCOUNT-V1] 孤儿设备清理失败（不阻断启动）");
        }
    }
}
