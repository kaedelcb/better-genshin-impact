using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
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

        app.MapGet("/api/admin/rooms", (HttpContext context, RoomManager manager, RoomHoeingHistory history) =>
        {
            NoStore(context);
            return Results.Json(RoomMonitorSnapshot.Create(manager, DateTime.UtcNow,
                TimeSpan.FromSeconds(playerTimeoutSeconds), history));
        }).RequireAuthorization();

        app.MapGet("/api/admin/rooms/{code}/logs", (string code, HttpContext context, RoomLogStore logs) =>
        {
            NoStore(context);
            var normalizedCode = RoomLogStore.NormalizeCode(code);
            if (normalizedCode is null) return Results.BadRequest();
            return Results.Json(new { code = normalizedCode, entries = logs.Read(normalizedCode) });
        }).RequireAuthorization();

        app.MapGet("/api/admin/logs/rooms", (HttpContext context, RoomLogArchive archive) =>
        {
            NoStore(context);
            try
            {
                var rooms = archive.GetAvailableRooms(RoomLogArchive.GetDate(DateTime.UtcNow));
                return Results.Json(new { rooms = rooms.Select(room => new
                {
                    room.Code,
                    room.Kind,
                    Dates = room.Dates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    room.Warnings
                }) });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("日志存储不可用，无法读取历史房间。请检查服务器日志目录。", statusCode: 503);
            }
        }).RequireAuthorization();

        app.MapGet("/api/admin/rooms/{code}/logs/dates", (string code, HttpContext context, RoomLogArchive archive) =>
        {
            NoStore(context);
            var normalizedCode = RoomLogStore.NormalizeCode(code);
            if (normalizedCode is null) return Results.Problem("房间码无效。", statusCode: 400);
            try
            {
                var dates = archive.GetAvailableDates(normalizedCode, RoomLogArchive.GetDate(DateTime.UtcNow));
                return Results.Json(new { dates = dates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("日志存储不可用，无法读取可选日期。请检查服务器日志目录。", statusCode: 503);
            }
        }).RequireAuthorization();

        app.MapGet("/api/admin/rooms/{code}/logs/download", async (string code, string? start, string? end,
            HttpContext context, RoomLogArchive archive) =>
        {
            NoStore(context);
            var normalizedCode = RoomLogStore.NormalizeCode(code);
            if (normalizedCode is null) return Results.Problem("房间码无效。", statusCode: 400);
            if (!RoomLogRange.TryParse(start, end, out var range, out var error))
                return Results.Problem(error, statusCode: 400);
            try
            {
                var download = await archive.OpenRangeAsync(normalizedCode, range!, context.RequestAborted);
                if (download is null)
                    return Results.Problem("所选时间段暂无已保存的房间日志；旧版未保存的日志无法补回。", statusCode: 404);
                var metadata = download.Metadata;
                context.Response.Headers["X-Room-Log-Count"] = metadata.Count.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Room-Log-Sources"] = string.Join(",", metadata.Sources.Take(20));
                context.Response.Headers["X-Room-Log-Source-Count"] = metadata.Sources.Length.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Room-Log-First-Utc"] = metadata.FirstTimestampUtc?.ToString("O", CultureInfo.InvariantCulture);
                context.Response.Headers["X-Room-Log-Last-Utc"] = metadata.LastTimestampUtc?.ToString("O", CultureInfo.InvariantCulture);
                // 完整说明保留在TXT中，响应头只放有界摘要，避免多年历史触发代理响应头上限。
                var warningSummary = metadata.Warnings.Take(1)
                    .Select(warning => warning.Length <= 120 ? warning : warning[..120] + "…").ToList();
                if (metadata.Warnings.Length > 1)
                    warningSummary.Add($"另有 {metadata.Warnings.Length - 1} 项保存说明，详见 TXT 文件开头。");
                context.Response.Headers["X-Room-Log-Warnings"] = Uri.EscapeDataString(
                    System.Text.Json.JsonSerializer.Serialize(warningSummary));
                return Results.Stream(async output =>
                {
                    await using (download)
                        await download.CopyToAsync(output, context.RequestAborted);
                }, "text/plain; charset=utf-8", download.FileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("所选日期的日志存储不可用或曾保存失败，无法完整下载。请检查服务器日志目录。", statusCode: 503);
            }
        }).RequireAuthorization();

        app.MapGet("/api/admin/rooms/{code}/logs/today", (string code, HttpContext context, RoomLogArchive archive) =>
        {
            NoStore(context);
            var normalizedCode = RoomLogStore.NormalizeCode(code);
            if (normalizedCode is null) return Results.BadRequest();
            try
            {
                var download = archive.OpenDay(normalizedCode, RoomLogArchive.GetDate(DateTime.UtcNow));
                if (download is null)
                    return Results.Problem("今天暂无已保存的房间日志；旧版未保存的日志无法补回。", statusCode: 404);
                return Results.Stream(async output =>
                {
                    await using (download)
                        await download.CopyToAsync(output, context.RequestAborted);
                }, "text/plain; charset=utf-8", download.FileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("日志存储不可用或今天曾保存失败，无法完整下载。请检查服务器日志目录。", statusCode: 503);
            }
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
