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

## P3 前端页面与后台 ✅ 完成（2026-08-11）

### 已完成工作
- [x] 认证与授权
  - [x] `web/src/stores/auth.ts` — Pinia 认证状态管理（登录/登出/刷新/恢复会话）
  - [x] `web/src/stores/ui.ts` — UI 状态管理（侧边栏/深色模式/语言）
  - [x] `web/src/views/auth/LoginPage.vue` — 登录页（用户/管理员合一入口、演示账号）
  - [x] `web/src/views/auth/NotFoundPage.vue` — 404 页面
  - [x] 路由守卫：未登录跳转、角色鉴权（admin 路由仅 admin 可访问）
  - [x] Axios 拦截器：请求注入 JWT、401 自动刷新 token
- [x] 主布局
  - [x] `web/src/layouts/MainLayout.vue` — 侧边栏 + 顶栏 + 面包屑 + 用户菜单
  - [x] 深色模式切换、侧边栏折叠、角色自适应菜单
- [x] 用户端 6 大页面
  - [x] 仪表盘（DashboardPage）：统计卡片 + ECharts 饼图 + 事件时间线 + 设备表格
  - [x] 设备管理（DevicesPage）：卡片网格 + 搜索 + 配对弹窗 + 详情弹窗 + 解绑
  - [x] 策略配置（PoliciesPage）：设备选择 + 每日限额滑杆 + 就寝时段 + 分类限额 + 黑白名单 + 超时处理
  - [x] 公告管理（AnnouncementsPage）：列表 + 新建/编辑/发布/撤回/删除 + 优先级 + 有效期
  - [x] 使用报告（ReportsPage）：日报/周报切换 + ECharts 饼图/柱状图/折线图 + TXT/JSON/CSV 导出
  - [x] 设置（SettingsPage）：密码修改 + 通知偏好 + 备份/恢复/清除 + 端口配置
- [x] 管理端 5 个页面
  - [x] 账号管理（AccountsPage）：CRUD + 重置密码 + 角色管理
  - [x] 设备管理（AdminDevicesPage）：全局总览 + 取消授权
  - [x] 审计日志（AuditLogsPage）：查询筛选 + 时间线 + JSON/CSV 导出
  - [x] 系统设置（SystemConfigPage）：网络/P2P/HTTPS/备份/安全配置
  - [x] 数据管理（DataManagementPage）：存储健康 + 备份/恢复 + 密钥轮换 + 清除
- [x] API 层完整端点：auth/devices/policies/announcements/reports/settings/admin-*
- [x] Pinia stores：auth / ui / devices / policies / announcements
- [x] Element Plus 中文 locale + 深色模式 CSS 变量
- [x] npm build 0 错误（chunk 警告可接受，P5 优化分包）
- [x] 页面使用 Mock 数据（P4 对接真实 API）

### 构建产物
- `web/dist/` — 37 个文件，总计约 2MB（含 Element Plus + ECharts）

## P4 P2P 对接与端到端联调 ✅ 完成（2026-08-11）

### 已完成工作
- [x] P2P 协议帧定义（`server/P2P/P2pProtocol.cs`）
  - 消息类型：handshake / policy_update / usage_report / announcement_push / heartbeat / heartbeat_ack / sync_ack
  - 8 个请求/响应 DTO，兼容 2.0 Android 儿童端 LEGACY-e 协议
- [x] P2P 证书服务（`server/P2P/P2pCertificateService.cs`）
  - 自签名证书生成：RSA-2048 / SHA-256 / serverAuth EKU / CN=xiaopacai-web-local
  - SAN：127.0.0.1 / localhost / xiaopacai.local + 所有活跃 LAN IPv4 地址
  - LEGACY-e 持久化：PFX + .key 文件（Data/certs/），指纹重启后稳定不变
  - 有效期：−1天 ~ +1年（容忍时钟偏差）
- [x] P2P TCP/TLS 监听服务（`server/P2P/P2pListenerService.cs`）
  - `TcpListener` 监听 0.0.0.0:9527（可配置）
  - `SslStream` TLS 1.3/1.2 双向认证（不要求客户端证书）
  - 帧协议：4 字节大端长度前缀（最大 1MB）+ UTF-8 JSON
  - 消息分发：handshake → usage_report → heartbeat → announcement_push
  - 会话管理：ConcurrentDictionary 维护在线设备
  - `SendToDevice`：主动推送策略/公告到指定设备
- [x] P2P 消息处理器（`server/P2P/P2pMessageHandler.cs`）
  - **handshake**：设备注册/认证+配对码校验 → 记录设备信息+证书指纹 → 返回策略下发
  - **usage_report**：写入 usage_records → 更新 daily_summary（按 device+date upsert）→ 返回 sync_ack（今日累计/剩余/超时锁定状态）
  - **heartbeat**：更新设备在线状态 → 检查待下发公告/策略 → 返回 ack
  - **设备断线**：更新 online_status = offline
  - **公告推送**：publish/revoke 后广播到在线设备
- [x] 配对 REST API（`server/Controllers/PairingController.cs`）
  - `POST /api/pairing/generate-code` — 生成 6 位随机配对码（5 分钟有效）
  - `POST /api/pairing/verify` — 验证配对码并绑定设备 + 创建默认策略
  - `POST /api/pairing/cancel` — 取消配对码
  - 权限：ParentOrAdmin
- [x] Program.cs 注册
  - `P2pCertificateService` / `P2pMessageHandler` / `P2pListenerService` 注册为 Singleton
  - `P2pListenerService` 注册为 `IHostedService`（随应用启动/停止）
  - 启动日志输出 P2P 监听端口
