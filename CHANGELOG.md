# 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [1.3.0] — 2026-08-24（[TASK-UPDATE-CHANNEL]，特别版独立渠道）

> 为 ColorOS 等限制机型增加「特别版（special）」独立分发渠道，与正式版（stable）在
> 检查、下载、推送、升级四环节全部隔离；自动升级与服务器推送绝不跨渠道串线。

### 新增
- `/api/update/check` 支持 `channel`（stable 缺省 / special），仅在本渠道内返回最新版本；
  响应新增 `channel` 字段；非法渠道返回 400
- admin 新建版本可选择渠道（stable/special），防降级按渠道内比较；发布广播的
  `update_available` 载荷携带 `channel`
- 下载中心新增「特别版（限制机型专用）」独立卡片（独立查询、独立下载、签名切换警告）
- admin 更新列表展示渠道标签

### 配套客户端（xiaopacai android）
- 构建期内建 `BuildConfig.UPDATE_CHANNEL`（release/debug=stable，strictTestkey=special），
  检查请求携带本机渠道
- 安装前新增签名一致性自检：更新包签名证书与本机不一致即拒绝安装（渠道隔离兜底）
- 服务端推送携带 channel 时与本机渠道比对，不一致直接忽略

### 上线数据
- 特别版 v1.3.3-testkey（versionCode 10304，channel=special）三 ABI 已上传并发布
  （SHA-256：arm64 78414f7d… / v7a c0eb3bf7… / x86_64 4907e571…）

### 单测
- 308 → 312（渠道隔离 +4：stable/special 互不串线、缺省 channel、非法 channel 400）

---

## [1.2.0] — 2026-08-23（[TASK-APP-UPDATE-V1]，交付）

> 自动更新闭环首版：服务端更新清单 + admin 发布推送 + 公开检查接口 + 清单驱动下载中心。

### 新增
- `app_updates` 表（platform/versionName/versionCode/minVersionCode/ABI URL+SHA-256/changelog/status/审计）
- `GET /api/update/check`（公开 + IP 限频 120 次/时；versionCode=0 下载中心语义返回最新已发布版本；
  force = minVersionCode > 当前版本；缺当前 ABI 返回 abiMissing）
- admin「App 更新」管理页：新建草稿 / 按 ABI 上传（服务端流式 SHA-256）/ 发布并推送（P2P 广播
  `update_available` + 审计）；防降级（versionCode 单调递增）；回滚=发布更大版本
- 下载中心由静态页升级为更新清单驱动（版本号/下载入口/changelog 随清单）
- 运维文档 docs/app-update-v1.md（发布 SOP/回滚/限频/边界）

### 修复
- 下载中心 versionCode=0 查询被服务端 400 拒绝 → 按文档语义放行（0 = 取最新已发布版本）

### 部署注意事项
- Nginx 反代需 `client_max_body_size` ≥ 200m（默认 1MB 会挡 APK 上传，已上线配置）

### 单测
- 306 → 308（AppUpdateTableMappingTests +2：versionCode=0 返回最新 / 当前不低于最新无更新）

---

## [1.1.1] — 2026-08-15（[TASK-HARDENING-V1.1.1]，交付）

## [1.1.1] — 2026-08-15（[TASK-HARDENING-V1.1.1]，交付）

> V1.1.1 加固版：修复 OPPO 真机回归 4 项 P0 缺陷（140 信任务书）。架构裁决见
> 双仓库 ADR 0016（xiaopacai/android/docs/adr/0016）。

### 修复
- **Bug3-A 查日志 "no such table" 500（根因修复）**：AppLogEntry 显式 `ToTable("app_logs")`，
  与 DataExtensions 建表 DDL 同名（此前 EF 按 DbSet 属性名查 AppLogEntries 表，
  写入/查询分表）；存量库零迁移；真实 SQLite 表名一致性回归单测（存量库 DDL 路径 +
  EnsureCreated 新库路径）
- **Bug3-C 日志页补「接收时间」列**：家长可确认客户端日志是否在持续上传

### 新增
- 守护失守事件与健康度（Bug1-D/1-B 服务端 + Web 展示）
  - `guard_events` 表（显式 ToTable，与 DDL 同名）+ (DeviceId, ReceivedAt) 索引
  - POST /api/guard-events（批量上传，校验+限速 60 次/时/账号）、
    GET /api/guard-events?deviceId=&limit=（本账号隔离，admin 可全量/过滤）、
    GET /api/guard-events/health?deviceId=（最新健康度快照）
  - 设备页守护健康徽章（失效红标/健康分）+ 详情弹窗（6 项检查勾叉、失守历史表格）
