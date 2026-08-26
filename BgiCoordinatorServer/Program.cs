using BgiCoordinatorServer.RoomControl.Hubs;
using BgiCoordinatorServer.RoomControl.Persistence;
using BgiCoordinatorServer.RoomControl.Services;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 读取环境变量配置
var maxRooms = int.TryParse(Environment.GetEnvironmentVariable("MAX_ROOMS"), out var mr) ? mr : 50;
var playerTimeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("PLAYER_TIMEOUT_SECONDS"), out var pts) ? pts : 120;

// 控制房间数据库路径
var dbPath = Environment.GetEnvironmentVariable("CONTROL_ROOM_DB_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "controlroom.db");

// 注册服务
builder.Services.AddSingleton(_ => new RoomManager(maxRooms));
builder.Services.AddHostedService<HeartbeatMonitor>();

// 控制房间新架构（事件存储 + 快照 + SSOT）
builder.Services.AddDbContext<ControlRoomDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<IEventStore, SqliteEventStore>();
builder.Services.AddScoped<IControlRoomRepository, ControlRoomRepository>();
builder.Services.AddScoped<IControlRoomManager, ControlRoomManager>();
builder.Services.AddScoped<IOnlineSessionManager, OnlineSessionManager>();
builder.Services.AddScoped<IScheduleNotifier, SignalRScheduleNotifier>();
builder.Services.AddHostedService<ScheduleEngine>();

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

// 确保控制房间数据库已创建（开发阶段；生产环境应使用 dotnet-ef migrations）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ControlRoomDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();

// 启用静态文件服务，用于控制房间网页端
app.UseDefaultFiles();
app.UseStaticFiles();

// 映射 SignalR Hub
app.MapHub<CoordinatorHub>("/hub");
app.MapHub<ControlRoomHub>("/control-hub");

// 健康检查移到 /health，避免与 UseDefaultFiles()（根路径服务 index.html）冲突
app.MapGet("/health", () => Results.Ok(new { status = "BgiCoordinatorServer running", maxRooms, playerTimeoutSeconds }));

app.Run();
