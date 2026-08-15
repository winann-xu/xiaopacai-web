-- ============================================================================
-- 小趴菜 Web 3.0 — SQLCipher 数据库 Schema
-- 版本：V3.0-MILESTONE-V3  日期：2026-08-15
-- 引擎：SQLite 3 + SQLCipher 加密扩展
-- 说明：对照 2.0 AppDatabase，新增管理后端表（audit_logs / pairing_info）；
--       OPT12 P1 新增：diagnostics / relay_sessions，devices 增加 owner_user_id / app_categories；
--       MILESTONE-V3 新增：policies.version（A2 乐观并发）、announcement_deliveries.compensated_at
--       （B2/B10 补偿重推）、announcement_tombstones（B5 公告删除墓碑）
-- ============================================================================

-- 启用外键约束
PRAGMA foreign_keys = ON;

-- ----------------------------------------------------------------------------
-- 1. users — 用户账号（家长 + 管理员）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    username        TEXT    NOT NULL UNIQUE,                -- 登录用户名
    password_hash   TEXT    NOT NULL,                       -- PBKDF2/Argon2 哈希
    password_salt   TEXT    NOT NULL,                       -- 哈希盐值
    display_name    TEXT    NOT NULL DEFAULT '',            -- 显示名称
    role            TEXT    NOT NULL DEFAULT 'parent'       -- 角色：admin / parent
                            CHECK(role IN ('admin', 'parent')),
    email           TEXT    DEFAULT NULL,                   -- 联系邮箱（可选）
    avatar_url      TEXT    DEFAULT NULL,                   -- 头像路径
    is_active       INTEGER NOT NULL DEFAULT 1,            -- 是否启用
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')), -- ISO8601 UTC
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    last_login_at   TEXT    DEFAULT NULL
);

