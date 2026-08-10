# 小趴菜（儿童守护）· Web 3.0 开发提示词包

版本：V3.0-P1    日期：2026-08-10    编制：Codex@50.20（主测试）    依据：2.0 验收报告 + UI_SPEC + P2P 协议

## 一、执行总览

- 目标：在 2.0（Windows 家长端 + Android 儿童端）基础上，新增 **Web 3.0 网页端**，覆盖 2.0 家长端全部功能，并包含**完整用户前端 + 管理后端**。
- 版本关系：2.0 与 3.0 **互不干扰、独立项目文件夹**；3.0 功能上**包含 2.0 全部内容**（家长端全部能力 + 管理能力），3.0 目录自包含（含 2.0 参考资料）。
- 儿童端兼容：Android 儿童端 APK **不修改**，通过既有 TLS/P2P 协议直接对接 Web 3.0 家长端（端口可配置），完成 发现-配对-策略下发-时长上报-超时拦截 全链路。
- 部署模式：**自托管**（家长本机/LAN 部署，延续"本地优先、不上云、数据不出本机"的产品边界）；预留 HTTPS/反向代理说明。
- 角色：Claude@50.53 主开发（git 主仓库 `/home/winann/xiaopacai-web`）；Codex@50.20 主测试（本地镜像 `C:\Users\Public\bridge\work\xiaopacai-web`）。

## 二、功能清单

### A. 用户前端（家长端 Web 界面）
1. **仪表盘**：设备在线状态（已连接/重连中/未连接）、今日使用汇总（总时长/剩余）、超时停用统计（台数）、公告状态、最近事件列表；实时刷新（WebSocket/SignalR）。
2. **设备管理**：儿童设备列表、配对引导（扫描/手动 IP/配对码）、连接状态与证书指纹、设备详情、解绑。
3. **策略配置**：每日使用限额（滑杆 30~480 分钟）、就寝时段、分类限额（游戏/社交/视频/学习）、黑白名单、超时处理方式（整机停用/部分 APP 停用/仅提醒）；保存即下发。
4. **公告管理**：新建/编辑/发布/撤回公告，优先级（普通/重要/紧急）、有效期；发布实时推送儿童端。
5. **使用报告**：日报/周报（图表：趋势/分类占比）、按设备筛选、导出 TXT/JSON/CSV（PDF 可选）。
6. **设置**：账号与密码修改（PBKDF2/Argon2）、通知偏好、数据备份/恢复/清除、Web 服务端口与运行状态。

### B. 管理后端（Admin）
1. **账号与权限**：家长账号与管理员账号，登录/登出（JWT + 会话），角色权限（admin/parent），修改密码。
2. **设备管理**：设备注册/授权/解绑、在线状态总览。
3. **审计日志**：登录、策略变更、公告发布/撤回、数据导出、账号操作；可查询/导出。
4. **系统设置**：Web 端口、P2P 监听端口（默认 9527）、备份目录、数据保留策略。
5. **数据管理**：加密备份/恢复、数据清除（含 KeyStore/口令派生密钥轮换）、存储健康检查。

## 三、架构与数据边界

- 单进程自托管：ASP.NET Core Kestrel 提供 REST API + 静态前端 + SignalR 实时通道 + P2P TCP/TLS 监听（复用 2.0 P2PListenerService 逻辑，TLS1.3/1.2、4 字节长度前缀 + JSON 帧、证书持久化）。
- 数据存储：SQLite + SQLCipher 本地加密（与 2.0 同款方案：随机库密钥 → 加密存储 → 启动解密）；口令 PBKDF2(≥600k)/Argon2；不上云、无第三方存储。
- 前端：Vue 3 + TypeScript + Element Plus（UI 对照 2.0 Material 风格：蓝色主色 #4CAF50 辅助、深色模式、无障碍），图表 ECharts。
- 安全：本地绑定（默认 127.0.0.1，可配置 0.0.0.0）+ 可选 HTTPS；JWT 有效期与刷新；接口鉴权中间件；审计日志；CSRF/XSS 防护。

## 四、目录结构（独立项目）

```
xiaopacai-web/
├── docs/          # 本提示词、ADR、API.md、UI_SPEC.md、部署说明、2.0 参考资料索引
├── server/        # ASP.NET Core 8：Controllers / Services / Models / Data / P2P / Middleware
├── web/           # Vue3 + TS：src/views（六大页面 + 管理端）、src/api、src/stores
├── tests/         # 后端 xunit（策略/加密/P2P/审计）+ 前端 vitest
├── build/         # 发布产物（dotnet publish + web 静态打包）
├── README.md / CHANGELOG.md / LICENSE(Apache-2.0) / CONTRIBUTING.md
└── CHECKPOINT.json / PROGRESS.md / TOKEN_USAGE.md
```

