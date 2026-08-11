# 小趴菜 Web 3.0 部署指南

版本：3.0.0-p5 | 适用平台：Linux (Ubuntu 22.04+) / Windows 10+

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

### 3.2 直接运行

```bash
cd build/server
./XiaopacaiWeb --urls "http://0.0.0.0:5000"
```

访问 `http://<服务器IP>:5000` 即可使用。

### 3.3 systemd 服务（Linux 推荐）

```bash
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

# 安全加固
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable xiaopacai-web
sudo systemctl start xiaopacai-web
```

### 3.4 Nginx 反向代理（HTTPS）

```nginx
server {
    listen 443 ssl;
    server_name xiaopacai.local;

    ssl_certificate /etc/ssl/certs/xiaopacai.pem;
    ssl_certificate_key /etc/ssl/private/xiaopacai.key;

    # 安全头
    add_header X-Frame-Options DENY;
    add_header X-Content-Type-Options nosniff;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

## 四、配置说明

`server/appsettings.json`：

```json
{
  "Database": {
    "Path": "data/xiaopacai_web.db"     // 数据库文件路径
  },
  "Jwt": {
    "Secret": "your-256-bit-secret-here", // JWT 签名密钥（生产环境必须修改）
    "Issuer": "xiaopacai-web",
    "AccessTokenExpiryMinutes": 60,
    "RefreshTokenExpiryDays": 7
  },
  "P2P": {
    "ListenPort": 9527,                   // P2P 监听端口
    "CertPath": "data/p2p_cert.pfx",      // 证书持久化路径
    "TlsMinVersion": "1.2"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### 安全要点

1. **JWT Secret**：生产环境必须修改为随机 256 位密钥，不得使用默认值
2. **P2P 证书**：首次启动自动生成自签名证书，持久化在 `CertPath`，指纹稳定不变
3. **数据库加密**：SQLCipher 自动加密，库密钥随机生成并加密存储
4. **默认绑定 127.0.0.1**：仅本机可访问，如需 LAN 访问请改为 `0.0.0.0` 并配置防火墙

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
# Web 服务端口
sudo ufw allow 5000/tcp

# P2P 监听端口（儿童端连接用）
sudo ufw allow 9527/tcp
```

## 十、常见问题

**Q: 启动报错 "SQLCipher not loaded"**
A: Linux 需要安装 SQLCipher 原生库：`sudo apt-get install libsqlcipher0`

**Q: 儿童端连不上 P2P**
A: 检查防火墙是否开放 9527 端口，确认服务端 P2P 监听已启动（日志含 "P2P Listening on 0.0.0.0:9527"）

**Q: 前端页面打开空白**
A: 确认已执行 `npm run build`，`wwwroot` 目录存在 `index.html`

**Q: 如何重置管理员密码**
A: 删除 `data/xiaopacai_web.db` 后重启服务，数据库将重新创建并恢复种子数据（admin/admin123）。注意：此操作会清除所有数据。