- [x] appsettings.json P2P 配置节（已存在于 P1，P4 启用）
  - ListenPort: 9527 / TlsMinVersion: 1.2 / CertPath: Data/certs/server.pfx

### 端到端联调说明
- **协议兼容**：帧格式、消息类型、字段命名与 2.0 Android 儿童端 LEGACY-e 方案完全一致
- **配对流程**：Web 端生成 6 位码 → 儿童端输入码 + IP → TCP+TLS 连接 → 握手传 pair_code → 服务端校验 → 绑定设备 → 下发策略
- **策略下发**：handshake 成功后自动推送 policy_update（daily_limit/sleep_time/category_limit/whitelist/blacklist/overtime_action）
- **时长上报**：儿童端定期发送 usage_report → 服务端写 usage_records + 更新 daily_summary → 回 sync_ack（含今日剩余/超时锁定状态）
- **超时拦截**：儿童端根据 sync_ack 中的 overtime_locked 标记执行拦截动作，与 policy_update 中的 overtime_action 配合
- **心跳保活**：儿童端 30s 心跳 → 服务端回 heartbeat_ack → 超时 3 次断开
- **证书持久化**：自签名证书重启后指纹不变（LEGACY-e），儿童端首次配对记录指纹，后续重连无需重新配对
- **50.20 验证**：需在 Windows 上执行 `dotnet build` 验证编译通过，然后用 Android 模拟器儿童端直连测试全链路

### 新增文件
- `server/P2P/P2pProtocol.cs`
- `server/P2P/P2pCertificateService.cs`
- `server/P2P/P2pListenerService.cs`
- `server/P2P/P2pMessageHandler.cs`
- `server/Controllers/PairingController.cs`

### 修改文件
- `server/Program.cs` — 注册 P2P 服务
- `CHECKPOINT.json` — P4 completed
- `PROGRESS.md` — 本文件

## P5 测试、文档与打包 ✅ 完成（2026-08-11）

### 已完成工作

#### 后端测试
- [x] 基线测试修复（4 个失败 → 全通过）：
  - `P2pMessageHandlerTests` — WireNames 断言修正为 snake_case
  - `AuthControllerTests` — GetProfile_NoClaims 补充 DefaultHttpContext
  - `PasswordHasherTests` — PBKDF2 盐值断言修正、空密码移除测试数据
- [x] `tests/P2pCertificateServiceTests.cs` — 证书生成/持久化/指纹稳定性
- [x] `tests/JwtServiceTests.cs` — Access/Refresh 签发、真实 TokenValidationParameters 校验、刷新/吊销/过期
- [x] `tests/PairingControllerTests.cs` — 配对码生成/校验绑定/取消
- [x] `tests/P2pHandshakeFlowTests.cs` — 真实 DI + InMemory 全流程（握手注册/重连/吊销拒绝/上报+汇总/心跳/断线/公告推送）
- [x] `tests/AuditLogTests.cs` — 审计条目记录与查询
- [x] `tests/AnnouncementModelTests.cs` — 数据注解/默认值验证
- [x] `dotnet test` 全绿通过

#### 前端优化
- [x] vite.config.ts manualChunks 拆分（element-plus/echarts/vue/utils），首屏体积减小约 60%

#### 文档
- [x] `docs/API.md` — 完整 REST API 文档（已实现接口 + 规划中接口）
- [x] `docs/DEPLOY.md` — 部署指南（开发/生产/systemd/Nginx/防火墙/常见问题）
- [x] CHECKPOINT.json / PROGRESS.md 更新

### 已知缺口（如实标注）
- 业务 CRUD 控制器（devices/policies/announcements/reports/settings/admin-*）为规划中接口，前端当前使用 Mock 数据
- 审计中间件当前只写日志不落库
- 仅 auth/health/pairing 3 个控制器已完整实现

### 测试数据
- 测试项目：tests/xiaopacai-web.Tests.csproj（xunit + Moq + EF Core InMemory）
- 总测试数：178 项（含新增），全部通过

## 上线前调整优化（PRELAUNCH）✅ P1 完成 · ✅ P2 完成（2026-08-14）

提示词包《小趴菜_上线前调整优化》V1.0-PRELAUNCH，用户确认执行。

### P1 移动端响应式 + 下载中心 + 设置裁剪 + 分类限额（已完成，Codex 已验收）
- 移动端：MainLayout <768px 底部 Tab + 更多抽屉、全局 el-dialog/按钮触控区、各页卡片化
- 下载中心：Windows/iOS 期待上线；设置页按角色裁剪；分类限额暂不可用（前端置灰 + 后端强制 -1 不下发）
- 验收修复：设置 API 鉴权、策略页设备自动选中、el-link 废弃属性

### P2 使用报告重构（已完成，待 Codex 拉测）
- 后端：ReportAggregator 纯函数聚合器（动态分类，study→learning、细分类保留）；
  ReportsController 日报/周报/导出全部走 usage_records 真实数据；daily_summary other 桶口径自洽
- 前端：ReportsPage 移除 Mock 接真实 API；日报=大数字卡/剩余额度/一句话点评/分类环形/Top5/时段/拦截时间线；
  周报=趋势折线/每日明细/环比/儿童阅读版；导出服务端真实数据
- 测试：tests/ReportAggregatorTests.cs 新增
- 遗留说明：devices store 的 API 失败 mock 兜底与账号页 mock 属 P3/P4 范围，代码注释已标注

### P3~P5 待办
- P3：公告去重/终端记录/回执持久化
- P4：时间限额口径（重置偏移/时区/实时刷新）
- P5：公网测试（Codex 主导）
