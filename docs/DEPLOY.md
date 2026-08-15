# 小趴菜 Web 3.0 部署指南

版本：3.0.0-p5 | 适用平台：Linux (Ubuntu 22.04+) / Windows 10+

## 部署记录：v1.1.0（阿里云，2026-08-15）

- Git tag：`v1.1.0`（android tag 对应 commit 453cba3；web tag 对应 commit 47865fb）
- 部署 commit：android `453cba3` / web `47865fb`（另含本仓库后续发布补丁：下载中心文件名 1.0.0→1.1.0、DEPLOY 记录）
- 部署时间：2026-08-15 19:20（CST）阿里云 8.217.165.122
- 环境变量变更：无（沿用 /etc/xiaopacai-web.env）
- 下载中心：XiaopacaiParent-1.1.0-{arm64-v8a,armeabi-v7a,x86_64}.apk（versionName 1.1.0 / versionCode 10100）
- 回滚点：tag `v1.1.0`；备份 /opt/xiaopacai/app.bak-20260815-192036
- 验收：Web 294/294、Android 137/137、Windows 15/15、npm build 通过；生产 health/login/下载 200

## 部署记录：v1.1.1（阿里云，2026-08-15，[TASK-HARDENING-V1.1.1]）

- Git tag：`v1.1.1`（android tag 对应 commit 69cf9e2；web tag 对应 commit 931abce）
- 部署 commit：android `69cf9e2` / web `931abce`（另含本仓库后续发布补丁：下载中心文件名 1.1.0→1.1.1、DEPLOY 记录）
- 部署时间：2026-08-15 22:53（CST）阿里云 8.217.165.122
- 环境变量变更：无（沿用 /etc/xiaopacai-web.env）
- 下载中心：XiaopacaiParent-1.1.1-{arm64-v8a,armeabi-v7a,x86_64}.apk（versionName 1.1.1 / versionCode 10101），旧 1.1.0 安装包已移除
- 回滚点：tag `v1.1.1`；备份 /opt/xiaopacai/app.bak-20260815-225329
- 验收：Web 303/303、Android 154/154、Windows 15/15、npm build 通过；生产 health/login 200、/api/logs 200（原 500 根因修复）、/api/guard-events 200、下载 200

## 一、环境要求

| 组件 | 最低版本 | 说明 |
|------|---------|------|
| .NET SDK | 8.0 | 构建后端（运行时 .NET 8 Runtime 即可运行已发布包） |
| Node.js | 18+ | 构建前端 |
| npm | 9+ | 包管理 |
| SQLite | 3.35+ | 数据库（SQLCipher 加密） |

## 二、快速启动（开发模式）

```bash
# 1. 克隆仓库
git clone https://github.com/winann-xu/xiaopacai-web.git
cd xiaopacai-web

# 2. 构建后端
cd server
dotnet restore
dotnet build -c Release

# 3. 构建前端
cd ../web
npm install
npm run build

# 4. 启动服务（开发模式，前后端分离）
# 终端 1 — 后端
cd ../server
dotnet run
# 后端启动于 http://localhost:5000

# 终端 2 — 前端开发服务器
cd ../web
npm run dev
# 前端启动于 http://localhost:5173，自动代理 API 到 5000
```

## 三、生产部署（单进程自托管）

### 3.1 发布构建

```bash
# 发布后端（生成自包含可执行文件）
cd server
dotnet publish -c Release -o ../build/server

# 构建前端并复制到发布目录
cd ../web
npm run build
cp -r dist ../build/server/wwwroot
```

### 3.2 直接运行（仅限内网测试）

```bash
cd build/server
./XiaopacaiWeb --urls "http://0.0.0.0:5000"
```

访问 `http://<服务器IP>:5000` 即可使用。

> ⚠️ [SEC-K6] HTTP 直连仅允许用于内网联调测试。公网/生产环境必须启用 HTTPS
> （3.4 Nginx TLS 终结 或 四.4 Kestrel 直连 HTTPS），否则认证 Cookie 无法携带
> `Secure` 标记，登录凭据可被明文嗅探 —— 违反红线 R4.1，禁止上线。

### 3.3 systemd 服务（Linux 推荐）

