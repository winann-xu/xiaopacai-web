# ADR 0009：账号体系重构——单邮箱账号 + 云端统一认证 + 儿童设备强制归属（TASK-ACCOUNT-V1）

- 日期：2026-08-15
- 状态：已接受
- 相关：ADR 0006/0007（指纹身份锚/扫码绑定）、SECURITY_BASELINE.md、125 信标准提示词 V1.0、126 信邮件设置页
- 任务：[TASK-ACCOUNT-V1] / 子任务 [TASK-ACCOUNT-V1-MAILCONFIG]（产品负责人已定稿，不中途询问）

## 背景

现状是「本地双角色 + 多账号测试体系」：users.Username 为任意字符串、种子账号 admin123/parent123、Web 支持用户名登录、reset-ticket 线上恢复码、Android 端本地 PBKDF2 家长密码（ParentPasswordManager）与 Web JWT 双轨并存、设备可无主（OwnerUserId 可空）。产品负责人定稿：统一为**单邮箱账号 + 云端统一认证 + 儿童设备强制归属 + 离线不降级**。本期范围 Web + Android；Windows/iOS/TV 仅预留接口。

## 不可违背原则（产品定稿，逐条落地映射）

P1 单邮箱账号全平台统一 → A1；P2 取消本地免密入口 → C1/C2；P3 儿童端强制归属 → A5/C4；P4 仅邮箱验证码注册/登录辅助/找回 → A2/A3/A4；P5 离线不降级 → C5；P6 取消冲突功能 → A4/C1 退役项；P7 admin 邮箱化 → A1/A7；P8 家长模式每次输密码 → C2；P9 儿童端系统级菜单密码门禁 → C3；P10 不限设备数 + >10 预警 → A6/B3；P11 解绑/换绑需密码验证、儿童端无解绑 → A5/B2/C4。

## 决策

### A1. 账号模型：Username 即邮箱（唯一），admin 邮箱化

- `users.Username` 统一存邮箱（小写归一），唯一索引；`Email` 列保留但恒等于 Username（兼容旧代码）。
- 登录/注册/找回一律以邮箱为账号；非邮箱账号形态废弃（种子账号 admin/parent 删除）。
- admin 固定邮箱账号（如 `admin@xiaopacai.com`，挂 Role=admin），与家长账号同表隔离（Role 区分）。
- 种子迁移：启动时若不存在任何 admin 且环境变量 `ADMIN_EMAIL`/`ADMIN_INITIAL_PASSWORD` 已配置则创建（MustChangePassword=true）；未配置则告警不创建。上线清库由 Codex 执行（生产为明文 SQLite 测试数据）。

### A2. 注册（仅邮箱 + 验证码）

- `POST /api/auth/email-code` `{email, purpose}`（purpose ∈ register|login|reset_password）：发送 6 位验证码，5 分钟有效、单码单用（per email+purpose 一码，重发作废旧码）。
- `POST /api/auth/register` `{email, code, password}`：校验码后创建账号（Role=parent），成功签发 JWT 自动登录。
- 限速：发码 per-IP 10 次/小时 + per-email 5 次/小时；验证/注册 per-email 10 次/小时（防爆破）。
- 邮件未配置 → 发码接口 503 明确错误（不阻断登录等其余功能）。
- 注册成功审计；验证码本身不落审计/日志。

### A3. 登录（邮箱+密码为主，验证码辅助）

- `POST /api/auth/login` 登录名仅接受邮箱（不再支持任意用户名；现状兼容分支删除）。
- `POST /api/auth/login/code` `{email, code}` 验证码登录（辅助，签发同等 JWT）。
- 失败限速沿用（5 次/小时，user+IP 双维）；虚拟哈希防枚举沿用；成功清计数。
- JWT 签名密钥走环境变量（现状不变）；RefreshToken 机制不变。
- login-ticket（Web 扫码登录）链路保留——它依赖已登录设备确认，不违反 P2。

### A4. 找回密码（验证码重置）

- `POST /api/auth/password-reset` `{email, code, newPassword}`（code 为 purpose=reset_password 的验证码）。
- 成功后吊销该账号全部 RefreshToken（防旧会话延续）。
- **退役**：`reset-ticket` 全链路（线上恢复码）接口删除（P4/P6）；MustChangePassword 强制改密保留。

### A5. 设备绑定/解绑/换绑（强制归属 + 密码验证）