## 五、技术栈与构建命令

- 后端：.NET 8 / ASP.NET Core / SQLCipher (Microsoft.Data.Sqlite + SQLCipher) / SignalR / JWT
- 前端：Node 18+ / Vue 3 / Vite / TypeScript / Element Plus / Pinia / ECharts
- 构建：
  - 后端：`dotnet build -c Release`；`dotnet publish -c Release`
  - 前端：`npm install`；`npm run build`
  - 测试：`dotnet test`；`npm run test`
- 运行：`dotnet run`（默认 http://127.0.0.1:5173 前端代理 5xxx 后端；或发布后单进程托管）

## 六、P2P 兼容要求（儿童端不改）

- 监听端口默认 9527（可配置），TLS 1.3/1.2 + 自签名证书持久化（指纹稳定，复用 2.0 LEGACY-e 实现）。
- 协议帧：4 字节大端长度前缀 + JSON；消息类型 handshake / policy_update / usage_report / announcement_push / heartbeat(+ack) / sync_ack。
- 配对：发现（mDNS/UDP 广播）+ 手动 IP + 配对码（6 位，服务端校验并与设备绑定）。
- 全链路验收：家长端 Web 改限额 → 下发 → 儿童端生效 → 超时拦截；断网跳过同步、恢复补发。

## 七、管理后端与前端交互要点

- 登录页（家长/管理员入口合一，按角色渲染菜单）；未登录跳转。
- 管理端：账号管理、设备管理、审计日志、系统设置、数据管理 5 个页面。
- 用户端：仪表盘、设备管理、策略配置、公告管理、使用报告、设置 6 个页面（对照 2.0 验收表）。
- 实时：SignalR 推送设备状态/公告/策略生效回执；仪表盘与设备页订阅。

## 八、验收标准（对照 2.0 验收表扩展）

| 类别 | 验收项 | 标准 |
|---|---|---|
| 功能 | 六大用户页面 | 与 2.0 家长端功能一一对应，GUI 走查通过 |
| 功能 | 管理后端 | 账号/权限/审计/系统/数据管理可用，权限隔离正确 |
| 联调 | P2P 全链路 | 儿童端 APK 不改直连 Web3.0：策略下发-时长上报-超时拦截 闭环 |
| 数据 | 隐私边界 | 不上云、SQLCipher 落盘、口令/库密钥加密存储、数据清除 |
| 安全 | 鉴权审计 | JWT + 角色鉴权、审计日志、HTTPS 说明 |
| GUI | 美观易用 | 对照 2.0 UI_SPEC：配色/深色/无障碍/响应式（桌面优先 + 移动可用） |
| 质量 | 测试 | 后端 xunit 覆盖策略/加密/P2P/审计；前端关键组件测试；构建 0 错误 |
| 交付 | 产物 | server publish + web 静态资源 + 部署说明 + Git bundle |

## 九、执行协议

- 阶段：P1 架构与骨架（目录/工程/CI 命令/数据库迁移）→ P2 后端 API 与数据层 → P3 前端六大页面 + 管理端 → P4 P2P 对接与端到端联调 → P5 测试、文档与打包。
- 协作：Claude 在 50.53 `/home/winann/xiaopacai-web` 开发并 commit；每次里程碑产出 `git bundle` 同步 50.20；Codex 拉取后在 50.20 构建/测试/回归，缺陷经信件（058+）回传。
- 规则：中文注释、commit 含 [TASK-WEB-xx] 标记、更新 CHECKPOINT/PROGRESS/TOKEN_USAGE、关键决策写 docs/adr/。
- 中断续接：读 CHECKPOINT.json + PROGRESS.md 恢复。

## 十、第一阶段任务（P1，立即执行）

1. 创建 `/home/winann/xiaopacai-web` 目录骨架（docs/server/web/tests/build + README/LICENSE/CHANGELOG/CHECKPOINT.json）。
2. 初始化 git 仓库并提交 P1 骨架。
3. 建立后端工程（ASP.NET Core 8 Web API，健康检查 `/api/health`）+ 前端工程（Vite + Vue3 + TS，可启动空壳）。
4. 设计数据库 Schema（SQLCipher）：users / devices / policies / announcements / usage_records / daily_summary / audit_logs / pairing_info（对照 2.0 AppDatabase）。
5. 产出 bundle 与回信（docs/bridge-out/046-web-p1-done.txt），说明 P1 完成与 P2 计划。

— 提示词包 V3.0-P1，Codex@50.20
