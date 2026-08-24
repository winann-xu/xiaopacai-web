# App 自动更新闭环 V1（[TASK-APP-UPDATE-V1]）接口与运维文档

> 版本 v1.2.0 · 2026-08-22 · 配套 ADR：[android/docs/adr/0017-app-auto-update-v1.md](../../xiaopacai/android/docs/adr/0017-app-auto-update-v1.md)
> 范围：xiaopacai-web（server + web 前端）+ xiaopacai（android 二合一客户端）；Windows 本期仅文档化（见 §4）。

## 1. 公开检查接口（客户端/下载中心共用）

### GET /api/update/check

无鉴权，公开接口。**限频**：同一 IP 120 次/小时（`RequestRateLimiter`，超限返回 429）。

| 参数 | 必填 | 说明 |
|---|---|---|
| platform | 是 | 固定 `android`（本期仅 android；其他值返回 400） |
| abi | 是 | `arm64-v8a` / `armeabi-v7a` / `x86_64`，其他值返回 400 |
| versionCode | 是 | 客户端当前版本码（下载中心传 0 = 恒返回最新已发布版本） |
| channel | 否 | `stable`（正式签名线，缺省）/ `special`（特别版·testkey 签名线）；其他值返回 400。仅在该渠道内返回最新版本，**跨渠道永不互相推送**（防跨签名覆盖） |

响应 200（JSON）：

```json
{
  "hasUpdate": true,
  "latestVersionCode": 10200,
  "latestVersionName": "1.2.0",
  "minVersionCode": 10200,
  "force": false,
  "abiMissing": false,
  "url": "/downloads/XiaopacaiParent-1.2.0-arm64-v8a.apk",
  "sha256": "<64位小写hex>",
  "sizeBytes": 26262825,
  "changelog": "更新说明（纯文本）",
  "publishedAt": "2026-08-22T10:00:00"
}
```

语义约定：
- 无已发布版本（或该 ABI 无包）：`hasUpdate=false`，不携带 url。
- 最新版本缺当前 ABI：`hasUpdate=true` + `abiMissing=true`（客户端提示「暂不支持本设备」）。
- `force` = 客户端当前 versionCode < 最新版本 minVersionCode（服务端计算）。
- 客户端侧还有一道**防降级兜底**：latestVersionCode ≤ 本机 → 视为已最新。
- `url` 为站内相对路径；下载与清单均走 HTTPS（客户端 CloudHttp 通道，局域网内 SSL 握手失败才回退 HTTP——与既有凭据通道同策略）。

## 2. 管理端接口（admin，AdminOnly 策略 + 审计）

前置：登录态由 httpOnly Cookie（`logged_in`）+ 服务端会话判定；接口需 admin 角色。

### GET /api/admin/updates — 列出全部版本（含 draft/published）

响应：`[{ id, platform, versionName, versionCode, minVersionCode, abiUrls, abiSha256, sizeBytes, changelog, status, channel, publishedAt, createdBy, createdAt }]`；`abiUrls`/`abiSha256` 为 `{"arm64-v8a":"/downloads/..."}` 形式的 JSON 对象。

### POST /api/admin/updates — 新建草稿

请求体：

```json
{ "platform": "android", "versionName": "1.2.0", "versionCode": 10200,
  "minVersionCode": 10200, "changelog": "..." }
```

规则：
- **防降级**：versionCode 必须大于库内已有最大 versionCode，否则 400。
- `minVersionCode` 传 0 → 自动等于 versionCode（即发布即全员强制，需谨慎）。
- 新建即 `draft`；写审计日志。

### POST /api/admin/updates/{id}/upload — 上传某 ABI 安装包（multipart，draft 状态）

| 表单字段 | 说明 |
|---|---|
| abi | 三个受支持 ABI 之一 |
| file | APK 文件（**请求体上限 150MB**，Kestrel MaxRequestBodySize 已放开） |

