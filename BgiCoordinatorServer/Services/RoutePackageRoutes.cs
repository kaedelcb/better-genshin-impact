using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using NexusBGI.RoutePackages;

namespace BgiCoordinatorServer.Services;

public static class RoutePackageRoutes
{
    public sealed record PublicationRequest(long ExpectedRevision, string Reason);
    public sealed record RestoreRequest(string SourceId, string[] Paths, long ExpectedRevision, string Reason);

    /// <summary>Runs before static files and model binding. Route package APIs have their own bounded IO boundary.</summary>
    public static void UseRoutePackageBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var normalizedPath = (path.Value ?? "").TrimEnd('/');
            var routeApi = path.StartsWithSegments("/api/hoeing-routes") || path.StartsWithSegments("/api/admin/routes");
            var routePage = normalizedPath.Equals("/admin/routes", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Equals("/routes/upload", StringComparison.OrdinalIgnoreCase);
            if (!routeApi && !routePage) { await next(context); return; }
            NoStore(context);
            if (!IsSecure(context))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "线路包服务只允许 HTTPS（本机 loopback 开发除外）。" });
                return;
            }
            var upload = normalizedPath.Equals("/api/admin/routes/upload", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method);
            var restore = HttpMethods.IsPost(context.Request.Method)
                && path.StartsWithSegments("/api/admin/routes") && normalizedPath.EndsWith("/restore", StringComparison.OrdinalIgnoreCase);
            var limit = upload ? RoutePackageArchive.MaxArchiveBytes : restore ? 2L * 1024 * 1024 : 32768L;
            if (context.Request.ContentLength > limit) { context.Response.StatusCode = 413; return; }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = limit;
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (RoutePackageException ex) { await Error(context, ex.StatusCode, ex.Message); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException)
            { await Error(context, 400, ex.Message); }
            catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "请求大小或格式无效。"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RoutePackages").LogError(ex, "线路包持久存储操作失败");
                await Error(context, 503, "线路包存储不可用，未将未知状态当作成功。请刷新并检查服务器日志。");
            }
        });
    }

    public static void MapRoutePackages(this WebApplication app)
    {
        var root = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        app.MapGet("/admin/routes", () => Results.File(Path.Combine(root, "admin-routes.html"), "text/html; charset=utf-8"))
            .RequireAuthorization();
        app.MapGet("/routes/upload", (HttpContext context) =>
        {
            // This endpoint does not create or upgrade any cookie; the upload token remains upload-only.
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; worker-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            return Results.File(Path.Combine(root, "route-upload.html"), "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/api/hoeing-routes/current", (RoutePackageStore store) => store.Current is { } publication
            ? Results.Json(publication) : Results.Problem("尚未发布首个线路包。", statusCode: 503));
        app.MapGet("/api/hoeing-routes/policy", (RoutePackageStore store) => Results.Json(new { schemaVersion = 1, requiredClientVersion = store.RequiredClientVersion }));
        app.MapGet("/api/hoeing-routes/packages/{id}", (string id, RoutePackageStore store) => Results.Json(store.Get(id)));
        app.MapMethods("/api/hoeing-routes/packages/{id}/download", ["GET", "HEAD"], (string id, HttpContext context, RoutePackageStore store) => Download(id, context, store, false));

        var admin = app.MapGroup("/api/admin/routes").RequireAuthorization();
        admin.MapGet("", (RoutePackageStore store) => Results.Json(store.List()));
        admin.MapGet("/csrf", (HttpContext context, RoutePackageAccess access) => Results.Json(new { token = access.IssueCsrf(context.User) }));
        admin.MapGet("/audit", (RoutePackageStore store) => Results.Json(store.Audit()));
        admin.MapGet("/{id}", (string id, RoutePackageStore store) => Results.Json(store.Compare(id)));
        admin.MapMethods("/{id}/download", ["GET", "HEAD"], (string id, HttpContext context, RoutePackageStore store) => Download(id, context, store, true));
        admin.MapPost("/{id}/publish", async (string id, PublicationRequest request, HttpContext context, RoutePackageAccess access, RoutePackageStore store) =>
        {
            RequireCsrf(context, access);
            return Results.Json(await store.PublishAsync(id, request.ExpectedRevision, request.Reason, Actor(context), ct: context.RequestAborted));
        });
        admin.MapPost("/{id}/rollback", async (string id, PublicationRequest request, HttpContext context, RoutePackageAccess access, RoutePackageStore store) =>
        {
            RequireCsrf(context, access);
            return Results.Json(await store.PublishAsync(id, request.ExpectedRevision, request.Reason, Actor(context), rollback: true, ct: context.RequestAborted));
        });
        admin.MapPost("/{id}/restore", async (string id, RestoreRequest request, HttpContext context, RoutePackageAccess access, RoutePackageStore store) =>
        {
            RequireCsrf(context, access);
            if (request.Paths is null) throw new RoutePackageException(400, "请选择恢复路径。");
            return Results.Json(await store.RestoreAsync(id, request.SourceId, request.Paths, request.ExpectedRevision, request.Reason, Actor(context), context.RequestAborted));
        });
        admin.MapDelete("/{id}", async (string id, HttpContext context, RoutePackageAccess access, RoutePackageStore store) =>
        {
            RequireCsrf(context, access);
            await store.DeleteAsync(id, Actor(context), context.RequestAborted);
            return Results.NoContent();
        });

        // Not in the authenticated group: valid upload-only tokens never become admin principals.
        app.MapPost("/api/admin/routes/upload", async (string? description, HttpContext context, RoutePackageAccess access, RoutePackageStore store) =>
        {
            string actor;
            if (context.Request.Headers.TryGetValue("X-Route-Upload-Token", out var token))
            {
                if (token.Count != 1 || !access.VerifyUploadToken(token[0])) return Results.Unauthorized();
                actor = "upload-token";
            }
            else
            {
                if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
                RequireCsrf(context, access);
                actor = Actor(context);
            }
            if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("请直接发送 application/zip 单 ZIP 请求体。", statusCode: 415);
            return Results.Json(await store.UploadAsync(context.Request.Body, description ?? "", actor, context.RequestAborted));
        }).AllowAnonymous().RequireRateLimiting("route-upload");
    }

    private static IResult Download(string id, HttpContext context, RoutePackageStore store, bool admin)
    {
        var descriptor = store.Get(id, admin);
        var stream = store.OpenDownload(id, admin);
        context.Response.Headers.ETag = "\"" + descriptor.Sha256 + "\"";
        context.Response.Headers.CacheControl = admin ? "no-store" : "public,max-age=31536000,immutable";
        return Results.File(stream, "application/zip", id + ".zip", lastModified: descriptor.CreatedAtUtc,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + descriptor.Sha256 + "\""), enableRangeProcessing: true);
    }

    private static void RequireCsrf(HttpContext context, RoutePackageAccess access)
    {
        if (context.Request.Headers.ContainsKey("X-Route-Upload-Token")
            || !context.Request.Headers.TryGetValue("X-CSRF-Token", out var token) || token.Count != 1 || !access.VerifyCsrf(context.User, token[0]))
            throw new RoutePackageException(403, "CSRF 校验失败，请刷新管理员会话。");
    }
    private static string Actor(HttpContext context) => "admin:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown");
    private static bool IsSecure(HttpContext context) => context.Request.IsHttps
        || context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
            && (context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(context.Request.Host.Host, out var host) && IPAddress.IsLoopback(host));
    private static Task Error(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) { context.Abort(); return Task.CompletedTask; }
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; worker-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    }
}
