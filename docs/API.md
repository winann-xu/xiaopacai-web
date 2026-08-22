# 小趴菜 Web 3.0 API 文档

版本：3.0.0-opt12-p1 | 基路径：`http://127.0.0.1:5000/api`

## 认证说明

- 认证方式：JWT Bearer Token
- 获取 Token：`POST /api/auth/login`
- Token 过期：Access Token 60 分钟，Refresh Token 7 天
- 刷新 Token：`POST /api/auth/refresh`
- 在请求头中携带：`Authorization: Bearer <access_token>`
- 测试账号（种子数据）：admin / admin123（管理员角色）

## 已实现接口

### 1. 认证（Auth）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| POST | `/api/auth/login` | 无 | 用户登录，返回 accessToken + refreshToken + profile |
| POST | `/api/auth/logout` | Bearer | 登出，吊销 refresh token |
| POST | `/api/auth/refresh` | 无 | 刷新 access token |
| POST | `/api/auth/change-password` | Bearer | 修改当前用户密码 |
| GET | `/api/auth/me` | Bearer | 获取当前用户信息（角色/权限） |

**POST /api/auth/login**
```json
// 请求
{ "username": "admin", "password": "admin123" }
// 响应 200
{
  "accessToken": "eyJhbG...",
  "refreshToken": "dGhpcyBp...",
  "expiresAt": "2026-08-12T10:30:00Z",
  "profile": { "username": "admin", "role": "admin", "displayName": "管理员" }
}
```

**POST /api/auth/change-password**
```json
// 请求（需 Bearer）
{ "oldPassword": "admin123", "newPassword": "newPassword456" }
// 响应 200
{ "message": "密码已修改" }
```

### 2. 健康检查（Health）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| GET | `/api/health` | 无 | 服务健康检查，返回 `{ "status": "healthy", "timestamp": "..." }` |

### 3. P2P 配对（Pairing）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| POST | `/api/pairing/generate-code` | Bearer (ParentOrAdmin) | 生成 6 位配对码（5 分钟有效期） |
| POST | `/api/pairing/verify` | Bearer (ParentOrAdmin) | 校验配对码并绑定设备 |
| POST | `/api/pairing/cancel` | Bearer (ParentOrAdmin) | 取消当前配对码 |

**POST /api/pairing/generate-code**
```json
// 请求
{ "deviceName": "小明手机" }
// 响应 200
{ "pairingCode": "123456", "expiresAt": 1723449600, "certFingerprint": "a1b2c3..." }
```

**POST /api/pairing/verify**
```json
// 请求
{ "pairingCode": "123456", "deviceId": "abc123", "deviceName": "小明手机" }
// 响应 200
{ "status": "verified", "deviceId": "abc123" }
// 响应 400（配对码错误/过期）
{ "status": "rejected", "reason": "配对码错误或已过期" }
```

### 4. OPT12 扩展接口（协议与数据模型扩展，P1 已实现）

#### 扫码登录（需求 10）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| POST | `/api/auth/login-ticket` | 无 | 生成一次性扫码登录 Ticket（90 秒有效，状态 pending） |
| GET | `/api/auth/login-ticket/{ticket}` | 无 | 轮询状态 pending/confirmed/expired；confirmed 时首次返回 JWT |
| POST | `/api/auth/login-ticket/{ticket}/confirm` | Bearer | 家长端 APP 确认扫码登录 |

#### 忘记密码重置（需求 12）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| POST | `/api/auth/reset-ticket` | 无 | 生成一次性重置 Ticket（10 分钟有效，绑定目标账号） |
| GET | `/api/auth/reset-ticket/{ticket}` | 无 | 轮询状态 pending/confirmed/expired |
| POST | `/api/auth/reset-ticket/{ticket}/confirm` | Bearer | 家长端 APP 确认身份（须与目标账号一致） |
| POST | `/api/auth/reset-ticket/{ticket}/reset` | 无（凭证=已确认 Ticket） | 设置新密码，吊销全部 Refresh Token |

**POST /api/auth/reset-ticket**
```json
// 请求
{ "username": "parent001" }
// 响应 200
{ "ticket": "a1b2c3...", "status": "pending", "expiresAt": "2026-08-11T10:00:00Z", "expiresInSeconds": 600 }
```

