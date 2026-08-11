# ADR 0003 — OPT12 Web 3.0 协议与数据模型扩展

- 日期：2026-08-11
- 状态：已接受（P1 阶段落地，P4 阶段继续深化）
- 关联需求：OPT12 需求 1（应用分类）/ 需求 3（跨网络中继）/ 需求 5（故障诊断）/ 需求 10（扫码登录）/ 需求 12（忘记密码）

## 背景

12 项优化中涉及 Web 3.0 后端协议与数据模型扩展的部分需要一次性规划并落地，为后续
Android 儿童端（P2）、Android 家长端（P3）、Web 前端与管理端（P4）提供稳定的接口契约。

## 决策

### 1. 故障诊断（需求 5）

新增 `diagnostics` 表与 `DiagnosticsController`：

| 端点 | 鉴权 | 说明 |
|------|------|------|
| `POST /api/diagnostics` | 匿名（儿童端无 JWT，走 P2P 证书链路） | 上报诊断信息；字段：device_id、app_version、android_version、device_model、manufacturer、permission_status(JSON)、service_status(JSON)、recent_crashes(JSON)、p2p_history(JSON)、db_size_bytes、network_type、reported_at |
| `GET /api/admin/diagnostics` | AdminOnly | 列表/筛选（deviceId、from、to、limit≤200） |
| `GET /api/admin/diagnostics/export` | AdminOnly | 导出全部筛选结果为 JSON 文件下载 |

设计要点：
- 诊断记录按 `device_id` 字符串关联（非 FK），允许诊断先于设备注册到达。
- 上报端点匿名开放；TODO(P4 安全审查)：接入设备级 Token 校验。
- `device_id` 与 `reported_at` 建复合索引，支撑管理端按设备/时间查询。

### 2. 扫码登录 Ticket（需求 10）

Ticket 为一次性、短时效凭证，P1 阶段存于进程内 `TicketStore`（ConcurrentDictionary），
单进程自托管场景足够；TODO(P4)：多实例/重启恢复场景迁移数据库表。

| 端点 | 鉴权 | 说明 |
|------|------|------|
| `POST /api/auth/login-ticket` | 匿名 | 生成一次性 Ticket（UUID，90 秒有效，状态 pending） |
| `GET /api/auth/login-ticket/{ticket}` | 匿名 | 轮询状态：pending / confirmed / expired；confirmed 时首次返回 JWT（access+refresh）并一次性消费 |
| `POST /api/auth/login-ticket/{ticket}/confirm` | 需登录 | 家长端 APP 确认，绑定确认者用户，签发由轮询侧完成 |

安全模型：Ticket 作为 Bearer 凭证，确认后仅首次轮询获得 JWT；过期或重复消费被拒绝。

### 3. 忘记密码重置 Ticket（需求 12）

| 端点 | 鉴权 | 说明 |
|------|------|------|
| `POST /api/auth/reset-ticket` | 匿名 | 生成一次性 Ticket（10 分钟有效），绑定目标账号 username；账号不存在时同样返回 Ticket，不泄露账号存在性 |
| `GET /api/auth/reset-ticket/{ticket}` | 匿名 | 轮询状态：pending / confirmed / expired |
| `POST /api/auth/reset-ticket/{ticket}/confirm` | 需登录 | 确认者账号必须与 Ticket 目标账号一致（TicketStore.Confirm 强制校验） |
| `POST /api/auth/reset-ticket/{ticket}/reset` | 匿名（凭证=已确认 Ticket） | 设置新密码（PBKDF2/Argon2），吊销该账号全部 Refresh Token，一次性消费 |

TODO(P5)：失败限速（5 次/小时）、审计日志落库。

### 4. 云端中继（需求 3）

- `devices` 表新增 `owner_user_id`（TEXT 可空，配对确认时绑定家长账号）与
  `app_categories`（TEXT JSON，应用分类配置）两列。
- 新增 `relay_sessions` 表：device_id、role（child/parent）、user_id（可空）、
  ip_address、status（connected/disconnected）、connected_at、disconnected_at。
- `GET /api/relay/sessions`（AdminOnly）：管理端查看中继会话列表，支持 status/role 筛选，
  返回 onlineCount 供仪表盘使用。

TODO(P4)：P2pMessageHandler 握手/断线时写入 relay_sessions；P2P 消息层实现
usage_report / policy_update / announcement_push 的中继路由。

### 5. 应用分类（需求 1）

- `GET /api/devices/{id}/app-categories`（ParentOrAdmin）：返回设备应用分类列表。
- `PUT /api/devices/{id}/app-categories`（ParentOrAdmin）：全量覆盖保存（JSON 数组
  `[{packageName, appName, category}]`），校验分类值 game/social/video/learning/other，
  归一化为小写后落库。

TODO(P4)：保存后触发 policy_push 携带 app_categories 下发儿童端。

## Schema 变更汇总（SQLCipher / EF Core 自动建表）

- 新表：`diagnostics`、`relay_sessions`
- `devices` 新列：`owner_user_id`（TEXT 可空）、`app_categories`（TEXT JSON）
- `server/Data/Schema.sql` 同步更新（V3.0-OPT12-P1）

## 兼容性

- 未修改任何既有端点行为（auth login/logout/refresh/change-password/me、pairing 全套、health）。
- `AuthController` 构造函数新增 `TicketStore` 依赖（DI Singleton），测试构造同步更新。
- 既有 178 项 xunit 测试全部通过。

## 后果

- 新端点已具备可验收的最小实现；依赖 P2P 中继写入、policy_push 下发、设备级鉴权、
  失败限速等为 P4/P5 待办，代码中以 `// TODO(P4/P5)` 标注。
- 扫码/重置 Ticket 内存存储在进程重启后失效（业务可接受：Ticket 有效期最长 10 分钟）。
