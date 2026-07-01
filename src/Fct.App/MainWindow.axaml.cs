using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fct.App.Hosting;
using Fct.App.ViewModels;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ILogger<MainWindow> _log;
    private readonly SatelliteHost _satellite;
    private readonly INotificationHub _notifications;

    // Parameterless path is for the XAML previewer only; the running app resolves the DI ctor.
    public MainWindow() : this(new MainViewModel(),
        new SatelliteHost(NullLoggerFactory.Instance, NullGameEventSink.Instance),
        NullLoggerFactory.Instance, new NotificationService()) { }

    public MainWindow(MainViewModel vm, SatelliteHost satellite, ILoggerFactory loggerFactory,
        INotificationHub notifications)
    {
        _vm = vm;
        _satellite = satellite;
        _notifications = notifications;
        _log = loggerFactory.CreateLogger<MainWindow>();

        InitializeComponent();
        DataContext = _vm;

        // The shell owns the satellite lifecycle and the OS file picker; the view models raise intent.
        _vm.SatelliteRestartRequested += () => _ = StartSatelliteAsync();
        _vm.AddPluginRequested += () => _ = PickPluginAsync();
    }

    // Kick off the satellite once the shell is on screen, so the window paints first — unless the
    // user has turned auto-launch off, in which case we sit idle until they start it.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm.SettingsPage.LaunchSatelliteOnStartup)
            _ = StartSatelliteAsync();
        else
            _vm.SetIdle();
    }

    // Install a native plugin: a folder holding a plugin.json manifest, copied into the host's
    // plugins directory. It loads on the next launch (the loader scans at startup).
    private async Task PickPluginAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a native plugin folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault() is { } f)
            InstallPlugin(f.Path.LocalPath);
    }

    private void InstallPlugin(string sourceDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(sourceDir, "plugin.json")))
            {
                _notifications.Publish(NotificationSeverity.Warning, "Plugins", "That folder isn't a plugin",
                    "Pick a folder containing a plugin.json manifest.");
                return;
            }

            var name = new DirectoryInfo(sourceDir).Name;
            var dest = Path.Combine(AppContext.BaseDirectory, "plugins", name);
            CopyDirectory(sourceDir, dest);
            _log.LogInformation(LogEvents.NativePluginLoaded, "Installed plugin folder {Name} -> {Dest}", name, dest);
            _notifications.Publish(NotificationSeverity.Success, "Plugins", $"Installed {name}",
                "The plugin loads the next time you start the app.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected, ex, "Plugin install failed from {Source}", sourceDir);
            _notifications.Publish(NotificationSeverity.Error, "Plugins", "Couldn't install the plugin", ex.Message);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private async Task StartSatelliteAsync()
    {
        _vm.SetStarting();
        try
        {
            var result = await _satellite.StartAsync();
            var pid = _satellite.Process?.Id ?? 0;

            _vm.SetOnline(result.Handshake, pid, result.Plugins);
            _log.LogInformation(LogEvents.SatelliteStarted,
                "Satellite online: pid {Pid}, {PluginCount} plugin window(s) [{Handshake}]",
                pid, result.Plugins.Count, result.Handshake);
        }
        catch (Exception ex)
        {
            _vm.SetOffline(ex.Message);
            _log.LogError(LogEvents.SatelliteLaunchFailed, ex, "Satellite launch failed");
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

    // Drain the satellite (plugins DeInit -> persist state, then it exits) BEFORE this window is
    // destroyed. The plugin config windows are SetParent-embedded into this window, so tearing it
    // down first wedges the satellite's UI thread on cross-process window teardown and the deinit
    // Invoke never runs. So cancel the first close, await the graceful shutdown, then close for real.
    private bool _satelliteDrained;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_satelliteDrained)
        {
            e.Cancel = true;
            try { await _satellite.ShutdownAsync(TimeSpan.FromSeconds(8)); }
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
