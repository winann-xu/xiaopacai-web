# ACCOUNT-V1 上线部署说明（ADR 0009）

> 适用版本：本仓库 f3fef09 之后。上线 = 服务端（.NET 8）+ Web 前端 + Android 双 bundle 全部替换后重启。

## 一、新增环境变量

| 变量 | 必填 | 说明 |
|---|---|---|
| `ADMIN_EMAIL` | 首次启动必填 | 管理员引导账号邮箱。仅当 users 表为空时生效 |
| `ADMIN_INITIAL_PASSWORD` | 首次启动必填 | 管理员初始密码，须通过密码策略（≥8 位、含字母与数字）。首次登录强制改密 |
| `XIAOPACAI_MASTER_KEY` | 要保存邮件 Secret 时必填 | 64 位 hex（32 字节），用于 AES-256-GCM 加密 AccessKeySecret / SMTP 密码。生成：`openssl rand -hex 32`。未配置时邮件设置页仍可保存非 Secret 字段，但保存 Secret 会被拒绝（400） |
| `MAIL_CHANNEL` | 否 | `api`（阿里云 DirectMail）或 `smtp`。数据库有完整配置时数据库优先 |
| `MAIL_ACCESS_KEY_ID` / `MAIL_ACCESS_KEY_SECRET` | 否 | DirectMail 凭据（仅当无 DB 配置时兜底） |
| `MAIL_FROM_ADDRESS` / `MAIL_FROM_NAME` | 否 | 发件人地址/名称（DirectMail 需已验证发信域名） |
| `MAIL_SMTP_HOST` / `MAIL_SMTP_PORT` / `MAIL_SMTP_USER` / `MAIL_SMTP_PASSWORD` / `MAIL_SMTP_USE_SSL` | 否 | SMTP 兜底配置（端口默认 587，SSL 默认 true） |

**配置优先级**：数据库 `mail_config`（管理端「邮件设置」页）→ 环境变量 `MAIL_*` → 邮件未配置（验证码相关接口返回 503，密码登录不受影响）。

## 二、上线执行顺序（重要，不可颠倒）

1. **备份**：老数据库（sqlite 文件）整份复制留档，再开始升级。
2. **部署新服务端**（.NET 8 publish）+ **部署新 Web 前端**（dist/）。
3. **设置环境变量**：`XIAOPACAI_MASTER_KEY`、`ADMIN_EMAIL`、`ADMIN_INITIAL_PASSWORD`（三者为首次启动关键）。
4. **清库启动**（二选一）：
   - **全新部署**：删除旧数据库文件后启动 → 仅当 `ADMIN_EMAIL` + `ADMIN_INITIAL_PASSWORD` 齐全且密码策略通过时才创建 admin（`MustChangePassword=true`）；不齐全则**拒绝创建任何账号**（安全优先，登录前须先配好变量）。
   - **带旧库升级**：不清库直接启动。旧 `admin/admin123`、`parent/parent123` 种子账号不再创建，但**已有账号保留**。旧账号若 Email 列为空，则无法用邮箱验证码/找回密码（密码登录用旧用户名仍可登录——服务端对 login 做了 Email 列兜底匹配）。建议上线后立即：admin 登录 → 账号管理 → 为老账号补邮箱 → 修改弱口令。
5. **验证清单**：
   - admin 首次登录被强制改密（跳转设置页）；
   - 管理端「系统设置 → 邮件设置」：配置通道 → 发测试邮件成功；
   - 注册两步（验证码邮件到达、注册后自动登录）；
   - 忘记密码两步（重置后所有旧 refresh token 吊销）；
   - 设备解绑需输入登录密码二次验证；
   - 儿童端孤儿设备启动自动转为未配对（审计日志 `orphan_device_cleanup`）。

## 三、接口速查

| 端点 | 说明 |
|---|---|
| `POST /api/auth/email-code` | `{email, purpose∈register\|login\|reset_password}`；6 位码 300s 有效、单码单用、重发作废；发码限速 IP 10/hr + 邮箱 5/hr；验证限速邮箱 10/hr；邮件未配置 → 503 |
| `POST /api/auth/register` | `{email, code, password, displayName}`；邮箱小写归一入库 |
| `POST /api/auth/login` | `{username(即邮箱), password}`；统一错误文案「邮箱或密码错误」防枚举 |
| `POST /api/auth/login/code` | `{email, code}`；失败限速 5/hr（user+IP） |
| `POST /api/auth/password-reset` | `{email, code, newPassword}`；成功后 RevokeAllUserTokens + 清除强制改密标记 |
| `POST /api/auth/verify-password` | 需登录；`{password}` → 一次性 `actionToken`（5 分钟、绑定 userId） |
| `DELETE /api/devices/{id}` | 必须带 `X-Action-Token` 头（verify-password 签发），无/过期/跨账号 → 401 |
| `GET /api/devices` | 响应改为 `{devices, deviceCount}`（旧数组响应不再返回） |
| `GET/PUT /api/admin/mail-config` | 仅 admin；Secret 脱敏回显「已设置」；Secret 留空=不变 |
| `POST /api/admin/mail-config/test` | `{to}` 发测试邮件，记录 LastTest* |

## 四、安全红线复核（发布前必查）

- [ ] 主密钥 `XIAOPACAI_MASTER_KEY` 已配置且与旧值不同（如曾用过旧变量名）
- [ ] admin 引导密码不含在代码/文档/信件的明文中
- [ ] 数据库文件权限仅服务进程可读写
- [ ] 邮件 Secret 在 DB 中以 `v1:` 前缀密文存储（可 `sqlite3` 抽查）
- [ ] 审计日志中不存在 `email_code_unconfigured` 之外的异常大量记录（防枚举告警）

## 五、回滚

- 服务端/前端回滚到上一版本 bundle 即可；新账号体系写入的数据（users.Email、mail_config）不影响旧版本运行（旧版忽略新列）。
- 注意：旧版本无 `X-Action-Token` 校验，回滚期间解绑防护失效——仅在紧急回滚时接受。