```bash
# [SEC-K4] 密钥放环境变量文件（而非 appsettings.json 明文），权限 0600 仅服务账号可读
sudo tee /etc/xiaopacai-web/env << 'EOF'
Jwt__SecretKey=<openssl rand -base64 48 的输出>
Database__Password=<openssl rand -base64 32 的输出>
P2P__CertPassword=<openssl rand -base64 24 的输出>
EOF
sudo chmod 600 /etc/xiaopacai-web/env
sudo chown root:xiaopacai /etc/xiaopacai-web/env

sudo tee /etc/systemd/system/xiaopacai-web.service << 'EOF'
[Unit]
Description=小趴菜 Web 3.0 家长端服务
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/xiaopacai-web
ExecStart=/opt/xiaopacai-web/XiaopacaiWeb --urls "http://127.0.0.1:5000"
Restart=on-failure
RestartSec=10
User=xiaopacai
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/xiaopacai-web/env

# [SEC-K4] 安全加固：最小权限 + 文件系统只读（数据目录单独放行）
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=full
ReadWritePaths=/opt/xiaopacai-web/Data /opt/xiaopacai-web/logs
UMask=0077

[Install]
WantedBy=multi-user.target
EOF

# [SEC-K4] 数据文件权限收口：目录 700、密钥/数据库文件 600
sudo mkdir -p /opt/xiaopacai-web/Data /opt/xiaopacai-web/logs
sudo chown -R xiaopacai:xiaopacai /opt/xiaopacai-web
sudo chmod 700 /opt/xiaopacai-web/Data
find /opt/xiaopacai-web/Data -type f -exec sudo chmod 600 {} \;

sudo systemctl daemon-reload
sudo systemctl enable xiaopacai-web
sudo systemctl start xiaopacai-web
```

> 服务默认绑定 `127.0.0.1:5000`，不对外网开放；对外入口是 3.4 的 Nginx 443。
> 生产环境若检测到 `Jwt:SecretKey` 为默认值/占位值（如 CHANGE-ME），服务会**拒绝启动**。

### 3.4 Nginx 反向代理（HTTPS，生产推荐）

```nginx
server {
    listen 443 ssl;
    server_name xiaopacai.local;

    # [SEC-K6] 仅 TLS 1.2+，禁用弱协议弱套件
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_certificate /etc/ssl/certs/xiaopacai.pem;
    ssl_certificate_key /etc/ssl/private/xiaopacai.key;

    # 安全头（后端 SecurityHeaders 中间件已下发同组头，此为纵深防御）
    add_header X-Frame-Options DENY;
    add_header X-Content-Type-Options nosniff;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        # [SEC-K4/K6] 覆盖（而非追加）客户端伪造的转发头：
        # X-Forwarded-For 取真实直连对端；X-Forwarded-Proto 取本代理真实协议
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

配套要求（缺一不可）：

1. 在 `appsettings.json` 或环境变量中设置 **`ReverseProxy__Enabled=true`**，后端才会信任
   本机回环代理的转发头 —— 此时 `Request.IsHttps` 正确，认证 Cookie 才带 `Secure` 标记、
   HSTS 才下发、审计日志/登录限速才记录真实客户端 IP（默认关闭，防公网伪造转发头）。
2. **不要**配置 `https_port`：TLS 已在代理终结，后端重定向由代理层完成。
3. 直接暴露 5000 端口的路径必须由防火墙禁止（见 九、防火墙配置）。

## 四、配置说明

`server/appsettings.json`（仓库内置为安全占位值，生产禁止直接使用）：

```json
{
  "Urls": "http://127.0.0.1:5000",                 // 服务绑定（生产保持本机回环）
  "Jwt": {
    "SecretKey": "CHANGE-ME-IN-PRODUCTION-32CHARS-MIN", // JWT 签名密钥（占位值，生产必改）
    "Issuer": "xiaopacai-web",
    "Audience": "xiaopacai-client",
    "AccessTokenExpiryMinutes": 60,
    "RefreshTokenExpiryDays": 7
  },
  "Database": {
    "Path": "Data/xiaopacai.db",                  // 数据库文件路径（SQLCipher 加密）
    "Password": ""                                 // 数据库密钥（占位值，生产必配）
  },
  "P2P": {
    "ListenPort": 9527,                            // P2P 监听端口（mTLS 双向认证）
    "TlsMinVersion": "1.2",
    "CertPath": "Data/certs/server.pfx",           // 证书持久化路径
    "CertPassword": ""                             // 证书密码（占位值，生产必配）
  },
  "ReverseProxy": {
    "Enabled": false                               // 是否信任本机回环代理转发头（3.4 场景置 true）
  }
}
```

### 四.1 生产密钥管理（红线 K4：禁止明文配置）

| 配置键 | 环境变量 | 说明 |
|--------|---------|------|
| `Jwt:SecretKey` | `Jwt__SecretKey` | JWT 签名密钥，≥32 随机字符 |
| `Database:Password` | `Database__Password` | SQLCipher 数据库密钥 |
| `P2P:CertPassword` | `P2P__CertPassword` | P2P 证书密码 |

1. 密钥一律通过 **环境变量 / systemd EnvironmentFile** 注入（见 3.3），`appsettings.json`
   只保留占位值，禁止提交真实密钥（仓库历史中也不得出现）。
2. EnvironmentFile 权限 `0600`，数据目录 `0700`，密钥/数据库文件 `0600`。
3. 生产启动自检：`Jwt:SecretKey` 为默认值、占位值（含 CHANGE-ME/dev-secret）或不足 32 字符时，
   服务**直接拒绝启动**并报错，防止带弱密钥上线。

### 四.2 HTTPS 上线要求（红线 K6：生产必须 HTTPS）

认证已迁移为 httpOnly Cookie 会话（SEC-K5），Cookie 的 `Secure` 标记取决于
`Request.IsHttps`，因此生产 HTTPS 是硬性要求。二选一：

- **方案 A（推荐）**：3.4 Nginx TLS 终结 + `ReverseProxy__Enabled=true`；
- **方案 B**：Kestrel 直连 HTTPS（无代理时），在 `appsettings.json` 追加：

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:443",
      "SslProtocols": [ "Tls12", "Tls13" ],
      "Certificate": {
        "Path": "/etc/xiaopacai-web/certs/server.pem",
        "KeyPath": "/etc/xiaopacai-web/certs/server.key"
      }
    }
  }
},
"https_port": 443
```

