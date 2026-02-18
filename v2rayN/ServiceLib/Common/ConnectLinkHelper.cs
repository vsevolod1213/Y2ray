namespace ServiceLib.Common;

public static class ConnectLinkHelper
{
    private const string Scheme = "yvpn";
    private const string ConnectHost = "connect";

    public static bool TryExtractToken(string? url, out string token)
    {
        token = string.Empty;
        if (url.IsNullOrEmpty())
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, ConnectHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query;
        if (query.StartsWith("?", StringComparison.Ordinal))
        {
            query = query[1..];
        }

        if (query.IsNullOrEmpty())
        {
            return false;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2)
            {
                continue;
            }

            if (!string.Equals(kv[0], "token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            token = Uri.UnescapeDataString(kv[1]);
            return token.IsNotEmpty();
        }

        return false;
    }
}