#### 故障诊断（需求 5）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| POST | `/api/diagnostics` | 无 | 儿童端上报诊断信息（device_id 必填，其余可选） |
| GET | `/api/admin/diagnostics` | AdminOnly | 列表/筛选（?deviceId=&from=&to=&limit=） |
| GET | `/api/admin/diagnostics/export` | AdminOnly | 导出筛选结果为 JSON 文件 |

#### 云端中继（需求 3）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| GET | `/api/relay/sessions` | AdminOnly | 中继会话列表（?status=&role=&limit=） |

#### 应用分类（需求 1）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| GET | `/api/devices/{id}/app-categories` | Bearer (ParentOrAdmin) | 查看设备应用分类列表 |
| PUT | `/api/devices/{id}/app-categories` | Bearer (ParentOrAdmin) | 全量保存应用分类（category 限 game/social/video/learning/other） |

**PUT /api/devices/{id}/app-categories**
```json
// 请求
{ "categories": [ { "packageName": "com.game.xxx", "appName": "某游戏", "category": "game" } ] }
// 响应 200
{ "deviceId": "XP-...", "categories": [...], "message": "应用分类已保存" }
```

### 5. App 更新（[TASK-APP-UPDATE-V1]，v1.2.0）

| 方法 | 路径 | 鉴权 | 说明 |
|------|------|------|------|
| GET | `/api/update/check?platform=android&abi={abi}&versionCode={vc}` | 公开（IP 限频 120/h） | 检查更新，返回清单+sha256+force |
| GET | `/api/admin/updates` | AdminOnly | 版本列表（含草稿） |
| POST | `/api/admin/updates` | AdminOnly | 新建草稿（versionCode 防降级校验） |
| POST | `/api/admin/updates/{id}/upload` | AdminOnly | 上传某 ABI APK（≤150MB，流式 SHA-256） |
| POST | `/api/admin/updates/{id}/publish` | AdminOnly | 发布 + P2P 广播 update_available + 审计 |

完整字段语义、P2P 推送格式、回滚方式与发布 SOP 见 [app-update-v1.md](app-update-v1.md)。

## 规划中接口（前端当前使用 Mock 数据）

以下接口在 PROMPT_3.0.md 中定义，尚未实现：

### 设备管理（Devices）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/devices` | 获取已配对设备列表 |
| GET | `/api/devices/{id}` | 获取设备详情 |
| DELETE | `/api/devices/{id}` | 解绑设备 |

### 策略配置（Policies）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/policies` | 获取当前策略配置 |
| PUT | `/api/policies` | 保存并下发策略 |
| GET | `/api/policies/history` | 策略变更历史 |

### 公告管理（Announcements）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/announcements` | 获取公告列表 |
| POST | `/api/announcements` | 新建公告 |
| PUT | `/api/announcements/{id}` | 编辑公告 |
| POST | `/api/announcements/{id}/publish` | 发布公告 |
| POST | `/api/announcements/{id}/revoke` | 撤回公告 |
| DELETE | `/api/announcements/{id}` | 删除公告 |

### 使用报告（Reports）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/reports/daily` | 日报（?date=yyyy-MM-dd） |
| GET | `/api/reports/weekly` | 周报（?from=&to=） |
| GET | `/api/reports/export` | 导出报告（?format=json/csv） |

### 设置（Settings）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/settings` | 获取系统设置 |
| PUT | `/api/settings` | 更新系统设置 |
| POST | `/api/settings/backup` | 创建加密备份 |
| POST | `/api/settings/restore` | 恢复备份 |

### 管理后台（Admin）— 规划中
| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/admin/users` | 用户列表 |
| POST | `/api/admin/users` | 创建用户 |
| PUT | `/api/admin/users/{id}` | 编辑用户 |
| DELETE | `/api/admin/users/{id}` | 删除用户 |
| GET | `/api/admin/audit-logs` | 审计日志查询 |

## 错误响应格式

```json
{
  "error": "错误描述",
  "code": "ERROR_CODE",
  "details": "详细说明（可选）"
}
```

## HTTP 状态码

| 状态码 | 含义 |
|--------|------|
| 200 | 成功 |
| 400 | 请求参数错误 |
| 401 | 未认证或 Token 过期 |
| 403 | 权限不足 |
| 404 | 资源不存在 |
| 500 | 服务器内部错误 |
