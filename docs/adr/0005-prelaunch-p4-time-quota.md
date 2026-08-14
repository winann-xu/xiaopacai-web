# ADR 0005 — 时间额度口径统一：重置偏移/时区/协议字段（PRELAUNCH-P4）

- 日期：2026-08-14
- 状态：已实施（P4 初版已拉测；FIX-100 增补见文末，待 Codex 复验）
- 关联需求：《小趴菜_上线前调整优化》需求 7（设备页数据准确性：重置偏移/时区/Mock 移除/实时刷新）、需求 9（仪表盘/策略页同源同步）

## 背景

已核实问题：

1. **限额重置链路断裂**：Web 点"重置当日限额"→ 服务器 `PendingResetAt` + 推送 `limit_reset`，但 Android `SyncManager` 从不处理 `limit_reset` 消息 → 儿童端已用不清零，超时封锁不解除，重置形同虚设。
2. **用量虚高（累加重复）**：Android 每 ≤5 分钟上报"当日累计值"，服务器端按行追加 → 同包名同日期被重复求和（例如 30→55 分钟两次上报被算成 85）。需按 (包名, 日期) upsert。
3. **日期口径混乱**：Web 用 `DateTime.UtcNow` 算"今天"，与儿童端本地日期（Asia/Shanghai）跨日不一致；报告默认区间同病。
4. **前端 Mock 兜底**：设备/账号/审计页 API 失败时展示假数据（设备 3 台、审计 5 条、账号 2 个），掩盖真实故障，需求 7/9 要求移除。
5. **无实时刷新**：设备页/仪表盘只在进页时拉一次，与儿童端状态脱节，需求验收要求 ≤30s 同步。

## 决策

### 1. 调整后已用 = max(0, 原始累计 − 当日重置偏移)

服务器新增 `AdjustedUsageCalculator.ComputeAdjusted(rawMinutes, resetOffsetMinutes, resetDate, today)`：
- 偏移仅当 `resetDate == today` 有效（跨日自动失效，偏移回到 0）；
- `devices` 表新增列：`LastResetOffsetMinutes`（INTEGER，默认 0）、`LastResetDate`（TEXT，yyyy-MM-dd）、`LastReportAt`（TEXT，UTC）。
- `ResetLimit` 时服务器用当前汇总估算偏移（立即展示 0），儿童端随后经 usage_report 上报精确偏移校正。

### 2. 统一 Asia/Shanghai 日期口径

`AppClock.TodayShanghai()/TodayShanghaiDate()`：优先 `TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")`，Windows/Linux 缺失时用 `CreateCustomTimeZone` 固定 +8 回退。Devices/Reports/Policies 控制器"今天"全部走此口径。

### 3. 协议扩展：usage_report 新增偏移字段（向后兼容，字段只增不改）

```json
{ "type": "usage_report",
  "payload": { "deviceId": "AND-001", "records": "[...]",
               "dailyResetOffsetMinutes": 60, "timestamp": 1760000000 } }
```

- `dailyResetOffsetMinutes`：儿童端本地存储的当日重置偏移（重置前累计分钟数）；缺省/非数字按 0 处理（旧端兼容）。
- 服务器收到且字段存在时落库 `LastResetOffsetMinutes`/`LastResetDate = 设备本地日期`；`sync_ack` 的 TodayTotalMinutes/Remaining/OvertimeLocked 改用调整后口径。
- 儿童端在 `limit_reset` 时把"重置前当日累计"写入 SharedPreferences（`guardian_prefs`，键 `daily_reset_offset_minutes`/`daily_reset_offset_date`），随每次 usage_report 上报。

### 4. usage_records 按 (包名, 日期) upsert 去重

`HandleUsageReport` 改为：同设备同包名同日期（设备本地日期）→ 更新 DurationSeconds/TotalMinutes 而非追加行，修复重复累加虚高。

### 5. 前端：移除 Mock + 30s 轮询 + 调整后口径展示

- 设备页/仪表盘/策略页 30s 轮询（`onUnmounted` 清理），显示"最后刷新"时间；策略页重置后立即本地刷新。
- 设备/账号/审计页 API 失败 → 错误提示 + 重试按钮（不再假数据）。
- 展示双口径：调整后"今日已用" + 原始累计（注明含重置前、与报告同口径）；重置过的设备打"已重置"标签。
- 仪表盘事件时间改用真实数据时间（`lastReportAt`/`lastSeen`），不再 `toISOString` 造数。

## 后果

- 报告（日报/周报）口径不变：仍按原始累计（含重置前），与设备页"原始累计"标注一致。
- 旧版儿童端（无偏移字段）行为等同旧版：偏移不校正，但重置链路本身已修复（服务器侧估算）。
- 验证链保持：策略下发/超时锁定/报告/公告去重/TLS P2P 均未改动。

## 增补（FIX-100，Codex 缺陷 100/101 修复，2026-08-14）

Codex 拉测发现两处问题，修复如下：

### 1. usage_records 历史重复行 → raw SUM 虚高（缺陷 100）

- 根因：P4 前无 (device_id, app_package, 日期) 唯一约束，历史重复行残留；P4 初版 upsert 只更新组内第一行、不删除同键其余行。
- 修复：启动迁移 `DedupUsageRecordsAsync` — 按 (DeviceId, AppPackage, substr(StartTime,1,10)) 保留 Id 最大一行、删除其余；再建唯一表达式索引 `idx_usage_records_device_package_date`（SQLCipher 兼容，失败不阻断启动）。应用层 upsert 同步加固：批内同键去重（取最后一条）+ 本批已插入键登记，防止同批双插入触发唯一冲突。

### 2. 展示/ack 优先儿童端上报的调整后已用（缺陷 100 建议 2）

- 协议再增字段（仍是只增不改，向后兼容）：

```json
{ "type": "usage_report",
  "payload": { "deviceId": "AND-001", "records": "[...]",
               "dailyResetOffsetMinutes": 60,
               "todayAdjustedMinutes": 25, "timestamp": 1760000000 } }
```

- `todayAdjustedMinutes`：儿童端自算的调整后今日已用（UsageStatsCollector 实时累计口径，与主页/超时判定同源；采集器未就绪时以 UsageStatsHelper 实时累计 − 偏移兜底）。
- Web 落库 `devices.TodayAdjustedMinutes`（INTEGER NULL）；设备列表/详情/ack 的"今日已用"优先采用该值，仅当 `LastReportAt` 属于今日（Asia/Shanghai）时有效，隔夜陈旧值回退服务端计算（`ResolveTodayUsedMinutes` 纯函数）。
- `ResetLimit` 时服务器将 `TodayAdjustedMinutes` 置 0（立即显示归零），儿童端下次上报自算值覆盖。
- usage_records 仅用于报告（原始累计）与回退口径；raw 经去重迁移后恢复准确。

### 3. 缺陷 101（Codex 已修，合入）

- Android `handleLimitReset` 原走 SQLCipher DAO 查询偶发 `SQLiteMisuseException`；改为 `UsageStatsHelper.getTodayTotalMinutes(context)` 实时累计（Codex commit 2084c34 已合入主仓库）。

