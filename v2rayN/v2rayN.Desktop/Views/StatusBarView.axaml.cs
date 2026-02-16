using DialogHostAvalonia;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private static Config _config;

    public StatusBarView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        ViewModel = StatusBarViewModel.Instance;
        ViewModel?.InitUpdateView(UpdateViewHandler);

        txtRunningServerDisplay.Tapped += TxtRunningServerDisplay_Tapped;
        txtRunningInfoDisplay.Tapped += TxtRunningServerDisplay_Tapped;
        btnQuickToggle.Click += BtnQuickToggle_Click;

        this.WhenActivated(disposables =>
        {
            //status bar
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundLanDisplay, v => v.txtInboundLanDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedDirectDisplay, v => v.txtSpeedDirectDisplay.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.SystemProxySelected)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => RefreshQuickToggle())
                .DisposeWith(disposables);
        });

        //spEnableTun.IsVisible = (Utils.IsWindows() || AppHandler.Instance.IsAdministrator);

        if (Utils.IsNonWindows() && cmbSystemProxy.Items.IsReadOnly == false)
        {
            cmbSystemProxy.Items.RemoveAt(cmbSystemProxy.Items.Count - 1);
        }
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.DispatcherRefreshIcon:
                Dispatcher.UIThread.Post(() =>
                {
                    RefreshIcon();
                },
                DispatcherPriority.Default);
                break;

            case EViewAction.SetClipboardData:
                if (obj is null)
                {
                    return false;
                }

                await AvaUtils.SetClipboardData(this, (string)obj);
                break;

            case EViewAction.PasswordInput:
                return await PasswordInputAsync();
        }
        return await Task.FromResult(true);
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow.Icon = AvaUtils.GetAppIcon();
            var iconslist = TrayIcon.GetIcons(Application.Current);
            iconslist[0].Icon = desktop.MainWindow.Icon;
            TrayIcon.SetIcons(Application.Current, iconslist);
        }
    }

    private async Task<bool> PasswordInputAsync()
    {
        var dialog = new SudoPasswordInputView();
        var obj = await DialogHost.Show(dialog);

        var password = obj?.ToString();
        if (password.IsNullOrEmpty())
        {
            togEnableTun.IsChecked = false;
            return false;
        }

        AppManager.Instance.LinuxSudoPwd = password;
        return true;
    }

    private bool IsConnected()
    {
        return ViewModel != null && ViewModel.SystemProxySelected == (int)ESysProxyType.ForcedChange;
    }

    private void RefreshQuickToggle()
    {
        if (btnQuickToggle == null)
        {
            return;
        }

        var connected = IsConnected();
        btnQuickToggle.Content = connected ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ";
        btnQuickToggle.Background = connected
            ? new SolidColorBrush(Color.Parse("#2B0F1B"))
            : new SolidColorBrush(Color.Parse("#140C14"));
        btnQuickToggle.BorderBrush = connected
            ? new SolidColorBrush(Color.Parse("#A32C4E"))
            : new SolidColorBrush(Color.Parse("#3A1B2A"));
        btnQuickToggle.Foreground = connected
            ? new SolidColorBrush(Color.Parse("#FFF6FA"))
            : new SolidColorBrush(Color.Parse("#E6D6DE"));
    }

    private async void BtnQuickToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var targetState = !IsConnected();
        var ok = await ViewModel.SetQuickConnectionAsync(targetState, ViewModel.EnableTun);
        if (!ok)
        {
            RefreshQuickToggle();
        }
    }

    private void TxtRunningServerDisplay_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.TestServerAvailability();
    }
}
