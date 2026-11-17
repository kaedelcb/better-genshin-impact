using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace BgiCoordinatorServer.Services;

/// <summary>The same composition root is used by production and focused wiring tests.</summary>
public static class RoutePackageHosting
{
    public static bool AddRoutePackageServices(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var root = Path.GetFullPath(configuration["ROUTE_PACKAGE_ROOT"]
            ?? Path.Combine(environment.ContentRootPath, "data", "route-packages"));
        var webRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot")));
        if (root.Equals(webRoot, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ROUTE_PACKAGE_ROOT 必须位于 wwwroot 之外。");
        var maxText = configuration["ROUTE_PACKAGE_MAX_BYTES"];
        var max = maxText is null ? 2L * 1024 * 1024 * 1024
            : long.TryParse(maxText, out var value) && value >= 4L * 1024 * 1024 && value <= 1024L * 1024 * 1024 * 1024 ? value
            : throw new InvalidOperationException("ROUTE_PACKAGE_MAX_BYTES 必须是 4 MiB 至 1 TiB 的字节整数。");
        services.AddSingleton(new RoutePackageAccess(configuration["ROUTE_UPLOAD_TOKEN"]));
        services.AddSingleton(_ => new RoutePackageStore(root, configuration["HOEING_REQUIRED_CLIENT_VERSION"], max));
        services.AddSingleton<RoutePackagePolicy>();
        var proxies = ParseTrustedProxyIps(configuration["ROUTE_TRUSTED_PROXY_IPS"]);
        // No middleware at all when empty: clearing both trust lists alone would trust every proxy.
        if (proxies.Length == 0) return false;
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
        });
        return true;
    }

    public static IPAddress[] ParseTrustedProxyIps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split(',').Select(part =>
        {
            var literal = part.Trim();
            // IPAddress.TryParse alone also accepts integer, octal and abbreviated IPv4 addresses.
            if (!IPAddress.TryParse(literal, out var ip) || literal.Contains('%')
                || ip.AddressFamily == AddressFamily.InterNetwork && (literal.Split('.').Length != 4
                    || literal.Split('.').Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9')
                        || p.Length > 1 && p[0] == '0'))
                || ip.AddressFamily == AddressFamily.InterNetworkV6 && (!literal.Contains(':') || literal.Contains('[') || literal.Contains(']')))
                throw new InvalidOperationException("ROUTE_TRUSTED_PROXY_IPS 只能是逗号分隔的精确 IP 地址，不支持网段、主机名或简写。");
            return ip;
        }).Distinct().ToArray();
    }
}
