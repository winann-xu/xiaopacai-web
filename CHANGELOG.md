# 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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
