# 小趴菜 Web 3.0 — 儿童守护家长 Web 界面

**版本：** 3.0.0-p1  
**日期：** 2026-08-10  
**许可：** Apache-2.0  

## 项目简介

小趴菜 Web 3.0 是儿童守护系统的网页端，在 2.0（Windows 家长端 + Android 儿童端）基础上新增 **完整 Web 家长端 + 管理后端**，通过 P2P TLS 协议直接对接已有 Android 儿童端 APK。

### 核心原则
- **本地优先、不上云**：所有数据存储在本机 SQLCipher 加密数据库
- **自托管**：家长本机/LAN 部署，数据不出本机
- **儿童端兼容**：Android 儿童端 APK 不改，直连 Web 3.0 家长端

## 功能概览

### 用户前端（6 大页面）
| 页面 | 功能 |
|------|------|
| 📊 仪表盘 | 设备在线状态、今日汇总、超时统计、实时刷新（SignalR） |
| 📱 设备管理 | 儿童设备列表、配对引导、连接状态、证书指纹 |
| ⚙️ 策略配置 | 每日限额、就寝时段、分类限额、黑白名单、超时处理 |
| 📢 公告管理 | 新建/编辑/发布/撤回、优先级、有效期、实时推送 |
| 📈 使用报告 | 日报/周报、趋势图表、分类占比、导出 TXT/JSON/CSV |
| 🔧 设置 | 账号密码、通知偏好、备份恢复、端口配置 |

### 管理后端（5 个页面）
| 页面 | 功能 |
|------|------|
| 👥 账号与权限 | 家长/管理员账号、角色权限、JWT 鉴权 |
| 🖥️ 设备管理 | 设备注册/授权/解绑、在线状态总览 |
| 📋 审计日志 | 操作记录查询/导出、审计追溯 |
| 🛠️ 系统设置 | Web/P2P 端口、备份目录、数据保留策略 |
| 💾 数据管理 | 加密备份/恢复、数据清除、密钥轮换 |

## 技术栈

| 层 | 技术 |
|----|------|
| 后端 | .NET 8 / ASP.NET Core / SQLCipher / SignalR / JWT |
| 前端 | Vue 3 + TypeScript + Element Plus + Pinia + ECharts |
| 存储 | SQLite + SQLCipher 加密 |
| P2P | TLS 1.3/1.2 + 自定义 JSON 协议帧（兼容 2.0） |

## 快速开始

> 注意：50.53 环境无 .NET SDK，构建验证由 Codex@50.20 执行。

### 开发环境要求
- .NET 8 SDK
- Node.js 18+ / npm 9+

### 启动后端
```bash
cd server
dotnet restore
dotnet run
# → http://127.0.0.1:5000
# 健康检查：GET /api/health
```

### 启动前端
```bash
cd web
npm install
npm run dev
# → http://localhost:5173
# 开发代理自动转发 /api → 后端 5000
```

### 构建
```bash
# 后端
cd server && dotnet publish -c Release -o ../build/server

# 前端
cd web && npm run build
# 产物 → web/dist/
```

## 项目结构

```
xiaopacai-web/
├── docs/           # 提示词、ADR、API.md、UI_SPEC、部署说明、2.0 参考资料
├── server/         # ASP.NET Core 8 后端
├── web/            # Vue 3 + TS 前端
├── tests/          # xunit + vitest
├── build/          # 发布产物
├── README.md       # ← 本文件
├── CHANGELOG.md    # 变更日志
├── LICENSE         # Apache-2.0
├── CHECKPOINT.json # 阶段检查点
├── PROGRESS.md     # 开发进度
└── TOKEN_USAGE.md  # Token 用量记录
```

## 开发阶段

| 阶段 | 内容 | 状态 |
|------|------|------|
| P1 | 架构与骨架（目录/工程/DB Schema） | ✅ 完成 |
| P2 | 后端 API 与数据层 | 🔲 待开始 |
| P3 | 前端六大页面 + 管理端 | 🔲 待开始 |
| P4 | P2P 对接与端到端联调 | 🔲 待开始 |
| P5 | 测试、文档与打包 | 🔲 待开始 |

## 协作模式

- **Claude@50.53**：主开发（git 主仓库）
- **Codex@50.20**：主测试（本地镜像 + 构建/测试/回归）
- **桥接**：每次里程碑产出 git bundle → 信件回传缺陷

## 许可

Apache License 2.0 — 详见 [LICENSE](./LICENSE)