配置后 `UseHttpsRedirection`/`UseHsts` 自动生效；HSTS 头仅对 HTTPS 请求下发（localhost 豁免）。

### 四.3 其他安全要点

1. **P2P 证书**：首次启动自动生成自签名证书，持久化在 `CertPath`，指纹稳定不变
   （家长端绑定证书指纹，证书更换后需重新配对）
2. **数据库加密**：SQLCipher 自动加密，库密钥随机生成并加密存储
3. **默认绑定 127.0.0.1**：服务仅本机可访问；对外入口统一走 Nginx 443
4. **登录/配对码限速**：已内置（IP 维度），经 3.4 代理部署后需开启
   `ReverseProxy__Enabled=true`，否则所有请求同源 127.0.0.1，限速按聚合 IP 计算

## 五、数据目录结构

```
/opt/xiaopacai-web/
├── XiaopacaiWeb          # 可执行文件
├── wwwroot/              # 前端静态文件
├── data/
│   ├── xiaopacai_web.db  # 加密数据库
│   ├── p2p_cert.pfx      # P2P 证书
│   └── db.key            # 数据库密钥文件
├── logs/                 # 日志目录
└── appsettings.json      # 配置文件
```

## 六、首次使用

1. 启动服务后访问 `http://<服务器IP>:5000`
2. 使用默认管理员账号登录：admin / admin123
3. 建议立即修改密码：设置 → 修改密码
4. 儿童端通过 P2P 连接家长端：在儿童端 APP 输入家长端 IP 和配对码

## 七、升级步骤

```bash
# 1. 停止服务
sudo systemctl stop xiaopacai-web

# 2. 备份数据
cp -r /opt/xiaopacai-web/data /opt/xiaopacai-web/data.bak.$(date +%Y%m%d)

# 3. 替换文件
cp -r build/server/* /opt/xiaopacai-web/

# 4. 启动服务
sudo systemctl start xiaopacai-web

# 5. 验证
curl http://127.0.0.1:5000/api/health
```

## 八、测试

```bash
# 后端测试
cd server
dotnet test

# 前端测试
cd web
npm test
```

## 九、防火墙配置

```bash
# [SEC-K6] HTTPS 入口（Nginx 443 或 Kestrel 直连 443）
sudo ufw allow 443/tcp

# P2P 监听端口（儿童端连接用，mTLS 双向认证）
sudo ufw allow 9527/tcp

# 后端 5000 端口禁止对外开放（服务已绑定 127.0.0.1，双保险）
# 如误执行过 sudo ufw allow 5000/tcp，请：sudo ufw delete allow 5000/tcp
sudo ufw status
```

端口暴露面：**仅 443（HTTPS）与 9527（P2P/mTLS）**。5000 为回环内部端口，
一切公网/局域网直连 5000 的流量都应被拒绝。

## 十、常见问题

**Q: 启动报错 "SQLCipher not loaded"**
A: Linux 需要安装 SQLCipher 原生库：`sudo apt-get install libsqlcipher0`

**Q: 儿童端连不上 P2P**
A: 检查防火墙是否开放 9527 端口，确认服务端 P2P 监听已启动（日志含 "P2P Listening on 0.0.0.0:9527"）

**Q: 前端页面打开空白**
A: 确认已执行 `npm run build`，`wwwroot` 目录存在 `index.html`

**Q: 如何重置管理员密码**
A: 删除 `data/xiaopacai_web.db` 后重启服务，数据库将重新创建并恢复种子数据（admin/admin123）。注意：此操作会清除所有数据。
