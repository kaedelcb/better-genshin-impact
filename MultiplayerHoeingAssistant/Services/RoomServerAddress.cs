using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.Services;

internal static class RoomServerAddress
{
    internal static bool TryNormalize(string? address, out string server)
    {
        server = "";
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        server = uri.AbsoluteUri;
        bool stripped;
        do { server = MhaGatewayClient.NormalizeBaseUrl(server, out stripped); }
        while (stripped);
        return true;
    }

    internal static bool Same(string? first, string? second) =>
        string.Equals(Key(first), Key(second), StringComparison.Ordinal);

    private static string Key(string? address) => TryNormalize(address, out var server) ? server : address?.Trim() ?? "";
}
