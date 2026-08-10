-- ============================================================================
-- 小趴菜 Web 3.0 — SQLCipher 数据库 Schema
-- 版本：V3.0-P1    日期：2026-08-10
-- 引擎：SQLite 3 + SQLCipher 加密扩展
-- 说明：对照 2.0 AppDatabase，新增管理后端表（audit_logs / pairing_info）
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
    device_id       INTEGER NOT NULL,
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

-- ============================================================================
-- 种子数据（P1 骨架：默认管理员账号）
-- 密码 "admin123" → PBKDF2 哈希（P2 阶段由应用层生成实际哈希）
-- ============================================================================
INSERT OR IGNORE INTO users (username, password_hash, password_salt, display_name, role)
VALUES ('admin', 'PLACEHOLDER_HASH_P2_STAGE', 'PLACEHOLDER_SALT_P2_STAGE', '管理员', 'admin');
