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
  - [x] `users` — 用户账号
  - [x] `devices` — 设备注册
  - [x] `policies` — 策略配置
  - [x] `announcements` — 公告管理
  - [x] `usage_records` — 使用记录
  - [x] `daily_summary` — 每日汇总
  - [x] `audit_logs` — 审计日志
  - [x] `pairing_info` — 配对信息
- [x] 项目文档
  - [x] `README.md` — 项目说明 + 快速开始
  - [x] `CHANGELOG.md` — 变更日志
  - [x] `LICENSE` — Apache-2.0
  - [x] `CHECKPOINT.json` — 阶段检查点
  - [x] `PROGRESS.md` — 本文件
  - [x] `TOKEN_USAGE.md` — Token 用量
  - [x] `CONTRIBUTING.md` — 贡献指南
- [x] Git 提交 + bundle 输出

---

## P2 后端 API 与数据层 🔲 待开始

### 计划任务
- [ ] 创建 8 个 Entity 模型类（`server/Models/`）
- [ ] 实现 AppDbContext 完整配置（OnModelCreating）
- [ ] SQLCipher 数据库初始化与密钥管理
- [ ] 用户认证 API：`POST /api/auth/login` `POST /api/auth/logout`
- [ ] JWT Token 签发与刷新中间件
- [ ] 设备管理 API：CRUD + 配对
- [ ] 策略配置 API：CRUD + 下发
- [ ] 公告管理 API：CRUD + 推送
- [ ] 使用记录 API：查询 + 汇总
- [ ] 审计日志 API：查询 + 导出
- [ ] 系统设置 API
- [ ] 数据管理 API：备份/恢复/清除
- [ ] 单元测试骨架（xunit）

---

## P3 前端页面与后台 🔲 待开始

## P4 P2P 对接与端到端联调 🔲 待开始

## P5 测试、文档与打包 🔲 待开始
