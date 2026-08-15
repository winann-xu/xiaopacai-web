# 小趴菜里程碑 V1.1.1 加固版 · 标准提示词（产品负责人已确认）

任务 ID：[TASK-HARDENING-V1.1.1]
范围：Web + Android（本期）；Windows/iOS/TV 不涉及（除明确说明外）。
版本：v1.1.1 / versionCode 10101（语义化版本，tag 与 Android versionName 一致）。

## 一、总体原则

1. **Git 版本管控**：本版升级走 Git：更新 CHANGELOG.md → 打 tag v1.1.1 → 交付后由 Codex 推送 GitHub 并部署阿里云；Android versionName=v1.1.1、versionCode=10101。
2. **安全红线沿用**：验证码/密码/令牌/密钥不落明文、不写日志与日志上传；公网唯一 HTTPS 通道；家长身份验证必须云端账号密码。
3. **离线语义沿用**：已下发内容本地照常执行，离线仅停止同步，恢复后增量同步 + 补报。
4. **目标水准**：达到 Norton Family（赛门铁克消费级家长管控）同档保护——权限健康度检测与告警、设备管理员防卸载、保活引导、失守上报；**不引入黑科技保活**（Leoric 类在 Android 14+ 已失效且不合规）。
5. **决策边界**：本提示词第三节已列全部已确认决策；未列出项（如“屏蔽系统设置”、DPC 落地激活等）不得擅自实施。

## 二、需求清单（逐条）

### 1. 上滑结束小趴菜后管控失效（P0，真机 OPPO 实测）

**现象**：限额到期后被管控 App 已拦截；用户上滑结束小趴菜进程后，被管控 App 恢复可用。

**根因（已实锤）**：`android/app/src/main/AndroidManifest.xml` 中 `GuardianForegroundService` 与 `ParentP2PListenerService` 均未声明 `android:stopWithTask="false"`，上滑最近任务后服务随任务销毁；OPPO ColorOS 熄屏/清理还会杀后台与无障碍服务。

**要求**：
- **1-A（必做）**：两个 service 声明补 `android:stopWithTask="false"`；保留 `onTaskRemoved` + 5 秒 AlarmManager 恢复兜底（现有 GuardianAlarmReceiver）；补单测覆盖 Manifest 声明与恢复链路。
- **1-B（必做）**：OPPO 保活引导四项自动检测 + 一键跳转：自启动管理、后台活动/后台冻结、电池优化白名单、最近任务锁定；复用/扩展 PermissionGuideScreen 与 AntiBypassService；家长端与 Web 展示“守护健康度”。
- **1-D（必做）**：失守监控上报：记录守护失效开始/结束时间与失守时长（本地持久化 + P2P 上报家长端 + Web 展示）；守护恢复后立即重新拦截并通知家长。
- **1-C（本期仅检测说明）**：检测设备是否可成为 Device Owner（ADB 预置条件/OPPO 企业定制限制），输出引导文档与提示；**不落地 DPC 激活**。

### 2. 儿童端倒计时不自动更新（P0，真机实测）

**现象**：儿童端看到的剩余时间一直不变（仅分钟、每 30 秒才刷新一次）。

**根因（已实锤）**：`GuardianHomeContent.kt` 用 30 秒轮询读取采集器，只显示分钟；采集器 60 秒采集一次，两者叠加导致数字长时间不动。

**要求**：
- **2-A（必做）**：改为每秒由 `System.currentTimeMillis()` 驱动的本地倒计时，显示 **HH:MM:SS**；剩余 = 今日限额 −（采集器最近已用 + 距最近采集时间增量）；采集器 60 秒采集周期保持。
- **2-B（必做）**：本地倒计时归零时立即触发锁定界面（与采集器/TimeoutExecutor 双保险，消除最长 60 秒空窗）。
- 权限或采集失效时显示“守护失效”明确状态与修复入口，禁止展示假倒计时。