-- ----------------------------------------------------------------------------
-- 2. devices — 儿童设备注册信息
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS devices (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_name     TEXT    NOT NULL,                       -- 设备名称（如 "小明手机"）
    device_id       TEXT    NOT NULL UNIQUE,                -- 设备唯一标识（Android 设备 ID）
    platform        TEXT    NOT NULL DEFAULT 'android',     -- 平台：android
    mac_address     TEXT    DEFAULT NULL,                   -- MAC 地址
    ip_address      TEXT    DEFAULT NULL,                   -- 最后已知 IP
    cert_fingerprint TEXT   DEFAULT NULL,                   -- TLS 证书 SHA256 指纹
    pair_code       TEXT    DEFAULT NULL,                   -- 配对码（6 位，配对后保存）
    pair_status     TEXT    NOT NULL DEFAULT 'unpaired'     -- 配对状态
                            CHECK(pair_status IN ('unpaired', 'paired', 'revoked')),
    online_status   TEXT    NOT NULL DEFAULT 'offline'      -- 在线状态
                            CHECK(online_status IN ('online', 'offline', 'reconnecting')),
    last_seen_at    TEXT    DEFAULT NULL,                   -- 最后在线时间
    -- OPT12 需求 1/3：应用分类配置（JSON 数组）+ 绑定家长账号（配对确认时绑定）
    owner_user_id   TEXT    DEFAULT NULL,                   -- 绑定家长账号（用户 ID 字符串，可空）
    app_categories  TEXT    DEFAULT NULL,                   -- 应用分类 JSON 数组 [{packageName,appName,category}]
    pending_reset_at TEXT   DEFAULT NULL,                   -- 待下发的每日限额重置时间（UTC ISO8601）
    is_active       INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- ----------------------------------------------------------------------------
-- 3. policies — 策略配置（每设备一条）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS policies (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id           INTEGER NOT NULL UNIQUE,            -- 关联设备
    daily_limit_minutes INTEGER NOT NULL DEFAULT 120,       -- 每日使用限额（30~480 分钟）
    bedtime_start       TEXT    DEFAULT NULL,               -- 就寝开始（HH:mm）
    bedtime_end         TEXT    DEFAULT NULL,               -- 就寝结束（HH:mm）
    -- 分类限额（分钟/天，-1 表示不限制）
    category_game_limit     INTEGER NOT NULL DEFAULT -1,
    category_social_limit   INTEGER NOT NULL DEFAULT -1,
    category_video_limit    INTEGER NOT NULL DEFAULT -1,
    category_learning_limit INTEGER NOT NULL DEFAULT -1,
    -- 黑白名单
    whitelist_apps      TEXT    DEFAULT NULL,               -- JSON 数组：["com.example.app"]
    blacklist_apps      TEXT    DEFAULT NULL,               -- JSON 数组
    -- 超时处理方式
    overtime_action     TEXT    NOT NULL DEFAULT 'full_lock' -- 整机停用/部分APP停用/仅提醒
                                CHECK(overtime_action IN ('full_lock', 'partial_lock', 'warn_only')),
    is_active           INTEGER NOT NULL DEFAULT 1,
    version             INTEGER NOT NULL DEFAULT 1,         -- [MILESTONE-V3 A2] 服务端权威版本（每次保存 +1）
    created_at          TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

-- ----------------------------------------------------------------------------
-- 4. announcements — 公告管理
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS announcements (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    title           TEXT    NOT NULL,
    content         TEXT    NOT NULL,
    priority        TEXT    NOT NULL DEFAULT 'normal'       -- 优先级
                            CHECK(priority IN ('normal', 'important', 'urgent')),
    status          TEXT    NOT NULL DEFAULT 'draft'        -- 状态
                            CHECK(status IN ('draft', 'published', 'revoked')),
    target_device_id INTEGER DEFAULT NULL,                  -- 定向设备（NULL=全部设备）
    valid_from      TEXT    DEFAULT NULL,                   -- 有效期开始
    valid_until     TEXT    DEFAULT NULL,                   -- 有效期结束
    published_at    TEXT    DEFAULT NULL,
    revoked_at      TEXT    DEFAULT NULL,
    created_by      INTEGER NOT NULL,                       -- 发布者 user.id
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (created_by) REFERENCES users(id),
    FOREIGN KEY (target_device_id) REFERENCES devices(id) ON DELETE SET NULL
);

-- ----------------------------------------------------------------------------
-- 5. usage_records — 使用记录（原始数据）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS usage_records (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       INTEGER NOT NULL,
    app_package     TEXT    NOT NULL DEFAULT '',            -- 应用包名
    app_name        TEXT    NOT NULL DEFAULT '',            -- 应用显示名
    category        TEXT    NOT NULL DEFAULT 'other'        -- 分类
                            CHECK(category IN ('game', 'social', 'video', 'learning', 'other')),
    start_time      TEXT    NOT NULL,                       -- 开始时间 ISO8601
    end_time        TEXT    DEFAULT NULL,                   -- 结束时间（进行中则为 NULL）
    duration_seconds INTEGER DEFAULT 0,                    -- 累计时长（秒）
    is_blocked      INTEGER NOT NULL DEFAULT 0,            -- 是否被拦截
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

-- 查询优化索引
CREATE INDEX IF NOT EXISTS idx_usage_records_device_time
    ON usage_records(device_id, start_time);
CREATE INDEX IF NOT EXISTS idx_usage_records_category
    ON usage_records(device_id, category);

-- ----------------------------------------------------------------------------
-- 6. daily_summary — 每日汇总（聚合数据）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS daily_summary (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       INTEGER NOT NULL,
    summary_date    TEXT    NOT NULL,                       -- 日期 YYYY-MM-DD
    total_minutes   INTEGER NOT NULL DEFAULT 0,            -- 当日总使用（分钟）
    game_minutes    INTEGER NOT NULL DEFAULT 0,
    social_minutes  INTEGER NOT NULL DEFAULT 0,
    video_minutes   INTEGER NOT NULL DEFAULT 0,
    learning_minutes INTEGER NOT NULL DEFAULT 0,
    other_minutes   INTEGER NOT NULL DEFAULT 0,
    overtime_count  INTEGER NOT NULL DEFAULT 0,            -- 超时停用次数
    block_count     INTEGER NOT NULL DEFAULT 0,            -- 拦截次数
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE,
    UNIQUE(device_id, summary_date)                        -- 每设备每天一条
);

CREATE INDEX IF NOT EXISTS idx_daily_summary_date
    ON daily_summary(summary_date);

-- ----------------------------------------------------------------------------
-- 7. audit_logs — 审计日志（管理后端）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_logs (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         INTEGER DEFAULT NULL,                  -- 操作者（NULL=系统自动）
    action          TEXT    NOT NULL,                       -- 操作类型：login/logout/policy_change/announcement_publish/announcement_revoke/data_export/account_manage/system_config
    target_type     TEXT    DEFAULT NULL,                   -- 操作对象类型：user/device/policy/announcement/system
    target_id       INTEGER DEFAULT NULL,                  -- 操作对象 ID
    detail          TEXT    DEFAULT NULL,                   -- 操作详情（JSON）
    ip_address      TEXT    DEFAULT NULL,                   -- 操作来源 IP
    user_agent      TEXT    DEFAULT NULL,                   -- 浏览器/客户端标识
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_audit_logs_user_time
    ON audit_logs(user_id, created_at);
CREATE INDEX IF NOT EXISTS idx_audit_logs_action
    ON audit_logs(action, created_at);

-- ----------------------------------------------------------------------------
-- 8. pairing_info — 配对信息（发现与配对过程记录）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pairing_info (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       INTEGER DEFAULT NULL,                   -- 关联设备（NULL=尚未分配，避免 FK 约束失败）
    pair_code       TEXT    NOT NULL,                       -- 6 位配对码
    pair_method     TEXT    NOT NULL DEFAULT 'manual'       -- 配对方式：scan / manual_ip / broadcast
                            CHECK(pair_method IN ('scan', 'manual_ip', 'broadcast')),
    discovery_data  TEXT    DEFAULT NULL,                   -- mDNS/UDP 发现数据（JSON）
    tls_fingerprint TEXT    DEFAULT NULL,                   -- 证书指纹（首次握手记录）
    pair_status     TEXT    NOT NULL DEFAULT 'pending'      -- 状态
                            CHECK(pair_status IN ('pending', 'confirmed', 'expired', 'rejected')),
    expires_at      TEXT    NOT NULL,                       -- 配对码过期时间
    confirmed_at    TEXT    DEFAULT NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_pairing_info_device
    ON pairing_info(device_id, pair_status);

-- ----------------------------------------------------------------------------
-- 9. diagnostics — 儿童端故障诊断记录（OPT12 需求 5）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS diagnostics (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id           TEXT    NOT NULL,                       -- 儿童端设备唯一标识
    app_version         TEXT    DEFAULT NULL,                   -- 儿童端 APP 版本
    android_version     TEXT    DEFAULT NULL,                   -- Android 系统版本
    device_model        TEXT    DEFAULT NULL,                   -- 设备型号
    manufacturer        TEXT    DEFAULT NULL,                   -- 设备厂商
    permission_status   TEXT    DEFAULT NULL,                   -- 权限状态（JSON）
    service_status      TEXT    DEFAULT NULL,                   -- 服务运行状态（JSON）
    recent_crashes      TEXT    DEFAULT NULL,                   -- 最近崩溃堆栈（JSON，最近 5 条）
    p2p_history         TEXT    DEFAULT NULL,                   -- P2P 连接历史（JSON）
    db_size_bytes       INTEGER DEFAULT NULL,                   -- 本地数据库大小（字节）
    network_type        TEXT    DEFAULT NULL,                   -- 网络状态：wifi/cellular/none
    reported_at         TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (device_id) REFERENCES devices(device_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_diagnostics_device_time
    ON diagnostics(device_id, reported_at);

-- ----------------------------------------------------------------------------
-- 10. relay_sessions — 云端中继会话记录（OPT12 需求 3）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS relay_sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       TEXT    NOT NULL,                       -- 连接方设备唯一标识
    role            TEXT    NOT NULL DEFAULT 'child'        -- 角色：child / parent
                            CHECK(role IN ('child', 'parent')),
    user_id         INTEGER DEFAULT NULL,                   -- 关联家长账号（可空）
    ip_address      TEXT    DEFAULT NULL,                   -- 连接来源 IP
    status          TEXT    NOT NULL DEFAULT 'connected'    -- 状态：connected / disconnected
                            CHECK(status IN ('connected', 'disconnected')),
    connected_at    TEXT    NOT NULL DEFAULT (datetime('now')),
    disconnected_at TEXT    DEFAULT NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_relay_sessions_device
    ON relay_sessions(device_id, status);
CREATE INDEX IF NOT EXISTS idx_relay_sessions_status
    ON relay_sessions(status, connected_at);

-- ----------------------------------------------------------------------------
-- 11. mail_config — 邮件发送配置（单行表，[TASK-ACCOUNT-V1-MAILCONFIG]）
--      Channel='api' → 阿里云 DirectMail API；'smtp' → 自备 SMTP；'' → 未配置
--      Secret 字段（*Enc）为服务端主密钥 AES-256-GCM 密文，禁止明文
--      （列名与 EF 模型一致；运行时由 EnsureMissingTablesAsync 补齐）
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mail_config (
    Id                  INTEGER NOT NULL PRIMARY KEY,       -- 恒为 1
    Channel             TEXT    NOT NULL DEFAULT ''         -- api / smtp / ''
                               CHECK(Channel IN ('api', 'smtp', '')),
    AccessKeyId         TEXT    NOT NULL DEFAULT '',        -- DirectMail RAM AccessKey ID（非机密）
    AccessKeySecretEnc  TEXT    NOT NULL DEFAULT '',        -- RAM AccessKey Secret（AES-GCM 密文）
    FromAddress         TEXT    NOT NULL DEFAULT '',        -- 发信地址（两通道共用）
    FromName            TEXT    NOT NULL DEFAULT '',        -- 发信人显示名
    SmtpHost            TEXT    NOT NULL DEFAULT '',
    SmtpPort            INTEGER NOT NULL DEFAULT 587,
    SmtpUser            TEXT    NOT NULL DEFAULT '',
    SmtpPasswordEnc     TEXT    NOT NULL DEFAULT '',        -- SMTP 密码（AES-GCM 密文）
    SmtpUseSsl          INTEGER NOT NULL DEFAULT 1,
    LastTestOk          INTEGER DEFAULT NULL,               -- 最近一次测试发送结果
    LastTestDetail      TEXT    DEFAULT NULL,
    LastTestAt          TEXT    DEFAULT NULL,
    UpdatedAt           TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- ============================================================================
-- 12. announcement_tombstones — 公告删除墓碑（[TASK-MILESTONE-V3] B5）
--      删除公告时落一行，客户端 7 天内重连同步清除本地残留（保留 7 天到期清理）
-- ============================================================================
CREATE TABLE IF NOT EXISTS announcement_tombstones (
    AnnouncementId  INTEGER NOT NULL PRIMARY KEY,           -- 被删除公告 id
    CreatedBy       INTEGER NOT NULL,                       -- 公告创建者（账号归属过滤）
    DeletedAt       TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_announcement_tombstones_CreatedBy_DeletedAt
    ON announcement_tombstones (CreatedBy, DeletedAt);

-- 注：announcement_deliveries 在 [TASK-PRELAUNCH-P3] 引入（见 EnsureMissingTablesAsync），
--     [TASK-MILESTONE-V3] B2/B10 新增 CompensatedAt 列：
--     ALTER TABLE announcement_deliveries ADD COLUMN CompensatedAt TEXT NULL
--     （60 秒未 displayed 补偿重推打标，幂等不重复推）

-- ============================================================================
-- [TASK-ACCOUNT-V1] 不再播种 admin123/parent123 种子账号。
-- 首次启动引导由环境变量 ADMIN_EMAIL + ADMIN_INITIAL_PASSWORD 完成
-- （应用层 BootstrapAdminFromEnvAsync，MustChangePassword=true）。
-- ============================================================================
