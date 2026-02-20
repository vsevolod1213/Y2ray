using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop;

public partial class App : Application
{
    private const string ToggleOnHeader = "ВКЛЮЧИТЬ";
    private const string ToggleOffHeader = "ВЫКЛЮЧИТЬ";
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

                UiWatchdog.Start();

                StatusBarViewModel.Instance
                    .WhenAnyValue(vm => vm.EnableTun)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshModeMenuState());

                StatusBarViewModel.Instance
                    .WhenAnyValue(vm => vm.SystemProxySelected)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshToggleConnectionMenuState());

                AppEvents.ProfilesRefreshRequested
                    .AsObservable()
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(__ => { _ = RefreshConfigsMenuAsync(); });
            }

            desktop.Exit += OnExit;
            desktop.MainWindow = new MainWindow();

            DeepLinkService.StartServer(url =>
            {
                AppEvents.DeepLinkRequested.Publish(url);
                return Task.CompletedTask;
            });
            _ = DeepLinkService.HandleStartupUrlAsync(url =>
            {
                AppEvents.DeepLinkRequested.Publish(url);
                return Task.CompletedTask;
            });

            if (Application.Current is { } app)
            {
                app.UrlsOpened += (_, e) =>
                {
                    if (e?.Urls == null)
                    {
                        return;
                    }

                    foreach (var uri in e.Urls)
                    {
                        var url = uri?.ToString();
                        if (url.IsNullOrEmpty())
                        {
                            continue;
                        }

                        AppEvents.ShowHideWindowRequested.Publish(true);
                        AppEvents.DeepLinkRequested.Publish(url);
                    }
                };
            }

            if (desktop is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.OpenUri)
                    {
                        var url = DeepLinkService.ExtractUrl(desktop.Args);
                        if (!url.IsNullOrEmpty())
                        {
                            AppEvents.ShowHideWindowRequested.Publish(true);
                            AppEvents.DeepLinkRequested.Publish(url);
                        }
                    }
                    else if (e.Kind == ActivationKind.Reopen)
                    {
                        AppEvents.ShowHideWindowRequested.Publish(true);
                    }
                };
            }

            RefreshModeMenuState();
            RefreshToggleConnectionMenuState();
            _ = RefreshConfigsMenuAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(500);
                _ = RefreshConfigsMenuAsync();
            });
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
        Logging.SaveLog("Tray: mode -> proxy");
        await SetTrayModeAsync(false);
    }

    private async void MenuModeTun_Click(object? sender, EventArgs e)
    {
        Logging.SaveLog("Tray: mode -> tunnel");
        await SetTrayModeAsync(true);
    }

    private async void MenuToggleConnection_Click(object? sender, EventArgs e)
    {
        Logging.SaveLog("Tray: toggle connection");
        var vm = StatusBarViewModel.Instance;
        var enable = !IsConnected();
        await vm.SetQuickConnectionAsync(enable, vm.EnableTun);
        RefreshToggleConnectionMenuState();
        AppEvents.ConnectionStateRefreshRequested.Publish();
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
            StatusBarViewModel.Instance.EnableTun = true;
            NoticeManager.Instance.Enqueue("Для режима ТУННЕЛЬ перезапустите приложение от имени администратора.");
            RefreshModeMenuState();
            return;
        }

        if (IsConnected())
        {
            await StatusBarViewModel.Instance.SetQuickConnectionAsync(true, useTun);
            RefreshModeMenuState();
            RefreshToggleConnectionMenuState();
            return;
        }

        StatusBarViewModel.Instance.EnableTun = useTun;
        RefreshModeMenuState();
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
            Logging.SaveLog($"Tray configs refresh: sub={config.SubIndexId}, count={profiles.Count}");

            var menuConfigsRoot = GetConfigurationsMenuItem();
            if (menuConfigsRoot != null)
            {
                var configMenu = menuConfigsRoot.Menu ?? new NativeMenu();
                configMenu.Items.Clear();

                foreach (var profile in profiles.Where(p => !p.IndexId.IsNullOrEmpty()))
                {
                    var profileId = profile.IndexId;
                    var item = new NativeMenuItem(GetProfileDisplayName(profile))
                    {
                        ToggleType = NativeMenuItemToggleType.Radio,
                        IsChecked = profileId == config.IndexId
                    };

                    item.Click += async (_, _) => await SelectConfigFromTrayAsync(profileId);
                    configMenu.Items.Add(item);
                }

                if (configMenu.Items.Count == 0)
                {
                    configMenu.Items.Add(new NativeMenuItem("Нет конфигураций")
                    {
                        IsEnabled = false
                    });
                }

                if (menuConfigsRoot.Menu == null)
                {
                    menuConfigsRoot.Menu = configMenu;
                }

                menuConfigsRoot.IsEnabled = true;
                Logging.SaveLog($"Tray configs applied: items={configMenu.Items.Count}");
                RefreshModeMenuState();
                RefreshToggleConnectionMenuState();
            }
            else
            {
                Logging.SaveLog("Tray configs refresh: menu root not found");
                _refreshConfigsMenuPending = true;
            }
        }
        finally
        {
            _refreshConfigsMenuSemaphore.Release();
            if (_refreshConfigsMenuPending)
            {
                _refreshConfigsMenuPending = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(200);
                    _ = RefreshConfigsMenuAsync();
                });
            }
        }
    }

    private async Task SelectConfigFromTrayAsync(string indexId)
    {
        if (indexId.IsNullOrEmpty())
        {
            return;
        }

        Logging.SaveLog($"Tray: select config {indexId}");
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
        if (menu == null)
        {
            return null;
        }

        var target = NormalizeHeader(header);
        return menu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(NormalizeHeader(item.Header), target, StringComparison.OrdinalIgnoreCase));
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
        var item = FindMenuItem(rootMenu, ConfigurationsMenuHeader);
        if (item != null)
        {
            return item;
        }

        if (rootMenu == null)
        {
            return null;
        }

        // Fallback: look for the placeholder submenu (Загрузка...).
        return rootMenu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(menuItem =>
                menuItem.Menu?.Items
                    .OfType<NativeMenuItem>()
                    .Any(child => string.Equals(NormalizeHeader(child.Header), "Загрузка...", StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static string NormalizeHeader(object? header)
    {
        return header?.ToString()?.Trim() ?? string.Empty;
    }

    private void RefreshToggleConnectionMenuState()
    {
        var toggleItem = GetToggleMenuItem();
        if (toggleItem == null)
        {
            return;
        }

        toggleItem.Header = IsConnected() ? ToggleOffHeader : ToggleOnHeader;
    }

    private NativeMenuItem? GetToggleMenuItem()
    {
        var rootMenu = GetTrayMenu();
        if (rootMenu == null)
        {
            return null;
        }

        return rootMenu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item =>
            {
                var header = item.Header?.ToString();
                return string.Equals(header, ToggleOnHeader, StringComparison.Ordinal)
                    || string.Equals(header, ToggleOffHeader, StringComparison.Ordinal);
            });
    }

    private static bool IsConnected()
    {
        return AppManager.Instance.Config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange;
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
