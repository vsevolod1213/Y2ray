using Avalonia.Controls.Notifications;
using DialogHostAvalonia;
using System.Net.Sockets;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<StatusBarViewModel>
{
    private static Config _config;
    private readonly WindowNotificationManager? _manager;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private bool _blCloseByUser;
    private bool _toggleInProgress;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _manager = new WindowNotificationManager(TopLevel.GetTopLevel(this))
        {
            MaxItems = 3,
            Position = NotificationPosition.TopRight
        };

        ViewModel = StatusBarViewModel.Instance;
        ViewModel?.InitUpdateView(UpdateViewHandler);
        _mainWindowViewModel = new MainWindowViewModel(UpdateViewHandler);

        btnToggleConnection.Click += BtnToggleConnection_Click;
        btnImportFromClipboard.Click += BtnImportFromClipboard_Click;
        btnExitApp.Click += BtnExitApp_Click;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);

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
        });

        if (Utils.IsWindows())
        {
            ThreadPool.RegisterWaitForSingleObject(Program.ProgramStarted, OnProgramStarted, null, -1, false);
            Title = $"{Global.AppName} - {(Utils.IsAdministrator() ? ResUI.RunAsAdmin : ResUI.NotRunAsAdmin)}";
        }
        else
        {
            Title = Global.AppName;
        }

        if (_config.UiItem.AutoHideStartup && Utils.IsWindows())
        {
            WindowState = WindowState.Minimized;
        }

        RefreshConnectionView();
        AppEvents.ProfilesRefreshRequested.Publish();
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
            desktop.MainWindow.Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
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
        return _config.TunModeItem.EnableTun
               && _config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange;
    }

    private void RefreshConnectionView()
    {
        var connected = IsConnected();

        txtConnectionStatus.Text = connected ? "Connected" : "Disconnected";
        txtConnectionStatus.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#FF8AA6"))
            : new SolidColorBrush(Color.Parse("#A1A7B7"));

        btnToggleConnection.Content = connected ? "DISCONNECT" : "CONNECT";
        btnToggleConnection.Background = connected
            ? new SolidColorBrush(Color.Parse("#242733"))
            : new SolidColorBrush(Color.Parse("#B4143C"));
        btnToggleConnection.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#555A6F"))
            : new SolidColorBrush(Color.Parse("#EF6C8A"));
        btnToggleConnection.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#F5F7FF"))
            : new SolidColorBrush(Color.Parse("#FFF4F6"));
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
                    await ViewModel.SetQuickConnectionAsync(false);
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
                    await ViewModel.SetQuickConnectionAsync(false);
                    return;
                }

                var coreType = AppManager.Instance.GetCoreType(server, server.ConfigType);
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
                var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var coreMsg);
                if (coreExec.IsNullOrEmpty())
                {
                    NoticeManager.Instance.Enqueue(coreMsg);
                    await ViewModel.SetQuickConnectionAsync(false);
                    return;
                }

                await ViewModel.SetQuickConnectionAsync(true);
                var inboundPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                if (!await WaitForSocksReadyAsync(inboundPort))
                {
                    await ViewModel.SetQuickConnectionAsync(false);
                    NoticeManager.Instance.Enqueue($"{ResUI.FailedToRunCore} (127.0.0.1:{inboundPort})");
                    return;
                }
            }
            else
            {
                await ViewModel.SetQuickConnectionAsync(false);
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

    private async void BtnExitApp_Click(object? sender, RoutedEventArgs e)
    {
        _blCloseByUser = true;
        StorageUI();
        await AppManager.Instance.AppExitAsync(true);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
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

        RefreshConnectionView();
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }
}