### 3. 日志未上传 + Web 查日志报错（P0，根因已实锤）

**根因**：`server/Models/AppLogEntry.cs` 未加 `[Table("app_logs")]`，`DbContext.OnModelCreating` 也未 `ToTable`；`DataExtensions.cs` 建表名为 `app_logs`，EF 按约定查询 `AppLogEntries` 表 → “no such table” 500，上传接口同因失败；客户端日志每 6 小时才自动上传且未登录家长账号时跳过。

**要求**：
- **3-A（必做）**：`AppLogEntry` 映射到 `app_logs`（与 DataExtensions 建表一致）；存量库兼容（表已存在则直接复用，不丢历史）；补单测覆盖“写入 + 查询”均走 `app_logs`。
- **3-B（必做）**：客户端登录/绑定成功后立即触发一次日志上传（`LogUploader.uploadNow`）；失败按 5/15/60 分钟指数退避重试；WorkManager 每 6 小时周期保留为兜底。
- **3-C（必做）**：家长端日志页显示“上次上传时间/失败原因”；Web 日志页账号隔离 + admin 全量可见（在已有基础上补齐状态展示）。

### 4. 无障碍等权限重开后丢失（P0，真机 OPPO 实测）

**现象**：App 设置好的无障碍等权限，程序重开/OPPO 清理后丢失，需重新授权。

**根因**：OPPO ColorOS 熄屏/强杀后移除无障碍服务；现有自检每分钟一次且引导力量不足。

**要求**：
- **4-A（必做）**：自检改为“事件 + 定时”双触发：回到前台、解锁、开机、收到系统广播立即检查，每分钟兜底；发现被关立即高优通知 + 一键直达无障碍服务设置页。
- **4-B（必做）**：OPPO 四项白名单检测与一键引导（与 1-B 合并实现）；家长端/Web 健康度展示与失守历史。
- **边界如实说明**：无障碍服务被系统/OEM 移除后，第三方 App 无法自动重新开启（平台硬限制），只能检测 + 引导 + 告警；验收文档须写明该边界与缓解措施。

## 三、已确认决策（产品负责人拍板，无需再问）

| 项 | 决策 |
|---|---|
| Bug1 | 执行 1-A + 1-B + 1-D；1-C 仅做检测与说明 |
| Bug2 | 执行 2-A + 2-B；显示 HH:MM:SS |
| Bug3 | 执行 3-A + 3-B + 3-C |
| Bug4 | 执行 4-A + 4-B |
| 版本 | v1.1.1 / versionCode 10101 |
| 保活路线 | 不引入黑科技保活（Leoric 类），不落地 DPC 激活 |
| 范围外 | “屏蔽系统设置”等未确认新功能本期不做 |

## 四、验收标准与交付物

**交付物**：
- 代码 + 新增单测（每条 P0 至少一条回归用例）；
- ADR 记录关键决策与平台边界（至少 1 条，可合并）；
- CHANGELOG.md 更新 + tag v1.1.1（提交后）；
- Android/Web 各一份 bundle 交付 Codex（命名：`xiaopacai-v1.1.1-result.bundle`、`xiaopacai-web-v1.1.1-result.bundle`）；
- 真机回归清单（OPPO）：上滑后守护继续/恢复、倒计时秒级刷新、权限被关后通知与引导、日志上传后 Web 可查、健康度展示。

**质量门禁**：Android 单测全绿、Web 单测全绿、npm build 通过；真机验收由 Codex 执行；通过后 Codex 部署阿里云、更新下载中心、推送 GitHub。

## 五、协作纪律

- Claude@50.53 主开发；Codex@50.20 测试/集成/部署/验收。
- 提交信息与任务标注 `[TASK-HARDENING-V1.1.1]`；中文注释；token 用量记录 `docs/TOKEN_USAGE.md`。
- 未确认项不得实施；平台边界如实写文档，不得夸大能力。
