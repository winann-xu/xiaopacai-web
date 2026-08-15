// 小趴菜 Web 3.0 — ASP.NET Core 8 入口
// 自托管单进程：REST API + SignalR + P2P TCP/TLS 监听
// P2 阶段：数据层 + 认证鉴权 完成

using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Middleware;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Services;
// [SEC-K4] 消除 System.Net.IPNetwork 与 HttpOverrides.IPNetwork 的命名歧义
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

var builder = WebApplication.CreateBuilder(args);

// ========== 服务注册 ==========

// ---- 控制器 + JSON 序列化 ----
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.WriteIndented = false;
    });

// ---- Swagger（开发环境） ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "小趴菜 Web 3.0 API",
        Version = "v1",
        Description = "儿童守护 Web 端 REST API"
    });

    // JWT Bearer 认证（Swagger UI 中输入 Token）
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

// ---- CORS（开发阶段宽松，生产收紧） ----
builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ---- SignalR 实时通信 ----
builder.Services.AddSignalR();

// ---- 响应压缩（gzip + brotli）：文本类静态资源与 JSON 传输体积平均减少 60%~75% ----
builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<BrotliCompressionProvider>();
    opts.Providers.Add<GzipCompressionProvider>();
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json",
        "image/svg+xml",
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

// ---- 密码哈希服务（无状态，Singleton） ----
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();

// ---- 扫码登录 Ticket 内存存储（OPT12 需求 10，Singleton；[TASK-ACCOUNT-V1] reset-ticket 已退役） ----
builder.Services.AddSingleton<TicketStore>();
// [SEC-P1] 过期 Ticket 定时清理（防内存无限增长）
builder.Services.AddHostedService<TicketCleanupService>();

// ---- [TASK-ACCOUNT-V1] 邮箱验证码 / 一次性 Action Token 内存存储（Singleton） ----
builder.Services.AddSingleton<VerificationCodeStore>();
builder.Services.AddSingleton<ActionTokenStore>();

// ---- [TASK-ACCOUNT-V1-MAILCONFIG] 邮件发送器（DB 配置 → 环境变量 MAIL_* 兜底，热加载） ----
builder.Services.AddSingleton<IMailSender, MailSender>();

// ---- SQLCipher 服务（Singleton：密钥管理 + 连接字符串提供） ----
var sqlCipherService = SqlCipherService.CreateFromConfig(builder.Configuration);
builder.Services.AddSingleton<ISqlCipherService>(sqlCipherService);

// ---- 数据库（EF Core + SQLCipher 加密拦截器） ----
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    opts.UseSqlite(
        sqlCipherService.GetPlainConnectionString(),
        sqlOpts =>
        {
            // SQLCipher PRAGMA key 拦截器 — 每次连接打开时自动设置加密密钥
            sqlOpts.CommandTimeout(30);
        });
    opts.AddInterceptors(new SqlCipherInterceptor(sqlCipherService.GetDbPassword()));
});

// ---- JWT 服务 ----
builder.Services.AddScoped<IJwtService, JwtService>();

// ---- JWT 鉴权 ----
// [SEC-P2] 弱密钥熵检查：非生产环境下密钥缺失/过弱时，启动即生成随机临时密钥并覆盖配置。
// JwtService（签发）与 JwtBearer（验签）均从 IConfiguration 读取，保证两端一致；
// 临时密钥仅进程内存有效，重启轮换（现有令牌失效需重新登录）。生产环境保持硬性拒绝启动（见下方校验）。
var jwtSecretKey = builder.Configuration["Jwt:SecretKey"];
var jwtKeyIsWeak = string.IsNullOrEmpty(jwtSecretKey) || jwtSecretKey.Length < 32 ||
                   jwtSecretKey.Contains("CHANGE-ME") || jwtSecretKey.Contains("dev-secret");
if (jwtKeyIsWeak && !builder.Environment.IsProduction())
{
    // 48 字节随机数 → 64 字符 Base64（SHA-256 HMAC 足够熵）
    jwtSecretKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:SecretKey"] = jwtSecretKey,
    });
    Console.WriteLine("[安全] Jwt:SecretKey 缺失或过弱，已生成随机临时密钥（进程内有效，重启后需重新登录）");
}
jwtSecretKey ??= "dev-secret-key-32chars-minimum-ok";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "xiaopacai-web";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "xiaopacai-client";

builder.Services.AddAuthentication(opts =>
{
    opts.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    opts.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(opts =>
{
    opts.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecretKey)),
        ClockSkew = TimeSpan.FromMinutes(1), // 1 分钟时钟偏差容忍
    };

    // [SEC-K5] 浏览器会话无 Authorization 头时，从 httpOnly Cookie 读取 access_token；
    // 原生客户端（Android/Windows）仍走 Bearer 头，两套链路并存。
    opts.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization))
            {
                var token = ctx.Request.Cookies["access_token"];
                if (!string.IsNullOrEmpty(token))
                    ctx.Token = token;
            }
            return Task.CompletedTask;
        }
    };
});

