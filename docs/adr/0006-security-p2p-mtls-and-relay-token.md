# ADR 0006：P2P 双向 TLS（mTLS）+ 中继会话令牌（安全基线 K1/K2/K3 修复）

- 日期：2026-08-14
- 状态：已接受
- 相关：SECURITY_BASELINE.md R2.3/R3.2/R3.3、PROMPT_SECURITY_TEST.md K1/K2/K3
- 任务：TASK-PRELAUNCH 安全测试（Codex 101 信授权）

## 背景

安全测试确认三项 P0 缺陷：

- **K1**：P2P 握手从未校验证书指纹，且已配对设备会**静默用攻击者证书覆盖**存储指纹 → 中间人/冒充任意设备（违反红线 R3.2"禁止接受任意客户端证书"）。
- **K2**：家长端中继 P2P 握手零鉴权——任何客户端声明 `parent-XXX` deviceId 即进入 `_sessions` 路由表，接收中继转发的儿童数据（违反红线 R2.3）。
- **K3**：P2P 握手无失败限速，6 位配对码可被 TCP 直连爆破（违反红线 R4.2）。

## 决策

### 1. mTLS 客户端证书（K1）

- **Android 客户端**（P2PConnectionService）：首次运行生成 EC P-256 自签名**客户端身份证书**（BouncyCastle，PKCS12 持久化于 app 私有目录，重启指纹稳定），TLS 握手时以 KeyManager 提交（mTLS）。
- **Web 服务端**（P2pListenerService）：`ClientCertificateRequired = true`——无客户端证书的旧版客户端 TLS 握手直接失败。
- **握手层**（P2pMessageHandler）：顶部守卫强制 `peerFingerprint` 非空（防御 TLS 配置回退）；已配对设备指纹必须匹配否则拒绝（且**先于任何状态更新**）；无指纹记录的历史设备 TOFU 采纳；凭新配对码重新绑定 = 信任轮换（采纳新指纹）。
- **废除 payload 自报指纹信任路径**：`req.CertFingerprint` 仅作协议字段保留，不作为信任依据（可伪造）。

### 2. 中继会话令牌 + 指纹双重绑定（K2）

- `/api/relay/register`（JWT 鉴权）签发 64 位十六进制随机 `sessionToken`（每次注册轮换），并持久化注册方提交的客户端证书指纹到 `relay_sessions`。
- 家长端 P2P 握手必须携带 `sessionToken` 且其 TLS 客户端证书指纹与注册绑定指纹一致（`CryptographicOperations.FixedTimeEquals` 常数时间比对 + OrdinalIgnoreCase 指纹比对）。
- 令牌只出现在注册响应一次，服务端不写日志、列表接口不回传（红线 R8.3）。
- Android 家长端：注册响应解析令牌 → 传入 connect → 握手携带 → 随 `guardian_prefs` 持久化供自动重连复用。

### 3. 握手失败限速（K3）

- IP 级：10 次失败/5 分钟（`p2p-handshake:ip:{ip}`），超限临时拒绝。
- 配对码级：10 次失败/5 分钟（`p2p-paircode:{code}`），防 10^6 爆破。
- 所有拒绝路径统一走 `RejectHandshakeAsync`：记失败计数 + 写 AuditLog（action=p2p.handshake_reject）。

### 4. 客户端服务端证书固定（R3.3 补强）

- Android 对 Web 中继服务端证书：首次 TOFU 采纳后**立即持久化**，后续连接（含自动重连）固定比对。

## 影响与迁移

- **协议变更**：handshake 帧新增 `sessionToken` 字段（仅家长端中继）；TLS 层要求客户端证书。儿童端/家长端 Android 需同步升级（本次已随客户端改动发布）。
- **测试设备**：历史配对设备若 `devices.cert_fingerprint` 为空 → 首次重连自动 TOFU，无感；若有旧指纹记录（测试夹具）→ 握手被拒，需管理员吊销后凭新配对码重新绑定（信任轮换）。
- **Windows 家长端**：经查不调用 `/api/relay/register`、不以 parent- 身份连 Web 中继，不受影响；儿童端 mTLS 证书对其 LAN 监听透明（应用层指纹兼容 null→"unknown"）。
- **公网部署前置**：本 ADR 实施前，公网环境禁止开放 9527 端口（K1/K2 均为 P0）。

## 回归证据

- 测试：tests/P2pHandshakeFlowTests.cs 新增 9 例（K1×4、K2×5、K3×1 内嵌于已有用例），既有用例全部适配 mTLS（补 peerFingerprint）。
- 全量 dotnet test 与 Android testDebugUnitTest 由 Codex 执行并回信确认。
