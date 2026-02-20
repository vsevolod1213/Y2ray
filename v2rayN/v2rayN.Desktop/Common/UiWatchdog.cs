using System.Diagnostics;
using Avalonia.Threading;
using ServiceLib.Common;

namespace v2rayN.Desktop.Common;

public static class UiWatchdog
{
    private static readonly TimeSpan UiPingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static long _lastUiTick;
    private static bool _started;
    private static System.Timers.Timer? _checkTimer;

    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _lastUiTick = Stopwatch.GetTimestamp();

        var uiTimer = new DispatcherTimer { Interval = UiPingInterval };
        uiTimer.Tick += (_, _) => _lastUiTick = Stopwatch.GetTimestamp();
        uiTimer.Start();

        _checkTimer = new System.Timers.Timer(CheckInterval.TotalMilliseconds)
        {
            AutoReset = true
        };
        _checkTimer.Elapsed += (_, _) => Check();
        _checkTimer.Start();
    }

    private static void Check()
    {
        var elapsed = Stopwatch.GetElapsedTime(_lastUiTick);
        if (elapsed > StallThreshold)
        {
            Logging.SaveLog($"UI watchdog: UI thread not responding for {elapsed.TotalSeconds:F1}s");
        }
    }
}