// ---- 角色策略 ----
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
    opts.AddPolicy("ParentOrAdmin", policy => policy.RequireRole("admin", "parent"));
});

// ========== P2P 服务（P4 阶段） ==========
builder.Services.AddSingleton<P2pCertificateService>();
builder.Services.AddSingleton<P2pMessageHandler>();
builder.Services.AddSingleton<P2pListenerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<P2pListenerService>());
// [TASK-MILESTONE-V3] B2/B10 公告送达补偿（60 秒未 displayed 重推，见 docs/adr/）
builder.Services.AddSingleton<Services.AnnouncementCompensationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Services.AnnouncementCompensationService>());

var app = builder.Build();

// ========== 生产安全校验：禁止弱 JWT 密钥 ==========
if (app.Environment.IsProduction() &&
    (jwtSecretKey.Length < 32 || jwtSecretKey.Contains("CHANGE-ME") || jwtSecretKey.Contains("dev-secret")))
{
    throw new InvalidOperationException(
        "生产环境必须配置强 Jwt:SecretKey（≥32 随机字符，禁止默认/占位值）");
}

// ========== 数据库初始化（密钥 + 迁移 + 种子数据） ==========
await app.Services.InitializeDatabaseAsync();

// ========== 中间件管道 ==========

// [SEC-K4/K6] 反向代理转发头：默认关闭。启用后（ReverseProxy:Enabled=true）信任来自
// 本机回环（127.0.0.0/8，即同机 Nginx 等 TLS 终结代理）的 X-Forwarded-For / X-Forwarded-Proto，
// 使 Request.IsHttps 正确（认证 Cookie 的 Secure 标记、HSTS 下发均依赖此值）、
// 审计日志与登录限速拿到真实客户端 IP。回环之外的对端一律不信任，防伪造转发头。
if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled", false))
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        KnownNetworks = { new IPNetwork(IPAddress.Loopback, 8) },
    });
}

// [SEC-K6] HTTPS 强化：HSTS（仅 HTTPS 请求下发，localhost 自动豁免）；
// HttpsRedirection 在未配置 https_port 时为无操作，部署配 https_port 后自动生效
app.UseHsts();
app.UseHttpsRedirection();

// [SEC-K6] 安全响应头（X-Content-Type-Options / X-Frame-Options / CSP 等）
app.UseMiddleware<SecurityHeadersMiddleware>();

// [SEC-K8] 下载中心白名单 + 敏感文件拒绝 + 路径穿越防护（先于静态文件中间件）
app.UseMiddleware<DownloadCenterGuardMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();

// ========== 前端静态文件（单进程自托管，wwwroot = Vue 构建产物）==========
// [TASK-OPT-12-P4] 修复：此前未配置静态文件服务，发布版根路径 404；
// DEPLOY.md 单进程部署（publish + 复制 web/dist 到 wwwroot）现在可直接访问
// [PERF] 缓存策略：SPA 入口与 API 一律 no-cache（每次回源校验）；
// 带哈希的 /assets/* 构建产物由静态文件中间件设置一年 immutable 长缓存。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers.CacheControl = "no-cache";
    }
    await next();
});

app.UseDefaultFiles();

// [下载中心] 显式注册 .apk/.ipa 等安装包扩展名，否则静态文件中间件对未知/特殊扩展名返回 404
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".apk"] = "application/vnd.android.package-archive";
contentTypeProvider.Mappings[".ipa"] = "application/octet-stream";
contentTypeProvider.Mappings[".dmg"] = "application/octet-stream";
contentTypeProvider.Mappings[".bat"] = "application/octet-stream";  // [REQ] 电脑一键授权脚本
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
    // [PERF] 带哈希的构建产物一年 immutable；非 /assets 入口已由上方中间件统一 no-cache
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    },
});

app.MapControllers();

// SPA 路由回退：前端路由（如 /login、/admin/devices）交给 index.html 处理
// [SEC-P2] 排除 /api：未知 API 路径不再被 SPA 兜底吞掉（返回 200 HTML），
// 改为 JSON 404，避免调用方把"接口不存在"误判为"接口存在但异常"
app.MapWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api"), b =>
{
    b.UseRouting();
    b.UseEndpoints(endpoints => endpoints.MapFallbackToFile("index.html"));
});
app.Map("/api/{**path}", () => Results.Json(new { error = "接口不存在" }, statusCode: 404));

// SignalR Hub 路由（P3 阶段激活）
// app.MapHub<DeviceHub>("/hubs/device");

// ========== 启动 ==========
var urls = app.Configuration["Urls"] ?? "http://127.0.0.1:5000";
app.Urls.Add(urls);

Console.WriteLine($"[小趴菜 Web 3.0] 启动成功 → {urls}");
Console.WriteLine($"[小趴菜 Web 3.0] Swagger → {urls}/swagger");
Console.WriteLine($"[小趴菜 Web 3.0] 健康检查 → {urls}/api/health");
Console.WriteLine($"[小趴菜 Web 3.0] P2P TCP/TLS → 0.0.0.0:{builder.Configuration.GetValue<int>("P2P:ListenPort", 9527)} (TLS 1.2/1.3)");

app.Run();
