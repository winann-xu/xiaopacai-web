# 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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
