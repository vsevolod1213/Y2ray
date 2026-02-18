using Microsoft.Win32;

namespace ServiceLib.Common;

public static class UriSchemeHelper
{
    private const string Scheme = "yvpn";

    public static void EnsureRegistered()
    {
        if (!Utils.IsWindows())
        {
            return;
        }

        try
        {
            var exePath = Utils.GetExePath();
            if (exePath.IsNullOrEmpty())
            {
                return;
            }

            var baseKey = $@"Software\\Classes\\{Scheme}";
            using var key = Registry.CurrentUser.CreateSubKey(baseKey);
            if (key == null)
            {
                return;
            }

            key.SetValue(string.Empty, $"URL:{Global.AppName} Protocol");
            key.SetValue("URL Protocol", string.Empty);

            using (var iconKey = key.CreateSubKey("DefaultIcon"))
            {
                iconKey?.SetValue(string.Empty, $"{exePath},1");
            }

            using (var commandKey = key.CreateSubKey(@"shell\\open\\command"))
            {
                commandKey?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
            }
        }
        catch
        {
            // Best-effort only; do not block startup.
        }
    }
}
