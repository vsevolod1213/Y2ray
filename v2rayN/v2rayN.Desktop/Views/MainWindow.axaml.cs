using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Threading;
using DialogHostAvalonia;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<StatusBarViewModel>
{
    private const double ModeThumbProxyLeft = 2d;
    private const double ModeThumbTunLeft = 102d;
    private static readonly TimeSpan ModeThumbAnimationDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan QuickConnectionTimeout = TimeSpan.FromSeconds(20);
    private const string InternetSettingsRegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] ConflictingVpnProcessMarkers =
    [
        "clash",
        "nekobox",
        "hiddify",
        "outline",
        "wireguard",
        "openvpn",
        "amnezia",
        "protonvpn",
        "surfshark",
        "nordvpn",
        "expressvpn",
        "mullvad",
        "v2raytun",
        "qv2ray",
        "v2rayu",
        "v2rayn",
        "warp"
    ];

    private static Config _config;
    private readonly WindowNotificationManager? _manager;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly DispatcherTimer _connectionTimer;
    private readonly DispatcherTimer _modeThumbAnimationTimer;
    private readonly SemaphoreSlim _refreshConfigsSemaphore = new(1, 1);
    private readonly SemaphoreSlim _passwordDialogSemaphore = new(1, 1);
    private CancellationTokenSource? _powerBrandAnimationCts;
    private DateTime? _connectedAtUtc;
    private DateTime _modeThumbAnimationStartedUtc;
    private bool _blCloseByUser;
    private bool _toggleInProgress;
    private bool _useTunMode;
    private bool _suppressConfigSelection;
    private bool _refreshConfigsPending;
    private bool _modeThumbInitialized;
    private bool _startupDialogsShown;
    private bool? _powerBrandConnectedState;
    private double _modeThumbCurrentLeft = ModeThumbProxyLeft;
    private double _modeThumbFromLeft;
    private double _modeThumbToLeft;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        ForceRussianLocalization();

        _manager = new WindowNotificationManager(TopLevel.GetTopLevel(this))
        {
            MaxItems = 3,
            Position = NotificationPosition.TopRight
        };

        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectionTimer.Tick += ConnectionTimer_Tick;
        _modeThumbAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _modeThumbAnimationTimer.Tick += ModeThumbAnimationTimer_Tick;

        ViewModel = StatusBarViewModel.Instance;
        ViewModel?.InitUpdateView(UpdateViewHandler);
        _mainWindowViewModel = new MainWindowViewModel(UpdateViewHandler);

        _useTunMode = _config.TunModeItem.EnableTun;

        btnToggleConnection.Click += BtnToggleConnection_Click;
        btnImportFromClipboard.Click += BtnImportFromClipboard_Click;
        btnModeProxy.Click += BtnModeProxy_Click;
        btnModeTun.Click += BtnModeTun_Click;
        lstConfigs.SelectionChanged += LstConfigs_SelectionChanged;

        btnOpenTg.Click += (_, _) => ProcUtils.ProcessStart("https://t.me/Y_VPN_bot");
        btnOpenSite.Click += (_, _) => ProcUtils.ProcessStart("https://yopen.ru");

        btnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
        btnMaximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        btnClose.Click += (_, _) => Close();

        if (Utils.IsMacOS())
        {
            ApplyMacWindowStyle();
        }

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.EnableTun)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(enableTun =>
                {
                    var previousMode = _useTunMode;
                    _useTunMode = enableTun;
                    RefreshModeView();
                    if (previousMode != enableTun && IsConnected())
                    {
                        _connectedAtUtc = DateTime.UtcNow;
                        UpdateConnectionDurationText();
                    }
                })
                .DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.EnableTun, v => v.ViewModel!.SystemProxySelected)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => RefreshConnectionView())
                .DisposeWith(disposables);

            AppEvents.ConnectionStateRefreshRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => RefreshConnectionView())
                .DisposeWith(disposables);

            AppEvents.SendSnackMsgRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async content => await DelegateSnackMsg(content))
                .DisposeWith(disposables);

            AppEvents.AppExitRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => StorageUI())
                .DisposeWith(disposables);

            AppEvents.ShutdownRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(content => Shutdown(content))
                .DisposeWith(disposables);

            AppEvents.ShowHideWindowRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(blShow => ShowHideWindow(blShow))
                .DisposeWith(disposables);

            AppEvents.ProfilesRefreshRequested
                .AsObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_evt => { _ = RefreshConfigListAsync(); })
                .DisposeWith(disposables);
        });

        if (Utils.IsWindows())
        {
            ThreadPool.RegisterWaitForSingleObject(Program.ProgramStarted, OnProgramStarted, null, -1, false);
            Title = Global.AppName;
        }
        else
        {
            Title = Global.AppName;
        }

        if (_config.UiItem.AutoHideStartup && Utils.IsWindows())
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void ForceRussianLocalization()
    {
        var ru = new CultureInfo("ru");
        CultureInfo.CurrentCulture = ru;
        CultureInfo.CurrentUICulture = ru;
        Thread.CurrentThread.CurrentCulture = ru;
        Thread.CurrentThread.CurrentUICulture = ru;

        if (_config.UiItem.CurrentLanguage != "ru")
        {
            _config.UiItem.CurrentLanguage = "ru";
            _ = ConfigHandler.SaveConfig(_config);
        }
    }

    private void OnProgramStarted(object? state, bool timeout)
    {
        Dispatcher.UIThread.Post(() => ShowHideWindow(true), DispatcherPriority.Default);
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.DispatcherRefreshIcon:
                Dispatcher.UIThread.Post(RefreshIcon, DispatcherPriority.Default);
                break;

            case EViewAction.SetClipboardData:
                if (obj is not string text)
                {
                    return false;
                }
                await AvaUtils.SetClipboardData(this, text);
                break;

            case EViewAction.AddServerViaClipboard:
                var clipboardData = await AvaUtils.GetClipboardData(this);
                if (clipboardData.IsNullOrEmpty())
                {
                    NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                    return false;
                }

                await _mainWindowViewModel.AddServerViaClipboardAsync(clipboardData);
                return true;

            case EViewAction.ShowYesNo:
                if (obj is not string msg || msg.IsNullOrEmpty())
                {
                    return false;
                }

                return await UI.ShowYesNo(this, msg) == ButtonResult.Yes;

            case EViewAction.PasswordInput:
                return await PasswordInputAsync();
        }

        return true;
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow.Icon = AvaUtils.GetAppIcon();
            var iconsList = TrayIcon.GetIcons(Application.Current);
            iconsList[0].Icon = desktop.MainWindow.Icon;
            TrayIcon.SetIcons(Application.Current, iconsList);
        }
    }

    private async Task<bool> PasswordInputAsync()
    {
        if (Utils.IsMacOS())
        {
            if (MacSudoHelper.IsHelperInstalled(Utils.StartupPath()))
            {
                return true;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await MacSudoHelper.InstallHelperWithSystemPromptDetailedAsync(Utils.StartupPath());
                Logging.SaveLog($"Mac helper system prompt result: {result}, attempt={attempt + 1}");
                if (result == MacSudoPromptResult.Installed)
                {
                    NoticeManager.Instance.Enqueue(ResUI.SudoHelperInstalled);
                    return true;
                }

                if (result != MacSudoPromptResult.Canceled)
                {
                    break;
                }
            }

            NoticeManager.Instance.Enqueue(ResUI.SudoHelperInstallFailed);
            return false;
        }

        return await PasswordInputWithDialogAsync();
    }

    private async Task<bool> PasswordInputWithDialogAsync()
    {
        if (!await _passwordDialogSemaphore.WaitAsync(0))
        {
            return false;
        }

        try
        {
            var dialog = new SudoPasswordInputView();
            var obj = await DialogHost.Show(dialog);
            if (obj is SudoPasswordResult result)
            {
                if (result.Password.IsNullOrEmpty())
                {
                    return false;
                }

                if (Utils.IsMacOS() && result.InstallHelper)
                {
                    var installed = await MacSudoHelper.InstallHelperAsync(result.Password, Utils.StartupPath());
                    if (installed)
                    {
                        AppManager.Instance.LinuxSudoPwd = string.Empty;
                        NoticeManager.Instance.Enqueue(ResUI.SudoHelperInstalled);
                        return true;
                    }

                    NoticeManager.Instance.Enqueue(ResUI.SudoHelperInstallFailed);
                }

                AppManager.Instance.LinuxSudoPwd = result.Password;
                return true;
            }

            var password = obj?.ToString();
            if (password.IsNullOrEmpty())
            {
                return false;
            }

            AppManager.Instance.LinuxSudoPwd = password;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _passwordDialogSemaphore.Release();
        }
    }

    private async Task DelegateSnackMsg(string content)
    {
        _manager?.Show(new Notification(null, content, NotificationType.Information));
        await Task.CompletedTask;
    }

    private bool IsConnected()
    {
        return _config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange && IsOwnCoreProcessRunning();
    }

    private static bool IsOwnCoreProcessRunning()
    {
        var startupPath = Path.GetFullPath(Utils.StartupPath());
        string[] coreNames = ["xray", "sing-box", "mihomo", "v2ray"];

        foreach (var coreName in coreNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(coreName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(processPath);
                    if (fullPath.StartsWith(startupPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private void RefreshConnectionView()
    {
        var connected = IsConnected();

        txtConnectionStatus.Text = connected ? "Подключено" : "Не подключено";
        txtConnectionStatus.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#F2A2B8"))
            : new SolidColorBrush(Color.Parse("#B8A0AB"));

        statusPill.Background = connected
            ? new SolidColorBrush(Color.Parse("#2B0E1B"))
            : new SolidColorBrush(Color.Parse("#1A0D14"));
        statusPill.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#8A2B48"))
            : new SolidColorBrush(Color.Parse("#2C1B26"));

        btnToggleConnection.Background = connected
            ? new SolidColorBrush(Color.Parse("#2B0F1B"))
            : new SolidColorBrush(Color.Parse("#140C14"));
        btnToggleConnection.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#A32C4E"))
            : new SolidColorBrush(Color.Parse("#3A1B2A"));

        UpdatePowerBrandVisual(connected);

        EnsureConnectionTimerState(connected);
    }

    private void UpdatePowerBrandVisual(bool connected)
    {
        if (_powerBrandConnectedState == null)
        {
            _powerBrandConnectedState = connected;
            SetPowerBrandState(connected, connected ? "нажмите для отключения" : "нажмите для запуска");
            return;
        }

        if (_powerBrandConnectedState == connected)
        {
            return;
        }

        _powerBrandConnectedState = connected;
        _ = AnimatePowerBrandTransitionAsync(connected);
    }

    private void SetPowerBrandState(bool connected, string hintText)
    {
        txtPowerBrand.Text = connected ? "YourVPN" : "Y-VPN";
        txtPowerBrand.Opacity = connected ? 1.0 : 0.78;
        txtPowerBrand.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#FFF6FA"))
            : new SolidColorBrush(Color.Parse("#B39BA6"));

        txtPowerHint.Text = hintText;
        txtPowerHint.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#F07A9B"))
            : new SolidColorBrush(Color.Parse("#8F7A86"));
    }

    private async Task AnimatePowerBrandTransitionAsync(bool connected)
    {
        _powerBrandAnimationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _powerBrandAnimationCts = cts;

        var frames = connected
            ? new[] { "Y-VPN", "Y VPN", "YVPN", "YoVPN", "YouVPN", "YourVPN" }
            : new[] { "YourVPN", "YouVPN", "YoVPN", "YVPN", "Y VPN", "Y-VPN" };

        txtPowerHint.Text = connected ? "подключаем..." : "отключаем...";
        txtPowerBrand.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#FFF6FA"))
            : new SolidColorBrush(Color.Parse("#C6A8B5"));

        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();

                var progress = (double)i / Math.Max(1, frames.Length - 1);
                var opacity = progress < 0.5
                    ? Lerp(0.95, 0.4, progress * 2)
                    : Lerp(0.4, connected ? 1.0 : 0.78, (progress - 0.5) * 2);

                txtPowerBrand.Text = frames[i];
                txtPowerBrand.Opacity = opacity;

                await Task.Delay(70, cts.Token);
            }

            SetPowerBrandState(connected, connected ? "нажмите для отключения" : "нажмите для запуска");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_powerBrandAnimationCts, cts))
            {
                _powerBrandAnimationCts = null;
            }
        }
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    private void RefreshModeView()
    {
        btnModeProxy.Foreground = _useTunMode
            ? new SolidColorBrush(Color.Parse("#B89EAA"))
            : new SolidColorBrush(Color.Parse("#F6F1F3"));
        btnModeTun.Foreground = _useTunMode
            ? new SolidColorBrush(Color.Parse("#F6F1F3"))
            : new SolidColorBrush(Color.Parse("#B89EAA"));

        if (Resources.TryGetValue(_useTunMode ? "ModeThumbTun" : "ModeThumbProxy", out var thumbBrush)
            && thumbBrush is IBrush brush)
        {
            modeToggleThumb.Background = brush;
        }

        AnimateModeThumb(_useTunMode);
    }

    private void AnimateModeThumb(bool useTun)
    {
        var targetLeft = useTun ? ModeThumbTunLeft : ModeThumbProxyLeft;
        if (!_modeThumbInitialized)
        {
            _modeThumbInitialized = true;
            SetModeThumbLeft(targetLeft);
            return;
        }

        if (Math.Abs(_modeThumbCurrentLeft - targetLeft) < 0.01d)
        {
            SetModeThumbLeft(targetLeft);
            return;
        }

        _modeThumbFromLeft = _modeThumbCurrentLeft;
        _modeThumbToLeft = targetLeft;
        _modeThumbAnimationStartedUtc = DateTime.UtcNow;
        if (!_modeThumbAnimationTimer.IsEnabled)
        {
            _modeThumbAnimationTimer.Start();
        }
    }

    private void ModeThumbAnimationTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _modeThumbAnimationStartedUtc;
        var progress = elapsed.TotalMilliseconds / ModeThumbAnimationDuration.TotalMilliseconds;
        if (progress >= 1d)
        {
            _modeThumbAnimationTimer.Stop();
            SetModeThumbLeft(_modeThumbToLeft);
            return;
        }

        var easedProgress = 1d - Math.Pow(1d - Math.Clamp(progress, 0d, 1d), 3d);
        var left = Lerp(_modeThumbFromLeft, _modeThumbToLeft, easedProgress);
        SetModeThumbLeft(left);
    }

    private void SetModeThumbLeft(double left)
    {
        _modeThumbCurrentLeft = left;
        modeToggleThumb.Margin = new Thickness(left, 2, 0, 2);
    }

    private void EnsureConnectionTimerState(bool connected)
    {
        if (!connected)
        {
            _connectedAtUtc = null;
            _connectionTimer.Stop();
            txtConnectionDuration.Text = "00:00:00";
            return;
        }

        if (_connectedAtUtc == null)
        {
            _connectedAtUtc = DateTime.UtcNow;
        }

        if (!_connectionTimer.IsEnabled)
        {
            _connectionTimer.Start();
        }

        UpdateConnectionDurationText();
    }

    private void ConnectionTimer_Tick(object? sender, EventArgs e)
    {
        UpdateConnectionDurationText();
    }

    private void UpdateConnectionDurationText()
    {
        if (_connectedAtUtc == null)
        {
            txtConnectionDuration.Text = "00:00:00";
            return;
        }

        var elapsed = DateTime.UtcNow - _connectedAtUtc.Value;
        var hours = (int)elapsed.TotalHours;
        txtConnectionDuration.Text = $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private async Task RefreshConfigListAsync()
    {
        if (!await _refreshConfigsSemaphore.WaitAsync(0))
        {
            _refreshConfigsPending = true;
            return;
        }

        try
        {
            var profiles = await AppManager.Instance.ProfileModels(_config.SubIndexId, "") ?? [];
            var configItems = profiles
                .Where(p => !p.IndexId.IsNullOrEmpty())
                .Select(p => new ComboItem { ID = p.IndexId, Text = GetProfileDisplayName(p) })
                .ToList();

            _suppressConfigSelection = true;
            lstConfigs.ItemsSource = configItems;

            var selected = configItems.FirstOrDefault(p => p.ID == _config.IndexId) ?? configItems.FirstOrDefault();
            lstConfigs.SelectedItem = selected;
            _suppressConfigSelection = false;

            if (selected != null && _config.IndexId != selected.ID)
            {
                await ConfigHandler.SetDefaultServerIndex(_config, selected.ID);
            }
        }
        finally
        {
            _refreshConfigsSemaphore.Release();
            if (_refreshConfigsPending)
            {
                _refreshConfigsPending = false;
                _ = RefreshConfigListAsync();
            }
        }
    }

    private async void LstConfigs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressConfigSelection)
        {
            return;
        }

        if (lstConfigs.SelectedItem is not ComboItem selected || selected.ID.IsNullOrEmpty())
        {
            return;
        }

        await ConfigHandler.SetDefaultServerIndex(_config, selected.ID);

        if (IsConnected())
        {
            AppEvents.ReloadRequested.Publish();
        }

        AppEvents.ProfilesRefreshRequested.Publish();
    }

    private async void BtnModeProxy_Click(object? sender, RoutedEventArgs e)
    {
        await SetConnectionModeAsync(false);
    }

    private async void BtnModeTun_Click(object? sender, RoutedEventArgs e)
    {
        await SetConnectionModeAsync(true);
    }

    private async Task SetConnectionModeAsync(bool useTun)
    {
        if (ViewModel == null)
        {
            return;
        }

        if (useTun && Utils.IsWindows() && !Utils.IsAdministrator())
        {
            var restartAsAdmin = await ShowAdminRequiredDialogAsync();
            if (restartAsAdmin)
            {
                await AppManager.Instance.RebootAsAdmin();
                return;
            }

            useTun = false;
        }

        if (ViewModel.EnableTun == useTun)
        {
            return;
        }

        if (IsConnected())
        {
            var switched = await TrySetQuickConnectionAsync(true, useTun);
            if (!switched)
            {
                return;
            }
            _useTunMode = useTun;
            RefreshModeView();
            _connectedAtUtc = DateTime.UtcNow;
            UpdateConnectionDurationText();
            return;
        }

        ViewModel.EnableTun = useTun;
        _useTunMode = useTun;
        RefreshModeView();
    }

    private async void BtnToggleConnection_Click(object? sender, RoutedEventArgs e)
    {
        await ToggleConnectionAsync();
    }

    private async Task ToggleConnectionAsync()
    {
        if (_toggleInProgress || ViewModel == null)
        {
            return;
        }

        _toggleInProgress = true;
        btnToggleConnection.IsEnabled = false;
        try
        {
            var nextStateConnected = !IsConnected();
            if (nextStateConnected)
            {
                if (_useTunMode && Utils.IsWindows() && !Utils.IsAdministrator())
                {
                    var restartAsAdmin = await ShowAdminRequiredDialogAsync();
                    if (restartAsAdmin)
                    {
                        await AppManager.Instance.RebootAsAdmin();
                        return;
                    }

                    await SetConnectionModeAsync(false);
                }

                if (TryGetConnectionConflictMessage(out var conflictMessage))
                {
                    var disableConflict = await ShowProxyConflictDialogAsync(conflictMessage);
                    if (disableConflict)
                    {
                        await CleanupConflictingProxySettingsAsync();
                    }
                }

                var server = await ConfigHandler.GetDefaultServer(_config);
                if (server == null)
                {
                    NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                    await TrySetQuickConnectionAsync(false, _useTunMode);
                    return;
                }

                var checkMsgs = await ActionPrecheckManager.Instance.Check(server.IndexId);
                if (checkMsgs.Count > 0)
                {
                    foreach (var msg in checkMsgs.Take(10))
                    {
                        NoticeManager.Instance.SendMessage(msg);
                    }
                    NoticeManager.Instance.Enqueue(Utils.List2String(checkMsgs.Take(10).ToList(), true));
                    await TrySetQuickConnectionAsync(false, _useTunMode);
                    return;
                }

                var coreType = AppManager.Instance.GetCoreType(server, server.ConfigType);
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
                var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var coreMsg);
                if (coreExec.IsNullOrEmpty())
                {
                    NoticeManager.Instance.Enqueue(coreMsg);
                    await TrySetQuickConnectionAsync(false, _useTunMode);
                    return;
                }

                if (!await TrySetQuickConnectionAsync(true, _useTunMode))
                {
                    return;
                }
                var inboundPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                if (!await WaitForSocksReadyAsync(inboundPort))
                {
                    await TrySetQuickConnectionAsync(false, _useTunMode);
                    NoticeManager.Instance.Enqueue($"{ResUI.FailedToRunCore} (127.0.0.1:{inboundPort})");
                    return;
                }
            }
            else
            {
                await TrySetQuickConnectionAsync(false, _useTunMode);
            }

            await Task.Delay(200);
            RefreshConnectionView();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ToggleConnectionAsync", ex);
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
        finally
        {
            _toggleInProgress = false;
            btnToggleConnection.IsEnabled = true;
        }
    }

    private async Task<bool> TrySetQuickConnectionAsync(bool enable, bool useTun)
    {
        if (ViewModel == null)
        {
            return false;
        }

        try
        {
            var operation = ViewModel.SetQuickConnectionAsync(enable, useTun);
            var completed = await Task.WhenAny(operation, Task.Delay(QuickConnectionTimeout));
            if (completed != operation)
            {
                Logging.SaveLog($"SetQuickConnectionAsync timeout. enable={enable}, useTun={useTun}");
                NoticeManager.Instance.Enqueue("Операция подключения заняла слишком много времени. Повторите попытку.");
                return false;
            }

            return await operation;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TrySetQuickConnectionAsync", ex);
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return false;
        }
    }
    private async Task<bool> ShowAdminRequiredDialogAsync()
    {
        const string title = "Требуются права администратора";
        const string message = "У приложения отсутствуют необходимые права администратора, режим \"Туннель\" будет переключен на режим \"Системный прокси\".";
        return await ShowTwoActionDialogAsync(title, message, "Понятно", "Перезапустить");
    }

    private async Task<bool> ShowProxyConflictDialogAsync(string message)
    {
        return await ShowTwoActionDialogAsync("Предупреждение", message, "Пропустить", "Отключить");
    }

    private static Control BuildDialogButton(string text, bool isPrimary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 36,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(9),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(1),
            Background = isPrimary
                ? new SolidColorBrush(Color.Parse("#1D78FF"))
                : new SolidColorBrush(Color.Parse("#1A1D26")),
            BorderBrush = isPrimary
                ? new SolidColorBrush(Color.Parse("#2E8DFF"))
                : new SolidColorBrush(Color.Parse("#2C3342")),
            Foreground = isPrimary
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.Parse("#D3DAEA"))
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    private async Task<bool> ShowTwoActionDialogAsync(string title, string message, string leftButtonText, string rightButtonText)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F7FA")),
            TextWrapping = TextWrapping.Wrap
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#B8C1D6")),
            TextWrapping = TextWrapping.Wrap
        };

        var buttonsGrid = new Grid
        {
            Margin = new Thickness(0, 18, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10
        };

        var leftButton = BuildDialogButton(leftButtonText, false, () => DialogHost.Close(null, false));
        var rightButton = BuildDialogButton(rightButtonText, true, () => DialogHost.Close(null, true));

        buttonsGrid.Children.Add(leftButton);
        Grid.SetColumn(leftButton, 0);
        buttonsGrid.Children.Add(rightButton);
        Grid.SetColumn(rightButton, 1);

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(titleBlock);
        panel.Children.Add(messageBlock);
        panel.Children.Add(buttonsGrid);

        var container = new Border
        {
            Width = 380,
            MaxWidth = 380,
            Background = new SolidColorBrush(Color.Parse("#11141C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#242B38")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = panel
        };

        var result = await DialogHost.Show(container);
        return result is bool accepted && accepted;
    }

    private bool TryGetConnectionConflictMessage(out string message)
    {
        message = string.Empty;
        var hasProxyConflict = HasConflictingSystemProxySettings(out var proxyDetails);
        var hasClientConflict = TryGetConflictingClientName(out var clientName);

        if (!hasProxyConflict && !hasClientConflict)
        {
            return false;
        }

        if (hasProxyConflict && hasClientConflict)
        {
            message = $"{proxyDetails}\nТакже обнаружен запущенный VPN-клиент: {clientName}.";
            return true;
        }

        if (hasProxyConflict)
        {
            message = proxyDetails;
            return true;
        }

        message = $"Обнаружен запущенный VPN-клиент: {clientName}.";
        return true;
    }

    private bool HasConflictingSystemProxySettings(out string details)
    {
        details = string.Empty;
        if (!Utils.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsRegPath, false);
            if (key == null)
            {
                return false;
            }

            var proxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0), CultureInfo.InvariantCulture) == 1;
            var proxyServer = key.GetValue("ProxyServer", string.Empty)?.ToString() ?? string.Empty;
            var autoConfigUrl = key.GetValue("AutoConfigURL", string.Empty)?.ToString() ?? string.Empty;

            if (proxyEnable)
            {
                var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                var mixedPort = AppManager.Instance.GetLocalPort(EInboundProtocol.mixed);

                var isLoopbackProxy = proxyServer.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                                      || proxyServer.Contains("localhost", StringComparison.OrdinalIgnoreCase);
                var hasExpectedPort = proxyServer.Contains($":{socksPort}", StringComparison.OrdinalIgnoreCase)
                                      || proxyServer.Contains($":{mixedPort}", StringComparison.OrdinalIgnoreCase);

                if (!isLoopbackProxy || !hasExpectedPort)
                {
                    details = "Обнаружены активированные настройки прокси.";
                    return true;
                }

                if (!IsOwnCoreProcessRunning())
                {
                    details = "Обнаружены активированные настройки прокси, но клиент не подключен.";
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(autoConfigUrl)
                && !autoConfigUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !autoConfigUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                details = "Обнаружен активный PAC-скрипт прокси.";
                return true;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("HasConflictingSystemProxySettings", ex);
        }

        return false;
    }

    private bool TryGetConflictingClientName(out string processName)
    {
        processName = string.Empty;
        var startupPath = Path.GetFullPath(Utils.StartupPath());

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var normalized = name.ToLowerInvariant();
                    if (!ConflictingVpnProcessMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    string? processPath = null;
                    try
                    {
                        processPath = process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(processPath))
                    {
                        var normalizedPath = Path.GetFullPath(processPath);
                        if (normalizedPath.StartsWith(startupPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    processName = name;
                    return true;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TryGetConflictingClientName", ex);
        }

        return false;
    }

    private async Task CleanupConflictingProxySettingsAsync()
    {
        await TrySetQuickConnectionAsync(false, _useTunMode);
        RefreshConnectionView();
    }

    private async Task EnsureStartupStateAsync()
    {
        if (_startupDialogsShown)
        {
            return;
        }

        _startupDialogsShown = true;

        await TrySetQuickConnectionAsync(false, _useTunMode);
        RefreshConnectionView();

        if (_useTunMode && Utils.IsWindows() && !Utils.IsAdministrator())
        {
            var restartAsAdmin = await ShowAdminRequiredDialogAsync();
            if (restartAsAdmin)
            {
                await AppManager.Instance.RebootAsAdmin();
                return;
            }

            await SetConnectionModeAsync(false);
        }

        if (TryGetConnectionConflictMessage(out var conflictMessage))
        {
            var disableConflict = await ShowProxyConflictDialogAsync(conflictMessage);
            if (disableConflict)
            {
                await CleanupConflictingProxySettingsAsync();
            }
        }
    }

    private static async Task<bool> WaitForSocksReadyAsync(int port, int maxAttempts = 20, int delayMs = 150)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(delayMs);
            try
            {
                await client.ConnectAsync(Global.Loopback, port, cts.Token);
                if (client.Connected)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // connect probe timed out
            }
            catch
            {
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private async void BtnImportFromClipboard_Click(object? sender, RoutedEventArgs e)
    {
        var clipboardData = await AvaUtils.GetClipboardData(this);
        if (clipboardData.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }

        var ret = await ConfigHandler.AddBatchServers(_config, clipboardData, _config.SubIndexId, false);
        if (ret > 0)
        {
            NoticeManager.Instance.Enqueue(string.Format(ResUI.SuccessfullyImportedServerViaClipboard, ret));
            AppEvents.ProfilesRefreshRequested.Publish();
            AppEvents.ReloadRequested.Publish();
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    private async void BtnDeleteConfigRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string configId } || configId.IsNullOrEmpty())
        {
            return;
        }

        var profile = await AppManager.Instance.GetProfileItem(configId);
        if (profile == null)
        {
            return;
        }

        await ConfigHandler.RemoveServers(_config, new List<ProfileItem> { profile });
        AppEvents.ProfilesRefreshRequested.Publish();

        if (IsConnected())
        {
            AppEvents.ReloadRequested.Publish();
        }
    }

    private static string GetProfileDisplayName(ProfileItemModel profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Remarks))
        {
            return profile.Remarks.Trim();
        }

        return profile.IndexId;
    }

    private void TopBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        _powerBrandAnimationCts?.Cancel();
        _modeThumbAnimationTimer.Stop();

        if (_blCloseByUser)
        {
            return;
        }

        Logging.SaveLog("OnClosing -> " + e.CloseReason.ToString());
        switch (e.CloseReason)
        {
            case WindowCloseReason.OwnerWindowClosing or WindowCloseReason.WindowClosing:
                e.Cancel = true;
                ShowHideWindow(false);
                break;

            case WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown:
                await AppManager.Instance.AppExitAsync(false);
                break;
        }

        base.OnClosing(e);
    }

    private void Shutdown(bool byUser)
    {
        if (byUser && !_blCloseByUser)
        {
            _blCloseByUser = true;
        }

        StorageUI();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void ShowHideWindow(bool? blShow)
    {
        var bl = blShow ??
                 (Utils.IsLinux()
                     ? (!AppManager.Instance.ShowInTaskbar ^ (WindowState == WindowState.Minimized))
                     : !AppManager.Instance.ShowInTaskbar);

        if (bl)
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Focus();
        }
        else
        {
            if (Utils.IsLinux() && _config.UiItem.Hide2TrayWhenClose == false)
            {
                WindowState = WindowState.Minimized;
                return;
            }

            foreach (var ownedWindow in OwnedWindows)
            {
                ownedWindow.Close();
            }
            Hide();
        }

        AppManager.Instance.ShowInTaskbar = bl;
    }

    protected override async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_config.UiItem.AutoHideStartup)
        {
            ShowHideWindow(false);
        }

        _useTunMode = ViewModel?.EnableTun ?? _config.TunModeItem.EnableTun;

        RefreshModeView();
        RefreshConnectionView();
        await EnsureStartupStateAsync();
        _ = RefreshConfigListAsync();
    }

    private void ApplyMacWindowStyle()
    {
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = false;
        ExtendClientAreaTitleBarHeightHint = 0;

        topBar.IsVisible = false;
        btnMinimize.IsVisible = false;
        btnMaximize.IsVisible = false;
        btnClose.IsVisible = false;

        if (rootGrid.RowDefinitions.Count > 0)
        {
            rootGrid.RowDefinitions[0].Height = new GridLength(0);
        }
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }
}
