using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fct.Host;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Fct.Host.Plugins.Ui;
using Fct.App.ViewModels;
// Aliased: MainWindow inherits Window.Resources (IResourceDictionary), which shadows the
// Fct.App.Lang.Resources type name inside this class; "Lang" would itself collide with the
// Fct.App.Lang namespace since this file's namespace (Fct.App) is its direct parent.
using Strings = Fct.App.Lang.Resources;
using Fct.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ILogger<MainWindow> _log;
    private readonly SatelliteRouter _router;
    private readonly INotificationHub _notifications;

    // Parameterless path is for the XAML previewer only; the running app resolves the DI ctor.
    public MainWindow() : this(new MainViewModel(),
        new SatelliteRouter(
            new SatelliteSupervisor(NullLoggerFactory.Instance, NullGameEventSink.Instance),
            NullLoggerFactory.Instance),
        NullLoggerFactory.Instance, new NotificationService()) { }

    // Internal (the SatelliteRouter it takes is internal to Fct.Host, visible here via InternalsVisibleTo);
    // Program registers MainWindow with an explicit factory that calls this ctor.
    internal MainWindow(MainViewModel vm, SatelliteRouter router, ILoggerFactory loggerFactory,
        INotificationHub notifications)
    {
        _vm = vm;
        _router = router;
        _notifications = notifications;
        _log = loggerFactory.CreateLogger<MainWindow>();

        InitializeComponent();
        DataContext = _vm;

        // The shell owns the satellite lifecycle and the OS file picker; the view models raise intent.
        _vm.SatelliteRestartRequested += () => _ = StartSatelliteAsync();
        _vm.AddPluginRequested += () => _ = PickPluginAsync();
        _vm.UnloadPluginRequested += row => _ = UnloadPluginAsync(row);
        _vm.LocatePluginRequested += row => _ = LocatePluginAsync(row);

        // Legacy plugins loaded/unloaded live across the package satellites reconcile the flat roster.
        _router.PluginAnnounced += p => _vm.AddLegacyPlugin(p);
        _router.PluginUnloaded += key => _vm.RemoveLegacyPlugin(key);
    }

    // Kick off the satellite once the shell is on screen, so the window paints first — unless the
    // user has turned auto-launch off, in which case we sit idle until they start it.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FlushPluginUi();
        if (_vm.SettingsPage.LaunchSatelliteOnStartup)
            _ = StartSatelliteAsync();
        else
            _vm.SetIdle();
    }

    // Native plugins finish IPlugin.InitializeAsync before Avalonia starts (Program.cs runs
    // host.Start() before StartWithClassicDesktopLifetime), so IUiContributor.RegisterUi — which the
    // contract requires to run "on the UI thread, after init, with the shell live" — can only happen
    // here, once the window is actually on screen.
    private void FlushPluginUi()
    {
        var manager = App.Services?.GetService<PluginManager>();
        var coordinator = App.Services?.GetService<PluginUiCoordinator>();
        if (manager is null || coordinator is null) return;

        coordinator.FlushRegisterUi(manager.Loaded.Select(p => (p.Manifest, p.Instance)));
    }

    // Add a plugin from a .zip package or a single .dll — the installer unpacks/classifies whatever
    // is inside (native / recompiled-shim / real-legacy), routes it to the right executor, loads it
    // live, and persists it. A single .dll covers loose legacy plugins like FFXIV_ACT_Plugin.dll, and
    // picking a plugin's entry DLL inside its folder (e.g. OverlayPlugin.dll) installs it in place
    // with its siblings.
    private async Task PickPluginAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dialog_AddPluginTitle,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Plugin (package or assembly)") { Patterns = new[] { "*.zip", "*.dll" } },
            },
        });
        if (files.FirstOrDefault() is { } file)
            await InstallAsync(file.Path.LocalPath);
    }

    // Re-locate a missing install-by-reference legacy plugin: pick its DLL at the new home, then let
    // the installer re-point the registry record and reload it on the satellite. On success the
    // satellite announces the plugin, which replaces the "files missing" roster row.
    private async Task LocatePluginAsync(PluginViewModel row)
    {
        var installer = App.Services?.GetService<PluginInstaller>();
        if (installer is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = string.Format(Strings.Dialog_LocatePluginTitleFormat, row.Name),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Plugin assembly") { Patterns = new[] { "*.dll" } },
            },
        });
        if (files.FirstOrDefault() is { } file)
            await installer.RelinkLegacyAsync(row.Key, file.Path.LocalPath);   // installer surfaces its own notifications
    }

    private async Task InstallAsync(string source)
    {
        var installer = App.Services?.GetService<PluginInstaller>();
        if (installer is null)
        {
            _notifications.Publish(NotificationSeverity.Error, Strings.Notify_Source_Plugins, Strings.Notify_PluginInstallFailedTitle, "Installer unavailable");
            return;
        }
        await installer.InstallAsync(source, default);   // installer surfaces its own notifications
    }

    // Unload = uninstall (no restart). For a legacy row, drop it first so its embedded HWND is
    // un-parented before the satellite disposes the window; then delegate to the installer.
    private async Task UnloadPluginAsync(PluginViewModel row)
    {
        var installer = App.Services?.GetService<PluginInstaller>();
        if (installer is null) return;

        if (row.Kind == PluginKind.Legacy)
        {
            _vm.RemoveLegacyPlugin(row.Key);
            await installer.UninstallAsync(row.Key, default, LoadKind.RealLegacy);
        }
        else
        {
            await installer.UninstallAsync(row.Key, default, LoadKind.Native);
        }
    }

    private async Task StartSatelliteAsync()
    {
        _vm.SetStarting();
        try
        {
            // No global satellite anymore: the router spawns one satellite per installed package as we
            // replay the persisted real-legacy plugins. Roster rows arrive via router.PluginAnnounced;
            // records whose files vanished (install-by-reference sources move) get a re-locatable row.
            _vm.SetOnline(Array.Empty<SatellitePlugin>());
            if (App.Services?.GetService<PluginInstaller>() is { } installer)
                foreach (var missing in await installer.ReplayLegacyToSatelliteAsync())
                    _vm.AddMissingLegacyPlugin(missing.Id, missing.Title ?? missing.Id, missing.Dir);
            _log.LogInformation(LogEvents.SatelliteStarted, "Satellite topology online (one process per installed package)");
        }
        catch (Exception ex)
        {
            _vm.SetOffline(ex.Message);
            _log.LogError(LogEvents.SatelliteLaunchFailed, ex, "Satellite topology start failed");
        }
    }

    private void OnScrimPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        => _vm.Notifications.CloseCenterCommand.Execute(null);

    // ---- custom window chrome ----
    private void OnMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximize();

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // Drain every satellite (plugins DeInit -> persist state, then each exits) BEFORE this window is
    // destroyed. The plugin config windows are SetParent-embedded into this window, so tearing it
    // down first wedges a satellite's UI thread on cross-process window teardown and the deinit
    // Invoke never runs. So cancel the first close, await the graceful shutdown, then close for real.
    private bool _satelliteDrained;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_satelliteDrained)
        {
            e.Cancel = true;
            try { await _router.StopAllAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { _log.LogWarning(LogEvents.SatelliteShutdownTimeout, ex, "Satellite drain on close faulted"); }
            _satelliteDrained = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