- 单测 294 → 303（guard_events 7、app_logs 表名一致性 2，含真实 SQLite 回归）

---

## [1.1.0] — 2026-08-15（[TASK-MILESTONE-V3]，交付）

> 版本号自本期起按 `docs/VERSIONING.md` 规范（里程碑 V3 = 1.1.0，交付时打 tag v1.1.0）。

### 新增
- 需求 1：Git 版本管控 — Vite 构建注入 `__APP_VERSION__`（Git tag / dev-短哈希），侧边栏展示版本号
- 需求 2：策略下发与家长公告场景 A/B 决策（ADR 0010）
  - A2 策略乐观并发：`policies.version` 列 + PUT expectedVersion 校验（409 冲突返回服务端最新版），前端保存回传版本、冲突采纳服务端最新
  - A12 解绑硬删除：设备行 + 策略 + 公告送达 + 使用记录/汇总 + 中继会话 + 配对信息全清
  - B2/B10 公告 60 秒补偿重推：`AnnouncementCompensationService`（30s 扫描、每设备一次、幂等打标 `compensated_at`）
  - B5 公告删除清除指令：`announcement_clear` P2P 消息 + `announcement_tombstones` 墓碑表（7 天，重连同步下发 cleared_ids）
  - B6 紧急未确认公告重连必补推（不限于最近 3 条）
  - B11 公告广播账号隔离（仅发布者账号设备，同步/心跳/补偿同口径）
  - B13 公告归属账号：列表/详情/送达明细/紧急统计均按账号过滤（修复任意家长可读任意公告 id 的越权读取）
- 需求 14：应用运行日志上传与查看（ADR 0014）
  - 新增 `app_logs` 表（账号级归属 + ReceivedAt 服务端时间索引，7 天保留按 ReceivedAt 清理）
  - 新增 `/api/logs`：POST 上传（ParentOrAdmin，单批 ≤500、级别白名单归一化、
    入库二次脱敏、Message 1000/Tag 64/Client 64 截断、客户端时间钳制、限流 30/小时）、
    GET 列表（家长强制本账号；admin 全部 + accountId/level/from/to 筛选，limit 钳制 1-1000）
  - 新增 `AppLogSanitizer`：与 Android 端完全一致的 4 条脱敏正则（密码/令牌赋值、
    验证码、JWT、64 位 hex），服务端入库兜底
  - 前端日志页 LogsPage（家长 `/logs`「运行日志」+ admin `/admin/logs`「账号日志」单组件双路由）：
    级别/时间范围筛选、admin 账号筛选、级别色标表格、分页、7 天保留与脱敏说明

### 变更
- 策略 GET 返回 `version`；PUT 兼容旧页面（不传 expectedVersion 不校验）
- 需求 3/4（ADR 0011）：换账号/解绑全清客户端侧配套完成——Android `LocalDataWipe` 全清 +
  三处核对、登录页/儿童端换绑确认提醒、换账号时经 verify-password + DELETE /api/devices
  同步解绑本机设备；服务端侧复用需求 2 的 A12 硬删除，无新增接口
- 需求 5（ADR 0012，仅 Android）：上滑结束进程后管控失效修复——补 GuardianAlarmReceiver
  清单声明（存量漏注册）、上滑 5 秒系统侧恢复闹钟、心跳杀进程检测与通知、
  管控生效标记快速重放、能力边界如实说明（OEM_KEEPALIVE.md + 权限引导页）
- 需求 10/11（ADR 0013，仅 Android）：家长端策略/公告/报告与 Web 双向同步——
  Android 消费既有 /api/devices、/api/policies（expectedVersion/409）、/api/announcements、
  /api/reports 接口，服务端无代码改动

### 修复
- 公告详情/送达明细接口补归属校验（此前任意家长可读任意公告 id）
- 需求 14 顺带修复（交付阻塞项，见 ADR 0014 第 9 节）：
  - Program.cs 顶层语句 `Services.AnnouncementCompensationService` 无法解析兄弟命名空间前缀
    （需求 2 遗留，服务端自此无法编译）→ 全限定 `XiaopacaiWeb.Services.AnnouncementCompensationService`
  - DevicesController.Unpair 的 `ExecuteDeleteAsync` 不被 EF InMemory 测试提供程序支持
    （生产 SQLite 支持）→ 改为 load + `RemoveRange`，行为不变（A12 硬删除语义）
  - DeviceAccessTests 两条测试仍断言旧软解绑语义（清 OwnerUserId 留行）→
    按 A12（ADR 0010）硬删除语义重写

