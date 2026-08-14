# ADR 0007：扫码绑定失败根因修复 + 家长端换账号清理（TASK-PRELAUNCH-FIX-SCAN / TASK-PRELAUNCH-PARENT-RESET）

- 日期：2026-08-14
- 状态：已接受
- 相关：SECURITY_BASELINE.md R2.1/R2.2、K3 限速（p2p-handshake:ip / p2p-paircode）、ADR 0006
- 任务：TASK-PRELAUNCH-FIX（Codex 117 信，用户已确认）

## 背景（生产事故根因，阿里云日志实证）

真机首次扫码绑定成功后，断线重连时儿童端仍携带旧配对码；当配对码为 pending 且签发账号 ≠ 设备归属账号时，服务端按 SEC R2.1/R2.2 拒绝「配对码归属不匹配」；儿童端不识别确定性拒绝，无限重试 → 5 次失败触发 SEC-K3 IP 限速 → 之后重新扫码也被拒（级联放大）。

## 决策

### 1. 已配对设备重连：按证书指纹放行，忽略携带的配对码（服务端）

- 已配对设备（PairStatus=paired 且有可信指纹）重连时**不再查询配对码归属**：无论携带已确认旧码、无码还是 pending 码，一律按证书指纹放行。
- 归属绑定只发生在两条路径：① 新设备首次配对（P2pMessageHandler section 1）；② `/api/relay/register` 配对码路径（保留归属校验）。
- 理由：已配对设备的身份锚是 mTLS 客户端证书指纹（ADR 0006 K1），配对码是一次性绑定凭据，不应参与已绑定设备的重连判定。

### 2. 解绑即释放归属（D1）

- `DevicesController.Unpair` 与吊销路径清空 `OwnerUserId` 与 `PairCode`：任意账号凭新 pending 配对码可重新绑定。
- 理由：旧归属残留会拦截新账号重绑（SEC-K2 403），换机/换账号场景无法自愈。

### 3. 确定性错误码 `error_code`（协议新增，向后兼容）

- 拒绝帧新增顶层字段：`{"type":"handshake_rejected","error":"<可读原因>","error_code":"<码>"}`。
- 错误码表：
  - `unpaired`：设备无归属且无有效配对码（不计限速）
  - `revoked`：设备已被吊销（不计限速）
  - `device_owned_by_other`：设备归属其他账号，换绑被拒（不计限速）
  - `fingerprint_mismatch`：证书指纹不匹配（不计限速）
  - `invalid_pairing_code`：配对码无效或已过期（**仍计限速**——爆破信号）
- 确定性拒绝（用户需家长端操作或重新扫码，重试无意义）**不计入** `p2p-handshake:ip` / `p2p-paircode` 失败计数；仅网络异常、无效配对码等临时性失败计数。审计日志仍记录（detail 含 error_code）。
- Windows 家长端沿用 `{"type":"error","payload":{"message":"<码>"}}`（message 即错误码）。
- 旧版客户端（不解析 error_code）行为不变：连接被关 → 退避重连，兼容无破坏。

### 4. 配对码消费语义

- 重绑成功（同账号/换账号凭有效码）即消费配对码：`PairStatus=confirmed`、写入 `ConfirmedAt`、绑定新指纹。
- 同账号重复扫码允许指纹轮换（D2，保留现状）：凭有效 pending 码重绑即可轮换指纹，扫码路径不受影响。

### 5. 儿童端确定性拒绝停止重连（D3 联动）

- `P2PConnectionService.performConnect` 发送握手后带 5s 超时读取首个响应帧：
  - 确定性拒绝（`unpaired`/`revoked`/`device_owned_by_other`/`fingerprint_mismatch`/`invalid_pairing_code`，及 Windows `missing_device_id`）→ 清配对码（含加密持久化副本）、关闭连接、**不调度重连**，`handshakeRejection` 状态流供配对界面显示原因；
  - 成功帧（policy_update 等）→ 按原流程 CONNECTED，首帧补入消息流供 SyncManager 处理（不丢失下行策略/公告）；
  - 超时/空帧（旧版家长端无握手回执）→ 按原流程 CONNECTED。
- `invalid_pairing_code` 归入确定性拒绝：过期/无效码重试同一旧码必然再失败，重试只会放大 K3 限速级联（安全优先，且不削弱服务端爆破计数——计数在服务端仍生效）。
- `P2PMessage.fromJson` 将顶层 `error`/`error_code` 并入 payload（Web 拒绝帧无 payload 对象），payload 同名键优先。

### 6. 配对成功后清除持久化配对码（D3 根因消除）

- 握手成功后 `_pairingCode=null` 并清空加密持久化的 `relay_pairing_code`；自动重连/应用重启后的重连均免码（仅凭指纹 + 中继会话令牌放行）。
- 唯一保留配对码的窗口：本次连接尝试进行中（connect 时持久化，成功或确定性拒绝时清除）。

### 7. 家长端换账号清理（Android 家长端）

- 设置页新增「清除账号绑定与本地数据」入口，**必须家长密码验证**（复用 ParentPasswordManager；失败不可清除，且计入失败锁定计数）。原无密码的「清除全部数据」入口移除。
- 清除范围：Web 登录 JWT（xiaopacai_web_prefs）、Web 中继绑定（relay_host/port/mode/fingerprint/session_token/pairing_code）、家长端四张业务表（device_registry/parent_policies/parent_announcements/parent_usage_summary）、家长密码 → 回到「新账号绑定」状态（下次进入家长端走首次设置密码流程，由新账号设定新密码，即「以新账号为准」）；断开 P2P 连接与监听。
- **保留**：`device_id` 设备身份与儿童端表（usage_records/policy_cache/announcements/pairing_info 等）——儿童端不受此功能影响。
- 审计：新表 `parent_audit_log`（DB V5 迁移）记录 action=account_reset 与清除范围摘要，**不含密码/令牌等敏感明文**。
- 新账号登录绑定后：登录成功即 `GET /api/announcements`（Bearer 新账号 JWT）全量拉取并**先清后插**覆盖本地公告（`web-` 前缀 ID 与本地自建区分）；策略（parent_policies）属本地创作数据，清理时已清空、由新账号重新建立，服务端设备策略随设备重新归属后以 Web 侧为准。

### 8. Web 前端配套

- 解绑确认文案明确：「解绑后将清空设备归属，可用任意账号重新扫码绑定」。
- 设备列表/详情返回并展示 `ownerAccount`（绑定账号，null=无归属），便于跨账号扫码排查。

## 后果

- 正：重连免码不再误报归属；跨账号扫码返回明确错误且不限速；解绑后可被新账号重新绑定；儿童端确定性拒绝后停止重试，K3 级联根除；家长端换账号后无旧账号数据残留。
- 负：已配对设备的重连安全性完全依赖指纹比对（ADR 0006 K1 已保证）；家长端清账号后需重新绑定全部设备（预期行为）。
- 存量恢复（修复上线前）：限速窗口约 5 分钟，等待后由原绑定账号重新扫码（覆盖旧码与指纹）。

## 验收要点（Codex 执行）

1. 重连免码不再误报归属；跨账号扫码返回明确错误且不限速；
2. 解绑后可被新账号重新绑定；
3. 儿童端确定性拒绝后停止重试并显示原因；
4. 家长端清除账号绑定需密码，失败不可清除；清除后新账号绑定数据干净（公告全量替换、密码重设、策略清空）；
5. 阿里云生产部署回归。
