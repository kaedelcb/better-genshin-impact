using System.Net;
using System.Threading.RateLimiting;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// 读取环境变量配置
var maxRooms = int.TryParse(Environment.GetEnvironmentVariable("MAX_ROOMS"), out var mr) ? mr : 50;
var playerTimeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("PLAYER_TIMEOUT_SECONDS"), out var pts) ? pts : 120;

// 注册服务
builder.Services.AddSingleton(_ => new RoomManager(maxRooms));
builder.Services.AddSingleton(new AdminMonitorAccess(
    builder.Configuration["ADMIN_MONITOR_PASSWORD"], builder.Environment.IsDevelopment()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "BgiAdminMonitor";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect("/admin/login");
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = context =>
    {
        var access = context.HttpContext.RequestServices.GetRequiredService<AdminMonitorAccess>();
        if (!access.Enabled ||
            !access.SecureRequest(context.HttpContext) ||
            context.Principal?.FindFirst(AdminMonitorAccess.RevisionClaim)?.Value != access.Revision)
            context.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.RejectionStatusCode = 429;
});
// 仅信任明确配置的反向代理，供 HTTPS 终止代理转发 X-Forwarded-Proto。
var trustedProxy = builder.Configuration["ADMIN_MONITOR_TRUSTED_PROXY_IP"];
if (!string.IsNullOrWhiteSpace(trustedProxy))
{
    if (!IPAddress.TryParse(trustedProxy, out var proxyIp))
        throw new InvalidOperationException("ADMIN_MONITOR_TRUSTED_PROXY_IP 必须是单个 IP 地址");
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Add(proxyIp);
        options.ForwardLimit = 1;
    });
}
builder.Services.AddHostedService<HeartbeatMonitor>();
// 网关（模块二 ServerGateway，与旧 CoordinatorHub 双轨并存，§4.7 兼容层）
builder.Services.AddSingleton<GatewaySessionTracker>();
builder.Services.AddSingleton<GatewayBroadcaster>();
builder.Services.AddSingleton<RoomPhaseObserver>();
builder.Services.AddSingleton<BgiCoordinatorServer.Services.IClientCapabilityLookup,
    BgiCoordinatorServer.Services.GatewayCapabilityLookup>();
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

if (!string.IsNullOrWhiteSpace(trustedProxy)) app.UseForwardedHeaders();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// 启用静态文件服务，用于控制房间网页端
// 管理页面源文件位于 wwwroot，但只允许由下面带鉴权的 /admin 路由返回。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (string.Equals(path, "/admin-login.html", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/admin-monitor.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== BGI 更新包目录（可选，联机助手"网络"来源更新）=====
// 挂载 BGI_UPDATE_ROOT（默认 wwwroot/bgi-update，可用 docker 卷挂到容器内任意路径）后：
//   GET /bgi-update/{文件名}.7z   → 安装包本体（静态托管）
//   GET /bgi-update/latest.json   → 由目录扫描自动生成的最新包清单（取版本最新的合法茶包名 .7z），
//                                    助手轮询此清单决定"版本号变金 + 弹窗提醒 + 网络来源下载更新"。
// 只要把茶包版 .7z（BetterGI_v*+lcb.* 命名）丢进该目录即可，无需手写清单。
var bgiUpdateRoot = builder.Configuration["BGI_UPDATE_ROOT"];
if (string.IsNullOrWhiteSpace(bgiUpdateRoot))
{
    bgiUpdateRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "bgi-update");
}
if (Directory.Exists(bgiUpdateRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(bgiUpdateRoot)),
        RequestPath = "/bgi-update",
    });
    app.MapGet("/bgi-update/latest.json", () =>
    {
        try
        {
            var newest = BgiUpdateCatalogDecisions.PickNewest(Directory.EnumerateFiles(bgiUpdateRoot, "*.7z", SearchOption.TopDirectoryOnly));
            if (newest == null) return Results.Json(new { error = "no valid package" }, statusCode: 404);
            var fullPath = Path.Combine(bgiUpdateRoot, newest.FileName);
            var sizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;
            DateTime? updatedAt = File.Exists(fullPath) ? new FileInfo(fullPath).LastWriteTimeUtc : null;
            return Results.Json(new
            {
                fileName = newest.FileName,
                sizeBytes,
                updatedAt,
                url = "/bgi-update/" + Uri.EscapeDataString(newest.FileName),
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });
}

// 映射 SignalR Hub：旧 /hub 不动（旧客户端零感知），新网关 /gateway（§4.8 URL 约定：配置只填基地址，SDK 内部拼路径）
app.MapHub<CoordinatorHub>("/hub");
app.MapHub<GatewayHub>("/gateway");
app.MapAdminMonitor(playerTimeoutSeconds);

// 健康检查移到 /health，避免与 UseDefaultFiles()（根路径服务 index.html）冲突
app.MapGet("/health", () => Results.Ok(new { status = "BgiCoordinatorServer running", maxRooms, playerTimeoutSeconds }));

app.Run();