行为：文件保存至 `wwwroot/downloads/XiaopacaiParent-{versionName}-{abi}.apk`（同名覆盖），流式计算 SHA-256 写入 `abiSha256`，记录 sizeBytes，写审计日志。

### POST /api/admin/updates/{id}/publish — 发布

规则：
- 至少需 1 个 ABI 的包（否则 400）；
- 状态置 `published` + 记录 `publishedAt`；
- **推送**：向全部在线儿童端设备 P2P 广播 `update_available`（见 §3），并审计（含在线设备数）；
- 已发布版本不可再上传/重复发布。

### 回滚/下线

无「下线」接口（审计与操作安全考虑）。回滚 = 新建一个 versionCode 更大的新版本并发布（例如回滚包版本号 1.2.1 内容同 1.1.6）。已发布版本不得删除或改版本码（防降级红线）。

## 3. P2P 推送格式（server → 儿童端）

服务端 P2pMessageHandler 在 publish 成功后向 `_sessions`（全部在线儿童端）广播：

```json
{
  "type": "update_available",
  "payload": {
    "update_id": 3,
    "version_code": 10200,
    "version_name": "1.2.0",
    "min_version_code": 10200,
    "published_at": "2026-08-22T10:00:00"
  }
}
```

**仅触发信号**：客户端收到后重新走 `/api/update/check` 取权威清单（防伪造/防降级），不信任 payload 中的下载信息。客户端行为（ADR 0017 D2/D6）：通知直达（儿童守护不被打断，不弹窗）；已开「自动下载」则后台下载+校验，完成后通知点击安装。

## 4. Windows 端（本期文档化，D5）

- 本期不提供 Windows 端自动更新接口与客户端逻辑。
- 发布 Windows 包时：人工上传至 Web 静态目录并在下载中心提供手动下载链接即可。
- 预留通道：`app_updates.platform` 字段已支持非 android 值，接口层本期仅开放 `android`；后续开放 Windows 时只需放开 platform 白名单 + 客户端侧新增下载器，服务端数据模型无需变更。

## 5. 发布操作 SOP（管理员）

1. 构建三个 ABI 的**正式签名** Release APK（签名在 Codex 构建机，与线上签名一致——换签名会触发安装失败）。
2. Web 管理后台「App 更新」→ 新建草稿（versionName/versionCode/minVersionCode/changelog）。
3. 逐个 ABI 上传对应 APK；核对页面显示的 SHA-256。
4. 全量验证：`GET /api/update/check?platform=android&abi=<每ABI>&versionCode=0` 应返回对应 url+sha256。
5. 发布：确认 minVersionCode 设置符合预期（=versionCode 即全员强制）。发布即 P2P 推送，**不可撤回**（回滚见 §2）。
6. 旧版本 APK 保留在 downloads 目录（发布历史留档）；磁盘紧张时可清理 draft 已覆盖的同名文件之外的旧版本文件（仅运维人工操作，接口不提供删除）。

> **部署前置（Nginx 反代）**：`client_max_body_size` 默认 1MB，会拦截 APK 上传（413）。
> 生产已配置 `client_max_body_size 200m;`（/etc/nginx/sites-enabled/xiaopacai-https），
> 新环境部署时需同样配置，否则 admin 上传接口不可用。

## 6. 测试与门禁

- server：`dotnet test`（tests 项目）306/306；新增 `AppUpdateTableMappingTests` 覆盖 EnsureCreated 既有库 DDL 与 EF 模型共用同一张表、ABI JSON 解析容错。
- web：`npm run build`（vue-tsc + vite）通过；下载中心与「App 更新」管理页为清单驱动。
- android：`./gradlew testDebugUnitTest` 178/178（新增 UpdateLogicTest 18 例：清单解析/频控/跳过/防降级/SHA-256/下载校验）；`assembleDebug` 三 ABI 通过；Release 编译通过（签名 APK 由 Codex 构建机出）。
