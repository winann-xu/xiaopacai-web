# 开发进度

## P1 架构与骨架 ✅ 完成（2026-08-10）

### 已完成工作
- [x] 项目目录骨架：`docs/` `server/` `web/` `tests/` `build/`
- [x] Git 初始化
- [x] ASP.NET Core 8 Web API 工程
  - [x] `server/xiaopacai-web.csproj` — 项目文件 + NuGet 依赖
  - [x] `server/Program.cs` — 入口 + 中间件管道 + `/api/health`
  - [x] `server/Controllers/HealthController.cs` — 健康检查控制器
  - [x] `server/appsettings.json` — 配置（JWT / DB / P2P / WebApp）
  - [x] `server/Data/DbContext.cs` — EF Core DbContext 骨架
  - [x] `server/Middleware/AuditMiddleware.cs` — 审计中间件骨架
- [x] Vite + Vue 3 + TypeScript 前端工程
  - [x] `web/package.json` — 依赖声明
  - [x] `web/vite.config.ts` — Vite 配置（代理后端 API）
  - [x] `web/tsconfig.json` — TypeScript 配置
  - [x] `web/src/main.ts` — Vue 入口
  - [x] `web/src/App.vue` — 根组件
  - [x] `web/src/router/index.ts` — 路由配置（11 条路由）
  - [x] `web/src/api/index.ts` — API 服务层骨架
  - [x] 11 个 Vue 页面占位组件（6 用户 + 5 管理）
- [x] SQLCipher 数据库 Schema
  - [x] `server/Data/Schema.sql` — 8 张表 DDL + 索引 + 种子数据
- [x] 项目文档（README / CHANGELOG / LICENSE / CHECKPOINT / PROGRESS / TOKEN_USAGE / CONTRIBUTING）
- [x] Git 提交 + bundle 输出
- [x] P1 验证修复：vue-tsc 升级 2.x + HomePage 未用变量清理（a09419e, Codex@50.20）

---

## P2 后端 API 与数据层 ✅ 完成（2026-08-10）

### P2-A: 数据层
- [x] 9 个 Entity 模型类（`server/Models/`）
  - User / Device / Policy / Announcement / UsageRecord / DailySummary / AuditLog / PairingInfo / RefreshToken
- [x] 完整 AppDbContext.OnModelCreating（表映射、索引、外键、级联删除、默认值）
- [x] SQLCipher 数据库初始化服务
  - 随机密钥生成（首次运行）→ 加密存储到 Data/.dbkey
  - 启动时加载密钥 + PRAGMA key
  - EF Core 连接拦截器（SqlCipherInterceptor）
- [x] 密码哈希服务
  - Argon2id（64MB 内存 / 4 迭代 / 2 并行度 / 32 字节盐值）
  - PBKDF2 SHA-256 600k 迭代（兼容模式）
  - 自动检测哈希格式（Argon2 vs PBKDF2）
- [x] JWT Token 服务
  - Access Token 60min + Refresh Token 7d
  - Refresh token 存储/验证/吊销
  - HMAC-SHA256 签名
- [x] DTOs（LoginRequest / RefreshRequest / ChangePasswordRequest / AuthResponse / UserProfile）
- [x] 数据库自动迁移 + 种子数据（admin/admin123 用 Argon2id 哈希）

### P2-B: 认证与鉴权
- [x] AuthController: POST /api/auth/login
- [x] AuthController: POST /api/auth/logout
- [x] AuthController: POST /api/auth/refresh
- [x] AuthController: POST /api/auth/change-password
- [x] AuthController: GET /api/auth/me（当前用户信息）
- [x] JWT 鉴权中间件完整配置（SecretKey/Issuer/Audience/ClockSkew）
- [x] 角色鉴权策略（AdminOnly / ParentOrAdmin）
- [x] Swagger JWT Bearer 文档配置

### P2 待 P3 继续
- [ ] P2-C: 业务 API（Devices/Policies/Announcements/Usage/Reports/Settings/Data/Audit Controllers）
- [ ] P2-D: 审计中间件完善

---

## P3 前端页面与后台 🔲 待开始

## P4 P2P 对接与端到端联调 🔲 待开始

## P5 测试、文档与打包 🔲 待开始
