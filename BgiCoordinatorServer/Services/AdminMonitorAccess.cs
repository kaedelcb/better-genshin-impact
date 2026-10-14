using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BgiCoordinatorServer.Services;

/// <summary>独立于控制房密码的单管理员只读监控入口。</summary>
public sealed class AdminMonitorAccess
{
    public const string RevisionClaim = "admin-monitor-revision";
    private readonly byte[]? _passwordDigest;

    public AdminMonitorAccess(string? password)
    {
        if (!string.IsNullOrWhiteSpace(password) && password.Length >= 16)
            _passwordDigest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    public bool Enabled => _passwordDigest is not null;

    /// <summary>服务重启时使旧管理员会话失效。</summary>
    public string Revision { get; } = Guid.NewGuid().ToString("N");

    public bool Verify(string? password)
    {
        if (_passwordDigest is null || string.IsNullOrEmpty(password) || password.Length > 1024)
            return false;
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(_passwordDigest, suppliedDigest);
    }
}

public static class AdminMonitorRoutes
{
    private sealed record LoginRequest(string? Password);

    public static void MapAdminMonitor(this WebApplication app, int playerTimeoutSeconds)
    {
        var access = app.Services.GetRequiredService<AdminMonitorAccess>();
        var pageDirectory = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");

        app.MapGet("/admin", () => Results.Redirect("/admin/hoeing"));

        app.MapGet("/admin/login", (HttpContext context) =>
        {
            NoStore(context);
            if (!access.Enabled) return Results.Problem("管理员监控密码未配置或不足 16 位", statusCode: 503);
            return Results.File(Path.Combine(pageDirectory, "admin-login.html"), "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost("/api/admin/login", async (LoginRequest request, HttpContext context) =>
        {
            NoStore(context);
            if (!access.Enabled) return Results.StatusCode(503);
            if (!access.Verify(request.Password)) return Results.Unauthorized();

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "local-admin"),
                 new Claim(AdminMonitorAccess.RevisionClaim, access.Revision)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                });
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("admin-login");

        app.MapPost("/api/admin/logout", async (HttpContext context) =>
        {
            NoStore(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/admin/hoeing", (HttpContext context) =>
        {
            NoStore(context);
            return Results.File(Path.Combine(pageDirectory, "admin-monitor.html"), "text/html; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/api/admin/rooms", (HttpContext context, RoomManager manager) =>
        {
            NoStore(context);
            return Results.Json(RoomMonitorSnapshot.Create(manager, DateTime.UtcNow,
                TimeSpan.FromSeconds(playerTimeoutSeconds)));
        }).RequireAuthorization();
    }

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'self'; base-uri 'none'";
    }
}
