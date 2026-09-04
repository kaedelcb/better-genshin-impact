using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;

var builder = WebApplication.CreateBuilder(args);

// 读取环境变量配置
var maxRooms = int.TryParse(Environment.GetEnvironmentVariable("MAX_ROOMS"), out var mr) ? mr : 50;
var playerTimeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("PLAYER_TIMEOUT_SECONDS"), out var pts) ? pts : 120;

// 注册服务
builder.Services.AddSingleton(_ => new RoomManager(maxRooms));
builder.Services.AddHostedService<HeartbeatMonitor>();
// 网关（模块二 ServerGateway，与旧 CoordinatorHub 双轨并存，§4.7 兼容层）
builder.Services.AddSingleton<GatewaySessionTracker>();
builder.Services.AddSingleton<GatewayBroadcaster>();
builder.Services.AddSingleton<RoomPhaseObserver>();
builder.Services.AddSingleton<RoomOperations>();
builder.Services.AddSingleton<GatewayDispatcher>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10MB，支持大量路线文件上报
});

// 配置 CORS（开发阶段允许所有来源）
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

// 启用静态文件服务，用于控制房间网页端
app.UseDefaultFiles();
app.UseStaticFiles();

// 映射 SignalR Hub：旧 /hub 不动（旧客户端零感知），新网关 /gateway（§4.8 URL 约定：配置只填基地址，SDK 内部拼路径）
app.MapHub<CoordinatorHub>("/hub");
app.MapHub<GatewayHub>("/gateway");

// 健康检查移到 /health，避免与 UseDefaultFiles()（根路径服务 index.html）冲突
app.MapGet("/health", () => Results.Ok(new { status = "BgiCoordinatorServer running", maxRooms, playerTimeoutSeconds }));

app.Run();
