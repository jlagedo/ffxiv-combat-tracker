using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fct.App.ViewModels;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _log;
    private readonly SatelliteHost _satellite;

    // One embedded view per plugin window; the config slot shows whichever plugin is selected.
    private readonly Dictionary<IntPtr, EmbeddedSatelliteView> _embeds = new();

    // Parameterless path is for the XAML previewer only; the running app resolves the DI ctor.
    public MainWindow() : this(new MainViewModel(), new SatelliteHost(NullLoggerFactory.Instance), NullLoggerFactory.Instance) { }

    public MainWindow(MainViewModel vm, SatelliteHost satellite, ILoggerFactory loggerFactory)
    {
        _vm = vm;
        _satellite = satellite;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<MainWindow>();

        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.RetryRequested += () => _ = StartSatelliteAsync();
        _vm.AddPluginRequested += () => _ = PickPluginAsync();
        UpdateHeader();

        _ = StartSatelliteAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedSection):
                UpdateHeader();
                UpdateEmbed();
                break;
            case nameof(MainViewModel.SelectedPlugin):
            case nameof(MainViewModel.ConfigReady):
            case nameof(MainViewModel.PluginsMode):
                UpdateEmbed();
                break;
        }
    }

    // Show the selected plugin's own configuration window in the bay — but only on the
    // Plugins page in Configure mode. The embedded HWND paints over Avalonia content, so we
    // detach it whenever the Manage rack or another section needs that space.
    private void UpdateEmbed()
    {
        var p = _vm.SelectedPlugin;
        var canEmbed = _vm.SelectedSection == "Plugins" && _vm.IsConfigure && _vm.ConfigReady;
        EmbedSlot.Child = canEmbed && p is { Hwnd: var h } && h != IntPtr.Zero
            ? GetEmbed(h)
            : null;
    }

    // Tapping a channel in the rail patches that plugin into the bay (leaving Manage).
    private void OnRailTapped(object? sender, TappedEventArgs e) => _vm.PluginsMode = "Configure";

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
            _vm.AddPlugin(f.Path.LocalPath);
    }

    private EmbeddedSatelliteView GetEmbed(IntPtr hwnd)
    {
        if (!_embeds.TryGetValue(hwnd, out var view))
            _embeds[hwnd] = view = new EmbeddedSatelliteView(hwnd, _loggerFactory.CreateLogger<EmbeddedSatelliteView>());
        return view;
    }

    // The eyebrow shows the raw section name; the title/subtitle are section-specific copy.
    private void UpdateHeader()
    {
        var (title, subtitle) = _vm.SelectedSection switch
        {
            "Dashboard" => ("Dashboard",
                "The state of the host at a glance — runtime, satellite, and the plugins it carries."),
            "Plugins" => ("Plugin Setup",
                "Load the legacy ecosystem unmodified, then configure each plugin's native tabs in place."),
            "Overlays" => ("Overlays",
                "Web overlays rendered for the browser layer — DPS meters, timelines, and raid tools."),
            "Settings" => ("Settings",
                "How the host launches and presents the two-process stack."),
            _ => ("", "")
        };
        HeaderTitle.Text = title;
        HeaderSubtitle.Text = subtitle;
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

            UpdateEmbed();
        }
        catch (Exception ex)
        {
            _vm.SetOffline(ex.Message);
            _log.LogError(LogEvents.SatelliteLaunchFailed, ex, "Satellite launch failed");
        }
    }

    // ---- custom window chrome ----
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
                ToggleMaximize();
            else
                BeginMoveDrag(e);
        }
    }

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
