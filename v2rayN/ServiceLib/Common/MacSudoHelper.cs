using CliWrap;
using CliWrap.Buffered;
using ServiceLib;

namespace ServiceLib.Common;

public enum MacSudoPromptResult
{
    Installed,
    Canceled,
    Failed
}

public static class MacSudoHelper
{
    private const string HelperRunScriptName = "run_as_root.sh";
    private const string HelperKillScriptName = "kill_as_root.sh";
    private const string HelperAllowedPathName = "allowed_path";
    private const string SudoersFileName = "yvpn";

    public static string HelperDir => Path.Combine("/Library/Application Support", Global.AppName);
    public static string RunScriptPath => Path.Combine(HelperDir, HelperRunScriptName);
    public static string KillScriptPath => Path.Combine(HelperDir, HelperKillScriptName);
    public static string AllowedPathFile => Path.Combine(HelperDir, HelperAllowedPathName);
    public static string SudoersPath => Path.Combine("/etc/sudoers.d", SudoersFileName);

    public static bool IsHelperInstalled(string? expectedBasePath = null)
    {
        if (!Utils.IsMacOS())
        {
            return false;
        }

        if (!File.Exists(RunScriptPath) || !File.Exists(KillScriptPath) || !File.Exists(SudoersPath) || !File.Exists(AllowedPathFile))
        {
            return false;
        }

        if (!IsRunScriptUpToDate())
        {
            return false;
        }

        if (expectedBasePath.IsNullOrEmpty())
        {
            return true;
        }

        try
        {
            var allowed = File.ReadAllText(AllowedPathFile).Trim();
            if (allowed.IsNullOrEmpty())
            {
                return false;
            }

            var allowedFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowed));
            var expectedFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedBasePath));
            return string.Equals(allowedFull, expectedFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> InstallHelperAsync(string password, string basePath)
    {
        if (!Utils.IsMacOS() || password.IsNullOrEmpty() || basePath.IsNullOrEmpty())
        {
            return false;
        }

        try
        {
            var tempDir = Utils.GetTempPath();
            var tempRun = Path.Combine(tempDir, HelperRunScriptName);
            var tempKill = Path.Combine(tempDir, HelperKillScriptName);
            var tempAllowed = Path.Combine(tempDir, HelperAllowedPathName);
            var tempSudoers = Path.Combine(tempDir, "yvpn_sudoers");

            File.WriteAllText(tempRun, BuildRunScript());
            File.WriteAllText(tempKill, EmbedUtils.GetEmbedText(Global.KillAsSudoOSXShellFileName));
            var normalizedBasePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
            File.WriteAllText(tempAllowed, normalizedBasePath);
            File.WriteAllText(tempSudoers, BuildSudoers(Environment.UserName));

            var cmd = new StringBuilder();
            cmd.AppendLine($"mkdir -p {HelperDir.AppendQuotes()}");
            cmd.AppendLine($"cp {tempRun.AppendQuotes()} {RunScriptPath.AppendQuotes()}");
            cmd.AppendLine($"cp {tempKill.AppendQuotes()} {KillScriptPath.AppendQuotes()}");
            cmd.AppendLine($"cp {tempAllowed.AppendQuotes()} {AllowedPathFile.AppendQuotes()}");
            cmd.AppendLine($"chown root:wheel {RunScriptPath.AppendQuotes()} {KillScriptPath.AppendQuotes()} {AllowedPathFile.AppendQuotes()}");
            cmd.AppendLine($"chmod 755 {RunScriptPath.AppendQuotes()} {KillScriptPath.AppendQuotes()}");
            cmd.AppendLine($"chmod 644 {AllowedPathFile.AppendQuotes()}");
            cmd.AppendLine($"cp {tempSudoers.AppendQuotes()} {SudoersPath.AppendQuotes()}");
            cmd.AppendLine($"chown root:wheel {SudoersPath.AppendQuotes()}");
            cmd.AppendLine($"chmod 440 {SudoersPath.AppendQuotes()}");
            cmd.AppendLine($"if ! /usr/sbin/visudo -cf {SudoersPath.AppendQuotes()}; then rm -f {SudoersPath.AppendQuotes()}; exit 1; fi");

            var args = new List<string>
            {
                "-S",
                Global.LinuxBash,
                "-c",
                cmd.ToString()
            };

            var result = await Cli.Wrap("/usr/bin/sudo")
                .WithArguments(args)
                .WithStandardInputPipe(PipeSource.FromString(password + Environment.NewLine))
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                return false;
            }

            return IsHelperInstalled(normalizedBasePath);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> InstallHelperWithSystemPromptAsync(string basePath)
    {
        return await InstallHelperWithSystemPromptDetailedAsync(basePath) == MacSudoPromptResult.Installed;
    }

    public static async Task<MacSudoPromptResult> InstallHelperWithSystemPromptDetailedAsync(string basePath)
    {
        if (!Utils.IsMacOS() || basePath.IsNullOrEmpty())
        {
            return MacSudoPromptResult.Failed;
        }

        try
        {
            var tempDir = Utils.GetTempPath();
            var tempRun = Path.Combine(tempDir, HelperRunScriptName);
            var tempKill = Path.Combine(tempDir, HelperKillScriptName);
            var tempAllowed = Path.Combine(tempDir, HelperAllowedPathName);
            var tempSudoers = Path.Combine(tempDir, "yvpn_sudoers");
            var tempInstall = Path.Combine(tempDir, "install_helper.sh");

            var normalizedBasePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
            File.WriteAllText(tempRun, BuildRunScript());
            File.WriteAllText(tempKill, EmbedUtils.GetEmbedText(Global.KillAsSudoOSXShellFileName));
            File.WriteAllText(tempAllowed, normalizedBasePath);
            File.WriteAllText(tempSudoers, BuildSudoers(Environment.UserName));

            var script = new StringBuilder();
            script.AppendLine("#!/bin/bash");
            script.AppendLine("set -e");
            script.AppendLine($"mkdir -p {HelperDir.AppendQuotes()}");
            script.AppendLine($"cp {tempRun.AppendQuotes()} {RunScriptPath.AppendQuotes()}");
            script.AppendLine($"cp {tempKill.AppendQuotes()} {KillScriptPath.AppendQuotes()}");
            script.AppendLine($"cp {tempAllowed.AppendQuotes()} {AllowedPathFile.AppendQuotes()}");
            script.AppendLine($"chown root:wheel {RunScriptPath.AppendQuotes()} {KillScriptPath.AppendQuotes()} {AllowedPathFile.AppendQuotes()}");
            script.AppendLine($"chmod 755 {RunScriptPath.AppendQuotes()} {KillScriptPath.AppendQuotes()}");
            script.AppendLine($"chmod 644 {AllowedPathFile.AppendQuotes()}");
            script.AppendLine($"cp {tempSudoers.AppendQuotes()} {SudoersPath.AppendQuotes()}");
            script.AppendLine($"chown root:wheel {SudoersPath.AppendQuotes()}");
            script.AppendLine($"chmod 440 {SudoersPath.AppendQuotes()}");
            script.AppendLine($"if ! /usr/sbin/visudo -cf {SudoersPath.AppendQuotes()}; then rm -f {SudoersPath.AppendQuotes()}; exit 1; fi");

            File.WriteAllText(tempInstall, script.ToString());
            try
            {
                File.SetUnixFileMode(tempInstall, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
            }

            var escapedInstallPathForShell = tempInstall.Replace("'", "'\\''");
            var installCommand = $"/bin/bash '{escapedInstallPathForShell}'";
            var prompt = "Yvpn: для режима «Туннель» нужны права администратора. Введите пароль macOS.";
            var appleScript = $"do shell script {AppleScriptQuote(installCommand)} with prompt {AppleScriptQuote(prompt)} with administrator privileges";
            var result = await Cli.Wrap("/usr/bin/osascript")
                .WithArguments(new List<string> { "-e", appleScript })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                var stderr = result.StandardError ?? string.Empty;
                if (stderr.Contains("-128", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
                {
                    return MacSudoPromptResult.Canceled;
                }

                Logging.SaveLog($"MacSudoHelper: osascript failed ({result.ExitCode}) {result.StandardError} {result.StandardOutput}");
                return MacSudoPromptResult.Failed;
            }

            return IsHelperInstalled(normalizedBasePath)
                ? MacSudoPromptResult.Installed
                : MacSudoPromptResult.Failed;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MacSudoHelper.InstallHelperWithSystemPromptDetailedAsync", ex);
            return MacSudoPromptResult.Failed;
        }
    }

    private static string BuildRunScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine($"ALLOWED_PATH_FILE={AllowedPathFile.AppendQuotes()}");
        sb.AppendLine("if [[ ! -f \"$ALLOWED_PATH_FILE\" ]]; then");
        sb.AppendLine("  echo \"allowed_path missing\"; exit 1; fi");
        sb.AppendLine("BASE=$(cat \"$ALLOWED_PATH_FILE\")");
        sb.AppendLine("BASE=${BASE%/}");
        sb.AppendLine();
        sb.AppendLine("CORE=${1:-}");
        sb.AppendLine("CONFIG=${2:-}");
        sb.AppendLine("if [[ -z \"$CORE\" || -z \"$CONFIG\" ]]; then");
        sb.AppendLine("  echo \"Usage: run_as_root.sh <core> <config>\"; exit 1; fi");
        sb.AppendLine();
        sb.AppendLine("if [[ \"$CORE\" != \"$BASE\"/bin/* ]]; then");
        sb.AppendLine("  echo \"Denied core path\"; exit 1; fi");
        sb.AppendLine("if [[ \"$CONFIG\" != \"$BASE\"/binConfigs/* && \"$CONFIG\" != \"$BASE\"/guiConfigs/* ]]; then");
        sb.AppendLine("  echo \"Denied config path\"; exit 1; fi");
        sb.AppendLine();
        sb.AppendLine("CORE_NAME=$(basename \"$CORE\")");
        sb.AppendLine("if [[ \"$CORE_NAME\" == *sing-box* ]]; then");
        sb.AppendLine("  exec \"$CORE\" run -c \"$CONFIG\" --disable-color");
        sb.AppendLine("elif [[ \"$CORE_NAME\" == *mihomo* || \"$CORE_NAME\" == \"clash\" ]]; then");
        sb.AppendLine("  exec \"$CORE\" -f \"$CONFIG\" -d \"$BASE/bin\"");
        sb.AppendLine("else");
        sb.AppendLine("  echo \"Denied core name: $CORE_NAME\"; exit 1; fi");
        return sb.ToString();
    }

    private static bool IsRunScriptUpToDate()
    {
        try
        {
            var content = File.ReadAllText(RunScriptPath);
            return content.Contains("BASE=${BASE%/}", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSudoers(string user)
    {
        var runPath = EscapeSudoersPath(RunScriptPath);
        var killPath = EscapeSudoersPath(KillScriptPath);
        return $"{user} ALL=(root) NOPASSWD: {runPath}, {killPath}" + Environment.NewLine;
    }

    private static string EscapeSudoersPath(string path)
    {
        return path.Replace(" ", "\\ ");
    }

    private static string AppleScriptQuote(string value)
    {
        return $"\"{value.Replace("\\\\", "\\\\\\\\").Replace("\"", "\\\\\"").Replace("\n", "\\\\n")}\"";
    }
}