---

## [3.0.0-p3] — 2026-08-11

### Added (P3 前端页面与后台)
- Pinia 状态管理：auth / ui / devices / policies / announcements
- 完整 API 服务层（24 个端点覆盖所有模块）
- 路由守卫：登录检查 + 角色鉴权 + token 自动刷新
- 主布局：侧边栏/顶栏/面包屑/深色模式/角色自适应菜单
- 登录页：家长/管理员合一入口 + 演示账号
- 404 页面
- 用户端 6 页：仪表盘（统计卡片 + ECharts + 事件时间线）/ 设备管理（卡片网格 + 配对 + 解绑）/ 策略配置（限额/时段/分类/黑白名单）/ 公告管理（CRUD + 发布/撤回）/ 使用报告（日报/周报 + 图表 + 导出）/ 设置（密码/通知/备份/端口）
- 管理端 5 页：账号管理 / 设备总览 / 审计日志 / 系统设置 / 数据管理
- ECharts 图表集成（饼图/柱状图/折线图）
- Element Plus 中文 locale + 深色模式 CSS 变量
- 所有页面使用 Mock 数据（P4 对接真实 API）

### Changed
- 移除 HomePage.vue 占位组件

### Fixed
- P2 合并修复：/api/health 重复注册（e24231c, Codex@50.20）

---

## [3.0.0-p2] — 2026-08-10

### P2 后端 API 与数据层（P2-A + P2-B）

#### 新增
- 9 个 Entity 模型：User / Device / Policy / Announcement / UsageRecord / DailySummary / AuditLog / PairingInfo / RefreshToken
- 完整 AppDbContext.OnModelCreating（表映射、索引、外键、级联删除、默认值）
- SQLCipher 数据库初始化服务（随机密钥 → 加密存储 → PRAGMA key 拦截器）
- 密码哈希服务：Argon2id（64MB/4iter/2parallel） + PBKDF2 SHA-256 600k（兼容模式）
- JWT Token 服务：Access 60min + Refresh 7d（签发/存储/验证/吊销）
- AuthController：POST login/logout/refresh/change-password + GET me
- 角色鉴权策略：AdminOnly / ParentOrAdmin
- EF Core SQLCipher 连接拦截器（SqlCipherInterceptor）
- 数据库自动迁移 + 种子数据（admin/admin123 Argon2id 哈希）
- 认证 DTOs：LoginRequest / RefreshRequest / ChangePasswordRequest / AuthResponse / UserProfile
- Swagger JWT Bearer 安全方案文档配置

#### 变更
- Program.cs：完整 DI 注册（DB/JWT/Auth/Swagger）+ 数据库初始化管道
- HealthController：版本号更新为 3.0.0-p2
- CHECKPOINT.json：P2 → completed
- PROGRESS.md：P2-A/P2-B 完成标记

#### 依赖
- 新增：System.IdentityModel.Tokens.Jwt（已在 csproj）
- 新增：Konscious.Security.Cryptography.Argon2（已在 csproj）
- EF Core / Sqlite / JwtBearer / SignalR / Swashbuckle 保持 P1 版本

---

## [3.0.0-p1] — 2026-08-10

### P1 架构与骨架

#### 新增
- 项目目录骨架（docs/server/web/tests/build）
- ASP.NET Core 8 Web API 工程（健康检查 `/api/health`）
- Vite + Vue 3 + TypeScript 前端工程（空壳可构建）
- SQLCipher 数据库 Schema（8 张表）
- README / CHANGELOG / LICENSE / CHECKPOINT.json / PROGRESS.md / TOKEN_USAGE.md
- `.gitignore` 规则
- Git 初始化 + 首次提交

#### 数据库表
- `users` — 用户账号（家长 + 管理员）
- `devices` — 儿童设备注册信息
- `policies` — 策略配置（每设备一条）
- `announcements` — 公告管理
- `usage_records` — 使用记录（原始数据）
- `daily_summary` — 每日汇总（聚合数据）
- `audit_logs` — 审计日志
- `pairing_info` — 配对信息

#### 依赖
- .NET 8 / ASP.NET Core / EF Core / SQLCipher / SignalR / JWT / Argon2
- Vue 3 / Vite / TypeScript / Element Plus / Pinia / ECharts / Axios

---

## 格式说明

`[版本号]` — 发布日期，格式使用 YYYY-MM-DD。变更分类：新增 / 变更 / 修复 / 移除。
