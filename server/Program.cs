// 小趴菜 Web 3.0 — ASP.NET Core 8 入口
// 自托管单进程：REST API + SignalR + P2P TCP/TLS 监听
// P2 阶段：数据层 + 认证鉴权 完成

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using XiaopacaiWeb.Data;
using XiaopacaiWeb.Middleware;
using XiaopacaiWeb.P2P;
using XiaopacaiWeb.Services;

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

// ---- 密码哈希服务（无状态，Singleton） ----
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();

// ---- 扫码登录/重置 Ticket 内存存储（OPT12 需求 10/12，Singleton） ----
builder.Services.AddSingleton<TicketStore>();

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
var jwtSecretKey = builder.Configuration["Jwt:SecretKey"] ?? "dev-secret-key-32chars-minimum-ok";
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

var app = builder.Build();

// ========== 数据库初始化（密钥 + 迁移 + 种子数据） ==========
await app.Services.InitializeDatabaseAsync();

// ========== 中间件管道 ==========

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

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
