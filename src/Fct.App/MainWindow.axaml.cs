using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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

    // Parameterless path is for the XAML previewer only; the running app resolves the DI ctor.
    public MainWindow() : this(new MainViewModel(), new SatelliteHost(NullLoggerFactory.Instance), NullLoggerFactory.Instance) { }

    public MainWindow(MainViewModel vm, SatelliteHost satellite, ILoggerFactory loggerFactory)
    {
        _vm = vm;
        _satellite = satellite;
        _log = loggerFactory.CreateLogger<MainWindow>();

        InitializeComponent();
        DataContext = _vm;

        // The Plugins page owns these interactions; the window owns the satellite + the picker.
        _vm.PluginsPage.RetryRequested += () => _ = StartSatelliteAsync();
        _vm.PluginsPage.AddPluginRequested += () => _ = PickPluginAsync();
    }

    // Kick off the satellite once the shell is on screen, so the window paints first.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = StartSatelliteAsync();
    }

    private async Task PickPluginAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a plugin",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(".NET plugin") { Patterns = new[] { "*.dll", "*.exe" } },
            },
        });
        if (files.FirstOrDefault() is { } f)
            _vm.PluginsPage.AddPlugin(f.Path.LocalPath);
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

    // ---- custom window chrome ----
    // Dragging and double-click-to-maximize are handled natively via the title bar's
    // WindowDecorationProperties.ElementRole; the buttons keep their explicit actions.
    private void OnMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximize();

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // Drain the satellite (plugins DeInit -> persist state, then it exits) BEFORE this window is
    // destroyed. The plugin config windows are SetParent-embedded into this window, so tearing it
    // down first wedges the satellite's UI thread on cross-process window teardown and the deinit
    // Invoke never runs. So cancel the first close, await the graceful shutdown, then close for real.
    // SatelliteLifetime.StopAsync remains an idempotent backstop for non-window shutdown paths.
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
