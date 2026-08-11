# 小趴菜 Web 3.0 API 文档

版本：3.0.0-p5 | 基路径：`http://127.0.0.1:5000/api`

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
