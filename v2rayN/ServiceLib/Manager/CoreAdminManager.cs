using CliWrap;
using CliWrap.Buffered;
using ServiceLib.Common;

namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private int _linuxSudoPid = -1;
    private const string _tag = "CoreAdminHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        if (_config != null)
        {
            return;
        }
        _config = config;
        _updateFunc = updateFunc;

        await Task.CompletedTask;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    public async Task<ProcessService?> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath)
    {
        if (UseMacSudoHelper())
        {
            return await RunProcessAsMacHelper(fileName, configPath);
        }

        if (AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty())
        {
            throw new Exception(ResUI.FailedToRunCore);
        }

        StringBuilder sb = new();
        sb.AppendLine("#!/bin/bash");
        var cmdLine = $"{fileName.AppendQuotes()} {string.Format(coreInfo.Arguments, Utils.GetBinConfigPath(configPath).AppendQuotes())}";
        sb.AppendLine($"exec sudo -S -- {cmdLine}");
        var shFilePath = await FileUtils.CreateLinuxShellFile("run_as_sudo.sh", sb.ToString(), true);

        var procService = new ProcessService(
            fileName: shFilePath,
            arguments: "",
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: true,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        await procService.StartAsync(AppManager.Instance.LinuxSudoPwd);

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }
        _linuxSudoPid = procService.Id;

        return procService;
    }

    public async Task KillProcessAsLinuxSudo()
    {
        if (_linuxSudoPid < 0)
        {
            return;
        }

        try
        {
            if (UseMacSudoHelper())
            {
                var macArg = new List<string>() { "-n", "--", MacSudoHelper.KillScriptPath, _linuxSudoPid.ToString() };
                var macResult = await Cli.Wrap("/usr/bin/sudo")
                    .WithArguments(macArg)
                    .ExecuteBufferedAsync();
                await UpdateFunc(false, macResult.StandardOutput.ToString());
                _linuxSudoPid = -1;
                return;
            }

            var shellFileName = Utils.IsMacOS() ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName;
            var shFilePath = await FileUtils.CreateLinuxShellFile("kill_as_sudo.sh", EmbedUtils.GetEmbedText(shellFileName), true);
            if (shFilePath.Contains(' '))
            {
                shFilePath = shFilePath.AppendQuotes();
            }
            var arg = new List<string>() { "-c", $"sudo -S {shFilePath} {_linuxSudoPid}" };
            var result = await Cli.Wrap(Global.LinuxBash)
                .WithArguments(arg)
                .WithStandardInputPipe(PipeSource.FromString(AppManager.Instance.LinuxSudoPwd))
                .ExecuteBufferedAsync();

            await UpdateFunc(false, result.StandardOutput.ToString());
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        _linuxSudoPid = -1;
    }

    private static bool UseMacSudoHelper()
    {
        return Utils.IsMacOS() && MacSudoHelper.IsHelperInstalled(Utils.StartupPath());
    }

    private async Task<ProcessService?> RunProcessAsMacHelper(string fileName, string configPath)
    {
        var args = $"-n -- {MacSudoHelper.RunScriptPath.AppendQuotes()} {fileName.AppendQuotes()} {Utils.GetBinConfigPath(configPath).AppendQuotes()}";
        var procService = new ProcessService(
            fileName: "/usr/bin/sudo",
            arguments: args,
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: false,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        await procService.StartAsync();

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }

        _linuxSudoPid = procService.Id;
        return procService;
    }
}
