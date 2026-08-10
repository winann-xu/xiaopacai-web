// 小趴菜 Web 3.0 — ASP.NET Core 8 入口
// 自托管单进程：REST API + SignalR + P2P TCP/TLS 监听

using Microsoft.OpenApi.Models;
using XiaopacaiWeb.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ========== 服务注册 ==========

// 控制器 + JSON 序列化
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.WriteIndented = false;
    });

// Swagger（仅开发环境）
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "小趴菜 Web 3.0 API",
        Version = "v1",
        Description = "儿童守护 Web 端 REST API"
    });
});

// CORS（开发阶段宽松，生产收紧）
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

// SignalR 实时通信
builder.Services.AddSignalR();

// JWT 鉴权（P2 阶段配置完整）
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // P2 阶段从配置读取实际值
            ValidIssuer = "xiaopacai-web",
            ValidAudience = "xiaopacai-client",
        };
    });
builder.Services.AddAuthorization();

// 数据库上下文（P2 阶段配置 SQLCipher 连接）
// builder.Services.AddDbContext<AppDbContext>(...);

// P2P 服务（P4 阶段接入）
// builder.Services.AddSingleton<P2PListenerService>();

var app = builder.Build();

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

// ========== 健康检查端点（P1 验证） ==========
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    version = "3.0.0-p1",
    timestamp = DateTime.UtcNow.ToString("O"),
    service = "xiaopacai-web"
}));

// ========== 启动 ==========
var urls = app.Configuration["Urls"] ?? "http://127.0.0.1:5000";
app.Urls.Add(urls);

Console.WriteLine($"[小趴菜 Web 3.0] 启动成功 → {urls}");
Console.WriteLine($"[小趴菜 Web 3.0] 健康检查 → {urls}/api/health");

app.Run();
