using System.IO.Pipes;
using System.Text;

namespace ServiceLib.Common;

public static class DeepLinkService
{
    private const int ConnectTimeoutMs = 1200;
    private const int RetryDelayMs = 250;
    private const int MaxAttempts = 6;

    private static string? _startupUrl;
    private static int _serverStarted;
    private static CancellationTokenSource? _serverCts;

    public static void CaptureStartupArgs(string[]? args)
    {
        _startupUrl = ExtractUrl(args);
    }

    public static string? ExtractUrl(string[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (arg.IsNullOrEmpty())
            {
                continue;
            }

            var trimmed = arg.Trim();
            if (trimmed.StartsWith("yvpn://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    public static async Task<bool> SendToRunningInstanceAsync(string[]? args)
    {
        var url = ExtractUrl(args);
        if (url.IsNullOrEmpty())
        {
            return false;
        }

        return await SendUrlToRunningInstanceAsync(url);
    }

    public static async Task<bool> SendUrlToRunningInstanceAsync(string url)
    {
        if (url.IsNullOrEmpty())
        {
            return false;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(ConnectTimeoutMs);

                using var writer = new StreamWriter(client, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                await writer.WriteLineAsync(url);
                return true;
            }
            catch
            {
                await Task.Delay(RetryDelayMs);
            }
        }

        return false;
    }

    public static void StartServer(Func<string, Task> handler)
    {
        if (handler == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _serverStarted, 1) == 1)
        {
            return;
        }

        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => RunServerLoopAsync(handler, _serverCts.Token));
    }

    public static async Task HandleStartupUrlAsync(Func<string, Task> handler)
    {
        if (handler == null)
        {
            return;
        }

        var url = _startupUrl;
        if (url.IsNullOrEmpty())
        {
            return;
        }

        _startupUrl = null;
        await handler(url);
    }

    private static async Task RunServerLoopAsync(Func<string, Task> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                if (line.IsNullOrEmpty())
                {
                    continue;
                }

                await handler(line.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                try
                {
                    await Task.Delay(200, token);
                }
                catch
                {
                    return;
                }
            }
        }
    }

    private static string PipeName => $"yvpn-deeplink-{Utils.GetMd5(Utils.GetExePath())}";
}