- 绑定：二维码/配对码生成仅限已登录家长（现状已如此）；儿童端扫码后设备唯一归属该账号（现状 + `OwnerUserId` 非空强制，启动时清理孤儿设备行——devices 无主行在服务启动任务中迁移归并/删除，避免逻辑依赖无主态）。
- 解绑/换绑：新增 `POST /api/auth/verify-password` `{password}`（登录态验证当前账号密码）→ 返回 5 分钟一次性 action token；`DevicesController.Unpair` 必须携带该 token（Header `X-Action-Token`），无/过期 → 401。解绑即释放归属（沿用 D1：清 OwnerUserId+PairCode）。
- 儿童端无解绑接口（现状即无，明确不提供）。

### A6. 多设备

- 服务端不设设备数上限；`DevicesController.List` 响应带 `deviceCount`；前端 >10 台预警（不阻断）。

### A7. 邮件服务 + admin 邮件设置页（126 信子任务 MAILCONFIG）

- `IMailSender` 抽象 + 两个实现：`DirectMailApiSender`（阿里云 DirectMail API，RAM AccessKey 签名）与 `SmtpSender`（标准 SMTP）。
- 配置来源优先级：**数据库 mail_config（admin 页面配置）优先；未配置时回退环境变量**（`MAIL_*`，部署说明给出全部变量名）；两者皆无 → 发码/找回/测试发送返回明确错误，不阻断登录。
- 新表 `mail_config`：channel(api|smtp)、AccessKeyId、AccessKeySecret(密文)、FromDomain/FromAddress/FromName、SmtpHost/SmtpPort/SmtpUser/SmtpPassword(密文)、UpdatedAt。
- **Secret 加密存储**：服务端主密钥 = 环境变量 `XIAOPACAI_MASTER_KEY`（32 字节 hex），AES-256-GCM 加密 Secret 字段；未配置主密钥时拒绝保存 Secret 类配置并提示（不降级明文入库）。**禁止明文入库/入仓**。
- 接口（仅 admin）：`GET /api/admin/mail-config`（Secret 回显脱敏：「已设置」/null）；`PUT /api/admin/mail-config`（Secret 留空=保持不变）；`POST /api/admin/mail-config/test` `{to}`（发送测试邮件，返回结果与最近一次结果）；限速 + 审计（不含 Secret 明文）。保存后立即生效（MailSender 配置热加载）。
- 注册验证码/登录辅助/找回均读该配置。

### A8. 安全与审计

- 验证码/注册/登录/找回/解绑/邮件配置全程限速（RequestRateLimiter 复用）+ 审计落库；验证码、密码、令牌、Secret 明文一律不入审计/日志/响应。
- 密码哈希沿用 Argon2（PasswordHasher 现状）；JWT 密钥、主密钥走环境变量。
- 解绑 action token 单次有效、5 分钟过期，绑定 userId 防跨账号使用。

### B. Web 前端

- B1 认证页重构：`/login` 邮箱+密码主流程 + 「验证码登录」tab + 「注册」（发码→注册两步）+ 「忘记密码」（发码→重置两步）；删除手机号/恢复码入口。
- B2 解绑交互：确认弹窗内加「账号密码」输入 → verify-password → 携带 action token 调 Unpair。
- B3 设备列表：deviceCount>10 显示预警条。
- B4 管理后台「系统设置 → 邮件设置」页：通道二选一表单、Secret 脱敏回显与留空语义、保存/测试发送、配置状态与最近测试结果展示、页内「如何开通阿里云邮件推送」说明文案（发信域名+DNS 验证+发信地址+RAM AccessKey）。

### C. Android

- C1 **退役本地密码体系**：ParentPasswordManager（本地 PBKDF2 密码/恢复码/锁定）、本地登录页全部移除；`ParentAccountReset` 的本地密码验证改为**云端账号密码验证**（调 login 接口；离线时拒绝清除——安全优先）。
- C2 **家长模式每次密码**：进入/切回/App 重启后必须输入邮箱+密码 → 云端 login 验证 → 会话态仅在内存（进程内本次进入有效）；JWT 仍存 xiaopacai_web_prefs 但**仅用于云端同步，不能免密进入家长模式**；记住的仅为账号标识（邮箱），密码不落盘。
- C3 **儿童端系统级菜单门禁**：设置、权限引导、无障碍/使用统计开关、卸载保护、设备管理器、退出守护 → 统一 `SystemGateDialog`（邮箱预填+密码输入 → 云端 login 验证）每次通过。
- C4 儿童端绑定：状态/设置展示绑定账号（ownerAccount）；无解绑入口。
- C5 离线语义：策略/公告/分类/限额本地缓存照常执行（现状已满足）；恢复后增量同步 + usage_report 补报（现状 SyncManager batch 已满足，文档化承诺）；离线仅停止同步。
- C6 Windows/iOS/TV：仅文档预留接口说明，不改实现。

