using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Threading;
using DialogHostAvalonia;
using System.Globalization;
using System.Net.Sockets;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<StatusBarViewModel>
{
    private const double ModeThumbProxyLeft = 2d;
    private const double ModeThumbTunLeft = 102d;
    private static readonly TimeSpan ModeThumbAnimationDuration = TimeSpan.FromMilliseconds(180);

    private static Config _config;
    private readonly WindowNotificationManager? _manager;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly DispatcherTimer _connectionTimer;
    private readonly DispatcherTimer _modeThumbAnimationTimer;
    private readonly SemaphoreSlim _refreshConfigsSemaphore = new(1, 1);
    private CancellationTokenSource? _powerBrandAnimationCts;
    private DateTime? _connectedAtUtc;
    private DateTime _modeThumbAnimationStartedUtc;
    private bool _blCloseByUser;
    private bool _toggleInProgress;
    private bool _useTunMode;
    private bool _suppressConfigSelection;
    private bool _refreshConfigsPending;
    private bool _modeThumbInitialized;
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
        expConfigs.Expanded += ExpConfigs_StateChanged;
        expConfigs.Collapsed += ExpConfigs_StateChanged;
        lstConfigs.SelectionChanged += LstConfigs_SelectionChanged;

        btnOpenTg.Click += (_, _) => ProcUtils.ProcessStart("https://t.me/Y_VPN_bot");
        btnOpenSite.Click += (_, _) => ProcUtils.ProcessStart("https://yopen.ru");

        btnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
        btnMaximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        btnClose.Click += (_, _) => Close();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.EnableTun)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(enableTun =>
                {
                    _useTunMode = enableTun;
                    RefreshModeView();
                })
                .DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.EnableTun, v => v.ViewModel!.SystemProxySelected)
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
        var dialog = new SudoPasswordInputView();
        var obj = await DialogHost.Show(dialog);
        var password = obj?.ToString();

        if (password.IsNullOrEmpty())
        {
            return false;
        }

        AppManager.Instance.LinuxSudoPwd = password;
        return true;
    }

    private async Task DelegateSnackMsg(string content)
    {
        _manager?.Show(new Notification(null, content, NotificationType.Information));
        await Task.CompletedTask;
    }

    private bool IsConnected()
    {
        return _config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange;
    }

    private void RefreshConnectionView()
    {
        var connected = IsConnected();

        txtConnectionStatus.Text = connected ? "Подключено" : "Не подключено";
        txtConnectionStatus.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#FF8BA9"))
            : new SolidColorBrush(Color.Parse("#B4BBCB"));

        statusPill.Background = connected
            ? new SolidColorBrush(Color.Parse("#2A1020"))
            : new SolidColorBrush(Color.Parse("#1A1F2D"));
        statusPill.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#7C2743"))
            : new SolidColorBrush(Color.Parse("#32384A"));

        btnToggleConnection.Background = connected
            ? new SolidColorBrush(Color.Parse("#2B0F1D"))
            : new SolidColorBrush(Color.Parse("#121720"));
        btnToggleConnection.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#9E2345"))
            : new SolidColorBrush(Color.Parse("#2C3347"));

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
            ? new SolidColorBrush(Color.Parse("#FFF7FA"))
            : new SolidColorBrush(Color.Parse("#9AA3B5"));

        txtPowerHint.Text = hintText;
        txtPowerHint.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#F2A2B8"))
            : new SolidColorBrush(Color.Parse("#717A8E"));
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
            ? new SolidColorBrush(Color.Parse("#FFF7FA"))
            : new SolidColorBrush(Color.Parse("#BFC7D7"));

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
            ? new SolidColorBrush(Color.Parse("#95A0B5"))
            : new SolidColorBrush(Color.Parse("#F2F5FB"));
        btnModeTun.Foreground = _useTunMode
            ? new SolidColorBrush(Color.Parse("#F2F5FB"))
            : new SolidColorBrush(Color.Parse("#95A0B5"));

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

    private void ExpConfigs_StateChanged(object? sender, RoutedEventArgs e)
    {
        RefreshConfigsChevron();
    }

    private void RefreshConfigsChevron()
    {
        var isExpanded = expConfigs.IsExpanded;
        txtConfigsChevron.Text = isExpanded ? "▾" : "▸";
        txtConfigsChevron.Foreground = isExpanded
            ? new SolidColorBrush(Color.Parse("#6EA8FF"))
            : new SolidColorBrush(Color.Parse("#95A0B5"));
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
        if (_useTunMode == useTun)
        {
            return;
        }

        _useTunMode = useTun;
        _config.TunModeItem.EnableTun = useTun;
        await ConfigHandler.SaveConfig(_config);
        RefreshModeView();

        if (!IsConnected() || ViewModel == null)
        {
            return;
        }

        await ViewModel.SetQuickConnectionAsync(true, _useTunMode);
        var inboundPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (!await WaitForSocksReadyAsync(inboundPort))
        {
            await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
            NoticeManager.Instance.Enqueue($"{ResUI.FailedToRunCore} (127.0.0.1:{inboundPort})");
        }

        RefreshConnectionView();
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
                var server = await ConfigHandler.GetDefaultServer(_config);
                if (server == null)
                {
                    NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                    await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
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
                    await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
                    return;
                }

                var coreType = AppManager.Instance.GetCoreType(server, server.ConfigType);
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
                var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var coreMsg);
                if (coreExec.IsNullOrEmpty())
                {
                    NoticeManager.Instance.Enqueue(coreMsg);
                    await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
                    return;
                }

                await ViewModel.SetQuickConnectionAsync(true, _useTunMode);
                var inboundPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                if (!await WaitForSocksReadyAsync(inboundPort))
                {
                    await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
                    NoticeManager.Instance.Enqueue($"{ResUI.FailedToRunCore} (127.0.0.1:{inboundPort})");
                    return;
                }
            }
            else
            {
                await ViewModel.SetQuickConnectionAsync(false, _useTunMode);
            }

            await Task.Delay(200);
            RefreshConnectionView();
        }
        finally
        {
            _toggleInProgress = false;
            btnToggleConnection.IsEnabled = true;
        }
    }

    private static async Task<bool> WaitForSocksReadyAsync(int port, int maxAttempts = 20, int delayMs = 150)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            using var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(Global.Loopback, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(delayMs));
                if (completed == connectTask && client.Connected)
                {
                    return true;
                }
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

    protected override void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_config.UiItem.AutoHideStartup)
        {
            ShowHideWindow(false);
        }

        RefreshModeView();
        RefreshConnectionView();
        RefreshConfigsChevron();
        _ = RefreshConfigListAsync();
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }
}
