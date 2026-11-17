using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoutePackageHttpTests
{
    private const string UploadToken = "isolated-test-upload-token-0123456789abcdef";
    private static ClaimsPrincipal Admin(string? session = null) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "fixture-admin"), new Claim(RoutePackageAccess.SessionClaim, session ?? new string('a', 32))], "fixture"));

    [Fact]
    public void UploadCredentialAndCsrfHaveIndependentSessionExpiryAndRestartBoundaries()
    {
        var access = new RoutePackageAccess(UploadToken);
        var admin = Admin();
        var now = DateTimeOffset.UtcNow;
        var csrf = access.IssueCsrf(admin, now);
        Assert.True(access.VerifyUploadToken(UploadToken));
        Assert.False(access.VerifyUploadToken(csrf));
        Assert.False(access.VerifyUploadToken("bad"));
        Assert.False(new RoutePackageAccess(null).VerifyUploadToken(UploadToken));
        Assert.True(access.VerifyCsrf(admin, csrf, now));
        Assert.False(access.VerifyCsrf(Admin(new string('b', 32)), csrf, now));
        Assert.False(access.VerifyCsrf(admin, csrf, now.AddMinutes(31)));
        Assert.False(access.VerifyCsrf(new ClaimsPrincipal(), csrf, now));
        Assert.False(new RoutePackageAccess(UploadToken).VerifyCsrf(admin, csrf, now));
        Assert.Throws<RoutePackageException>(() => access.IssueCsrf(new ClaimsPrincipal()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ProductionRegistrationNeverDropsTheFailClosedPolicyOrTrustsAllProxies(string? proxies)
    {
        using var data = new RoutePackageTestData();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ROUTE_TRUSTED_PROXY_IPS"] = proxies }).Build();
        var services = new ServiceCollection();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(data.Root);
        environment.SetupGet(e => e.WebRootPath).Returns(Path.Combine(data.Root, "wwwroot"));
        Assert.False(services.AddRoutePackageServices(config, environment.Object));
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<RoutePackagePolicy>();
        Assert.Equal("hoeing_route_package_uninitialized", Assert.Throws<InvalidOperationException>(policy.RequireConfigured).Message);
        Assert.Empty(provider.GetServices<IConfigureOptions<ForwardedHeadersOptions>>());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("10.0.0.0/8")]
    [InlineData("proxy.internal")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0177.0.0.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("127.0.0.1,")]
    [InlineData("[::1]")]
    public void ProxyConfigurationRejectsAnythingExceptExactIpLiterals(string value)
        => Assert.Throws<InvalidOperationException>(() => RoutePackageHosting.ParseTrustedProxyIps(value));

    [Fact]
    public void ExplicitProxyConfigurationIsSingleHopSymmetricAndHasNoTrustedNetworkRanges()
    {
        using var data = new RoutePackageTestData();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ROUTE_TRUSTED_PROXY_IPS"] = "10.0.0.2, ::1" }).Build();
        var services = new ServiceCollection();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(data.Root);
        Assert.True(services.AddRoutePackageServices(config, environment.Object));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Empty(options.KnownNetworks);
        Assert.Equal(new[] { IPAddress.Parse("10.0.0.2"), IPAddress.IPv6Loopback }, options.KnownProxies);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Fact]
    public async Task RealMappedApiKeepsTokenUploadOnlyAndRequiresCookieBoundCsrfForAdminWrites()
    {
        using var data = new RoutePackageTestData();
        await using var http = new InMemoryPipeline(data);
        var zip = await File.ReadAllBytesAsync(data.ValidZip());
        Assert.Equal(401, (await http.Send("GET", "/api/admin/routes")).Status);
        var uploaded = await http.Send("POST", "/api/admin/routes/upload", zip, uploadToken: UploadToken, contentType: "application/zip");
        Assert.Equal(200, uploaded.Status);
        using var json = JsonDocument.Parse(uploaded.Body);
        var id = json.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("draft", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(404, (await http.Send("GET", "/api/hoeing-routes/packages/" + id)).Status);
        Assert.Equal(404, (await http.Send("GET", "/api/hoeing-routes/packages/" + id + "/download")).Status);
        var publicationBody = Encoding.UTF8.GetBytes("""{"expectedRevision":0,"reason":"test publish"}""");
        Assert.Equal(401, (await http.Send("POST", $"/api/admin/routes/{id}/publish", publicationBody, uploadToken: UploadToken)).Status);
        Assert.Equal(403, (await http.Send("POST", $"/api/admin/routes/{id}/publish", publicationBody, admin: true)).Status);
        Assert.Equal(403, (await http.Send("POST", "/api/admin/routes/upload", zip, admin: true, contentType: "application/zip")).Status);
        Assert.Equal(401, (await http.Send("POST", "/api/admin/routes/upload", zip, admin: true, csrf: http.Csrf, uploadToken: "wrong", contentType: "application/zip")).Status);
        Assert.Equal(403, (await http.Send("POST", $"/api/admin/routes/{id}/publish", publicationBody, admin: true, csrf: http.Csrf, uploadToken: UploadToken)).Status);
        Assert.Equal(200, (await http.Send("POST", $"/api/admin/routes/{id}/publish", publicationBody, admin: true, csrf: http.Csrf)).Status);
        Assert.Equal(200, (await http.Send("GET", "/api/hoeing-routes/current")).Status);
        var audit = await http.Send("GET", "/api/admin/routes/audit", admin: true);
        using var auditJson = JsonDocument.Parse(audit.Body);
        Assert.Equal(JsonValueKind.Array, auditJson.RootElement.ValueKind);
        Assert.True(auditJson.RootElement[0].TryGetProperty("atUtc", out _));
        var range = await http.Send("GET", $"/api/hoeing-routes/packages/{id}/download", range: "bytes=0-9");
        Assert.Equal(206, range.Status);
        Assert.Equal(10, range.Body.Length);
        Assert.Equal(200, (await http.Send("HEAD", $"/api/hoeing-routes/packages/{id}/download")).Status);
    }

    [Fact]
    public async Task RestoreAcceptsLargeLegalPathListsButOtherBodiesStayAt32KiB()
    {
        using var data = new RoutePackageTestData();
        await using var http = new InMemoryPipeline(data);
        var draft = await data.Upload(http.Store);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { sourceId = draft.Id, paths = Enumerable.Repeat("builtin/中文线路/001.json", 2000), expectedRevision = 0, reason = "large legal selection" });
        Assert.True(body.Length > 32768);
        Assert.Equal(200, (await http.Send("POST", $"/api/admin/routes/{draft.Id}/restore", body, admin: true, csrf: http.Csrf)).Status);
        Assert.Equal(413, (await http.Send("POST", $"/api/admin/routes/{draft.Id}/publish", body, admin: true, csrf: http.Csrf)).Status);
        Assert.Equal(413, (await http.Send("POST", $"/api/admin/routes/{draft.Id}/restore", new byte[2 * 1024 * 1024 + 1], admin: true, csrf: http.Csrf)).Status);
    }

    [Fact]
    public async Task InsecureRemoteRequestCannotUseSpoofedForwardedHeaders()
    {
        using var data = new RoutePackageTestData();
        await using var http = new InMemoryPipeline(data);
        Assert.Equal(400, (await http.Send("GET", "/api/hoeing-routes/policy", secure: false)).Status);
        Assert.Equal(400, (await http.Send("GET", "/routes/upload/", secure: false)).Status);
        Assert.Equal(400, (await http.Send("GET", "/ADMIN/ROUTES/", secure: false)).Status);
        // An ordinary unrelated endpoint is not intercepted by the route-package boundary.
        Assert.Equal(200, (await http.Send("GET", "/unrelated", secure: false)).Status);
    }

    // Exercises actual endpoint routing, auth metadata and middleware without opening a server or a port.
    private sealed class InMemoryPipeline : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;
        private readonly ClaimsPrincipal _admin = Admin();
        internal RoutePackageStore Store => _app.Services.GetRequiredService<RoutePackageStore>();
        internal string Csrf => _app.Services.GetRequiredService<RoutePackageAccess>().IssueCsrf(_admin);
        internal InMemoryPipeline(RoutePackageTestData data)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = data.Root, EnvironmentName = "Development" });
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data.Root, "keys")));
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("route-upload", limiter => { limiter.PermitLimit = 1; limiter.QueueLimit = 0; }));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ROUTE_PACKAGE_ROOT"] = data.StoreRoot, ["HOEING_REQUIRED_CLIENT_VERSION"] = RoutePackageTestData.Version, ["ROUTE_UPLOAD_TOKEN"] = UploadToken }).Build();
            builder.Services.AddRoutePackageServices(config, builder.Environment);
            _app = builder.Build();
            _app.UseRoutePackageBoundary();
            _app.UseRouting();
            _app.UseRateLimiter();
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.MapRoutePackages();
            _app.MapGet("/unrelated", () => Results.Ok());
            _app.UseEndpoints(_ => { });
            _pipeline = ((IApplicationBuilder)_app).Build();
        }
        internal async Task<(int Status, byte[] Body)> Send(string method, string path, byte[]? body = null,
            bool admin = false, string? csrf = null, string? uploadToken = null, string contentType = "application/json", string? range = null, bool secure = true)
        {
            using var scope = _app.Services.CreateScope();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Method = method; ctx.Request.Path = path; ctx.Request.Scheme = secure ? "https" : "http";
            ctx.Request.Host = new HostString("routes.example.test");
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            if (admin) ctx.User = _admin;
            if (csrf != null) ctx.Request.Headers["X-CSRF-Token"] = csrf;
            if (uploadToken != null) ctx.Request.Headers["X-Route-Upload-Token"] = uploadToken;
            if (range != null) ctx.Request.Headers.Range = range;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(body is { Length: > 0 }));
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit());
            ctx.Request.ContentType = contentType;
            ctx.Request.ContentLength = body?.Length ?? 0;
            ctx.Request.Body = new MemoryStream(body ?? []);
            using var output = new MemoryStream();
            ctx.Response.Body = output;
            await _pipeline(ctx);
            return (ctx.Response.StatusCode, output.ToArray());
        }
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