## 后果

- 正：单一邮箱身份、强制归属、无本地免密后门（P2 全平台生效）；解绑需密码验证防误操作与未授权解绑；邮件配置自助化且密钥加密。
- 负：离线时无法验证密码 → 清账号、系统级菜单放行均不可用（安全优先的预期代价）；迁移期旧账号/测试账号作废（上线清库已由 Codex 执行）。
- 风险与缓解：验证码邮件送达依赖 DirectMail 开通（未开通时注册/找回明确报错，登录不受影响）；主密钥缺失时 Secret 类配置不可保存（显式提示）。

## 验收要点（Codex 执行，125/126 信标准）

1. 邮箱注册→验证码→登录→找回全链路（阿里云邮件推送实测送达）；
2. 家长端每次进入必须账号密码；错误密码拒绝且限速；
3. 儿童端所有系统级菜单均需账号密码；
4. 儿童端只能绑定一个家长账号；换绑/解绑需账号密码；解绑后可重绑；
5. 单账号多设备（≥2 台模拟器）策略/公告同步一致；>10 台预警生效；
6. 离线：断网后策略/公告照常执行；恢复后增量同步+补报；
7. admin 邮箱账号登录后台可用，与用户账号隔离；
8. 全量回归：Web 单测、Android 单测（120+）、Windows 单测（15）、npm build、Release APK 构建；
9. 安全：验证码爆破限速、审计留痕、无明文密钥入库入仓；
10. 邮件设置页：admin 配置/修改/脱敏回显/测试发送/未配置降级/权限隔离。

## 实现记录（[TASK-ACCOUNT-V1] 双端落地，2026-08-15）

- **服务端**（f3fef09）与 **Web 前端**（8a84375）按 A/B 节落地：邮箱注册/验证码登录/找回、verify-password + X-Action-Token 解绑、{devices,deviceCount} 响应、admin 邮件设置页（DB mail_config → 环境变量 MAIL_* 兜底、Secret 经 XIAOPACAI_MASTER_KEY AES-256-GCM 加密）。部署说明见 docs/deployment-account-v1.md。
- **Android**（本仓库）：
  - ParentPasswordManager/ParentLoginScreen 本地密码、恢复码、修改密码入口全部移除；RoleManager 仅存角色状态，验证职责移交 `CloudAccountManager`（POST /api/auth/login，JWT KeyStore 加密、密码不落盘、只记邮箱）。
  - 家长登录态仅进程会话内（parentLoggedIn 不持久化）——进入/切回/重启一律云端验证；新增 `web_host`/`web_port` 持久化（家长登录页/中继设置页保存），供儿童端门禁复用服务器地址。
  - 儿童端统一 `SystemGateDialog`（守护设置/权限管理/应用分类/解除设备管理器/切换到儿童端/换账号清理共用），每次云端验证，离线明确拒绝并提示「需要联网」。
  - 绑定账号展示落在家长端（设置页 Web 账号卡片显示邮箱 + 退出登录）。**偏离 C4**：儿童端本地无归属邮箱数据源（配对/中继握手不携带 owner 邮箱），展示由 Web 端设备详情承担（服务端 devices 响应已含 ownerAccount）；Android 侧不展示，属文档化取舍。
  - **门禁验证语义**：云端登录成功即视为通过（自托管家庭场景：能登录本服务器账号者即家庭成员）；未强制要求与绑定邮箱一致——设备级归属仍由服务端 P2P 握手指纹/中继会话绑定保证（红线 R3.x 不受影响）。
  - 离线语义（C5）：策略/公告/限额/分类本地缓存照常执行；usage_records sync_status=0 增量补报、诊断报告缓存补传（既有机制，未改动）；离线仅影响验证类操作（家长模式进入、系统级门禁、换账号清理）。
  - 单测：新增 CloudAccountManagerTest（6 用例：服务器地址/邮箱绑定/未配置服务器/401/离线拒绝），重写 ParentAccountResetTest（4 用例：云端验证失败/离线/未配置服务器拒绝清除 + 清除仅移除凭据保留服务器配置）；删除 ParentPasswordManagerTest。
