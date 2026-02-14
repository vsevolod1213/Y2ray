using v2rayN.Desktop.Views;

namespace v2rayN.Desktop;

public partial class App : Application
{
    private const string ModeMenuHeader = "Режим";
    private const string ProxyMenuHeader = "Прокси";
    private const string TunnelMenuHeader = "Туннель";
    private const string ConfigurationsMenuHeader = "Конфигурации";

    private readonly SemaphoreSlim _refreshConfigsMenuSemaphore = new(1, 1);
    private bool _refreshConfigsMenuPending;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Design.IsDesignMode)
            {
                AppManager.Instance.InitComponents();
                DataContext = StatusBarViewModel.Instance;

                AppEvents.ProfilesRefreshRequested
                    .AsObservable()
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(__ => { _ = RefreshConfigsMenuAsync(); });
            }

            desktop.Exit += OnExit;
            desktop.MainWindow = new MainWindow();

            RefreshModeMenuState();
            _ = RefreshConfigsMenuAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
    }

    private async void MenuModeProxy_Click(object? sender, EventArgs e)
    {
        await SetTrayModeAsync(false);
    }

    private async void MenuModeTun_Click(object? sender, EventArgs e)
    {
        await SetTrayModeAsync(true);
    }

    private void RefreshModeMenuState()
    {
        var useTun = AppManager.Instance.Config.TunModeItem.EnableTun;
        var (menuModeProxy, menuModeTun) = GetModeMenuItems();

        if (menuModeProxy != null)
        {
            menuModeProxy.IsChecked = !useTun;
        }
        if (menuModeTun != null)
        {
            menuModeTun.IsChecked = useTun;
        }
    }

    private async Task SetTrayModeAsync(bool useTun)
    {
        var config = AppManager.Instance.Config;
        if (config.TunModeItem.EnableTun == useTun)
        {
            RefreshModeMenuState();
            return;
        }

        if (useTun && Utils.IsWindows() && !Utils.IsAdministrator())
        {
            await AppManager.Instance.RebootAsAdmin();
            return;
        }

        config.TunModeItem.EnableTun = useTun;
        await ConfigHandler.SaveConfig(config);

        StatusBarViewModel.Instance.EnableTun = useTun;
        RefreshModeMenuState();

        if (config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange)
        {
            AppEvents.ReloadRequested.Publish();
        }
    }

    private async Task RefreshConfigsMenuAsync()
    {
        if (!await _refreshConfigsMenuSemaphore.WaitAsync(0))
        {
            _refreshConfigsMenuPending = true;
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            var profiles = await AppManager.Instance.ProfileModels(config.SubIndexId, "") ?? [];

            var menu = new NativeMenu();
            foreach (var profile in profiles.Where(p => !p.IndexId.IsNullOrEmpty()))
            {
                var profileId = profile.IndexId;
                var item = new NativeMenuItem(GetProfileDisplayName(profile))
                {
                    ToggleType = NativeMenuItemToggleType.Radio,
                    IsChecked = profileId == config.IndexId
                };

                item.Click += async (_, _) => await SelectConfigFromTrayAsync(profileId);
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new NativeMenuItem("Нет конфигураций")
                {
                    IsEnabled = false
                });
            }

            var menuConfigsRoot = GetConfigurationsMenuItem();
            if (menuConfigsRoot != null)
            {
                menuConfigsRoot.Menu = menu;
            }
        }
        finally
        {
            _refreshConfigsMenuSemaphore.Release();
            if (_refreshConfigsMenuPending)
            {
                _refreshConfigsMenuPending = false;
                _ = RefreshConfigsMenuAsync();
            }
        }
    }

    private async Task SelectConfigFromTrayAsync(string indexId)
    {
        if (indexId.IsNullOrEmpty())
        {
            return;
        }

        var config = AppManager.Instance.Config;
        if (config.IndexId == indexId)
        {
            await RefreshConfigsMenuAsync();
            return;
        }

        var profile = await AppManager.Instance.GetProfileItem(indexId);
        if (profile == null)
        {
            return;
        }

        if (await ConfigHandler.SetDefaultServerIndex(config, indexId) == 0)
        {
            AppEvents.ReloadRequested.Publish();
            AppEvents.ProfilesRefreshRequested.Publish();
        }

        await RefreshConfigsMenuAsync();
    }

    private NativeMenu? GetTrayMenu()
    {
        var icons = TrayIcon.GetIcons(Application.Current);
        if (icons.Count == 0)
        {
            return null;
        }

        return icons[0].Menu;
    }

    private static NativeMenuItem? FindMenuItem(NativeMenu? menu, string header)
    {
        return menu?.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));
    }

    private (NativeMenuItem? Proxy, NativeMenuItem? Tunnel) GetModeMenuItems()
    {
        var rootMenu = GetTrayMenu();
        var modeRoot = FindMenuItem(rootMenu, ModeMenuHeader);
        var modeMenu = modeRoot?.Menu;
        return (FindMenuItem(modeMenu, ProxyMenuHeader), FindMenuItem(modeMenu, TunnelMenuHeader));
    }

    private NativeMenuItem? GetConfigurationsMenuItem()
    {
        var rootMenu = GetTrayMenu();
        return FindMenuItem(rootMenu, ConfigurationsMenuHeader);
    }

    private static string GetProfileDisplayName(ProfileItemModel profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Remarks))
        {
            return profile.Remarks.Trim();
        }

        return profile.IndexId;
    }

    private async void MenuExit_Click(object? sender, EventArgs e)
    {
        await AppManager.Instance.AppExitAsync(false);
        AppManager.Instance.Shutdown(true);
    }
}
