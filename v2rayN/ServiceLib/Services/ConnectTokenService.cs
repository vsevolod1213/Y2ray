using System.Net;
using System.Net.Mime;
using System.Text;

namespace ServiceLib.Services;

internal enum ConnectTokenStatus
{
    Ok,
    NotConfigured,
    InvalidToken,
    ExpiredOrUsed,
    NetworkError,
    InvalidResponse,
    ServerError
}

internal sealed record ConnectTokenResult(ConnectTokenStatus Status, string? Config);

internal static class ConnectTokenService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<ConnectTokenResult> ResolveAsync(string token, string? baseUrl)
    {
        if (token.IsNullOrEmpty())
        {
            return new ConnectTokenResult(ConnectTokenStatus.InvalidToken, null);
        }

        var resolveUrl = BuildResolveUrl(baseUrl);
        if (resolveUrl.IsNullOrEmpty())
        {
            return new ConnectTokenResult(ConnectTokenStatus.NotConfigured, null);
        }

        var payload = JsonUtils.Serialize(new { token }, false);
        if (payload.IsNullOrEmpty())
        {
            return new ConnectTokenResult(ConnectTokenStatus.InvalidResponse, null);
        }

        using var content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json);
        HttpResponseMessage response;
        try
        {
            response = await HttpClient.PostAsync(resolveUrl, content);
        }
        catch
        {
            return new ConnectTokenResult(ConnectTokenStatus.NetworkError, null);
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new ConnectTokenResult(ConnectTokenStatus.ExpiredOrUsed, null);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ConnectTokenResult(ConnectTokenStatus.InvalidToken, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ConnectTokenResult(ConnectTokenStatus.ServerError, null);
        }

        var body = await response.Content.ReadAsStringAsync();
        if (body.IsNullOrEmpty())
        {
            return new ConnectTokenResult(ConnectTokenStatus.InvalidResponse, null);
        }

        var result = JsonUtils.Deserialize<ConnectTokenResponse>(body);
        if (result?.Config.IsNullOrEmpty() ?? true)
        {
            return new ConnectTokenResult(ConnectTokenStatus.InvalidResponse, null);
        }

        return new ConnectTokenResult(ConnectTokenStatus.Ok, result.Config);
    }

    private static string? BuildResolveUrl(string? baseUrl)
    {
        if (baseUrl.IsNullOrEmpty())
        {
            return null;
        }

        var trimmed = baseUrl.Trim();
        if (!trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed += "/";
        }

        var url = trimmed + "connect/resolve";
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    private sealed class ConnectTokenResponse
    {
        public string? Config { get; set; }
        public string? DeviceId { get; set; }
        public string? ExpiresAt { get; set; }
    }
}
