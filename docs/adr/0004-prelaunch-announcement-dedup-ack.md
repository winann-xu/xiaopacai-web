# ADR 0004 — 公告去重/终端记录/回执落库协议扩展（PRELAUNCH-P3）

- 日期：2026-08-14
- 状态：已实施（待 Codex 评审协议与拉测）
- 关联需求：《小趴菜_上线前调整优化》需求 3（公告管理逻辑修正）、需求 9 第 4 条（紧急公告计数口径）

## 背景

已核实问题：

1. Android `AnnouncementDao.upsert` 使用 `CONFLICT_REPLACE`，同一公告重推（撤回后重新发布/断线补推）会覆盖 `is_read`/`acknowledged_at`，导致已确认状态丢失并再次弹通知/全屏置顶。
2. `SyncManager.handleAnnouncementPush` 对每条推送无条件调用 `showAnnouncementImmediately`，无"已显示过"判断。
3. 紧急公告（priority>=2）只在全屏覆盖层出现；主页列表查询 `is_read = 0`，确认后（is_read=1）即从列表消失，不满足"列表可见并标注已确认/未确认"。
4. Web 收到 `announcement_ack` 仅向家长端中继转发，未落库，Web 无法查看每设备回执。

## 决策

### 1. 推送载荷扩展（向后兼容，字段只增不改）

`announcement_push` 的 `payload.announcements[]` 每项新增：

| 字段 | 类型 | 说明 |
|------|------|------|
| `version` | int | 发布代数：每次 publish 递增（首次=1）；撤回不递增 |
| `content_hash` | string | SHA-256(`title`+`\n`+`content`+`\n`+`priority`) 前 16 位十六进制；内容未变则哈希不变 |
| `requires_ack` | bool | `priority >= 2` 时为 true（修复此前紧急公告推送不带 requires_ack 的缺陷） |

旧儿童端忽略未知字段不受影响（JSON 解析按 opt 取值）。

### 2. 新消息类型：`announcement_displayed`

儿童端公告"已显示"事件上报（此前只有 `announcement_ack` 确认回执）：

```json
{ "type": "announcement_displayed",
  "payload": { "announcementId": 12, "displayedAt": 1760000000, "deviceId": "AND-001" } }
```

Web 收到后落库 `displayed_at` 并中继给家长端（与 `announcement_ack` 同链路）。

### 3. Web 新增 `announcement_deliveries` 送达回执表

| 列 | 说明 |
|----|------|
| announcement_id / device_id | 复合唯一索引 |
| push_count | 累计推送次数（发布/重推每次 +1） |
| last_pushed_at | 最近推送时间 |
| displayed_at | 终端首次显示时间（来自 announcement_displayed） |
| acknowledged_at | 家长/终端确认时间（来自 announcement_ack） |

- 推送时（`PushAnnouncement`/补推 sync）按设备 upsert：`push_count++`、`last_pushed_at=now`
- 离线期间不产生推送记录（重连补推时计数）
- 紧急公告"未确认数"口径 = 已发布紧急公告 ×（已配对激活设备中未确认的设备数），Web 仪表盘据此显示

### 4. Android 终端去重规则（DB V4）

- `announcements` 新增列：`displayed_at`、`last_push_hash`、`delivered_count`（V3→V4 迁移补列）
- `upsert` 改合并式：按 announcement_id 读取既有行：
  - 内容哈希不变 → 保留 `is_read`/`acknowledged_at`/`displayed_at`，仅更新 `expires_at`、`delivered_count++`
  - 哈希变化（标题/内容/优先级变化）→ 更新正文，重置 `displayed_at=0`/`acknowledged_at=0`/`is_read=0`，允许重新提示
- `handleAnnouncementPush` 展示判定：
  - `action == "revoke"` → 本地清除/置过期，不展示
  - 已显示且哈希未变 → 不弹窗、不通知、不置顶（仅更新有效期）
  - 紧急且 `acknowledged_at > 0` → 不再全屏
  - 新公告/内容变化 → 展示并写 `displayed_at`，上报 `announcement_displayed`
- 主页公告列表：查询条件改为 `priority >= 2 OR is_read = 0`，紧急卡片带"紧急"红标与"已确认/未确认"状态，确认后保留记录

## 影响与兼容性

- 协议字段均为新增，旧端忽略即可；`announcement_displayed` 为新增消息类型，Web 服务端兼容处理（未知类型记日志跳过，同现有 default 分支）。
- 家长端（Android ParentP2PListenerService）对 `announcement_ack` 的处理不变；`announcement_displayed` 中继到家长端，家长端无处理逻辑时忽略。
- 不破坏已验证链路：TLS P2P 握手、配对码、策略下发、时长上报、超时锁定、重连补推。

## 待办（后续阶段）

- P4：`announcement_ack` 在设备离线时的本地缓存与重连补发（当前未连接时静默丢弃，靠重推兜底）。
