using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Fct.App.ViewModels;

namespace Fct.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // One embedded view per plugin window; the config slot shows whichever plugin is selected.
    private readonly Dictionary<IntPtr, EmbeddedSatelliteView> _embeds = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.RetryRequested += () => _ = StartSatelliteAsync();
        UpdateHeader();

        _ = StartSatelliteAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedSection):
                UpdateHeader();
                break;
            case nameof(MainViewModel.SelectedPlugin):
            case nameof(MainViewModel.ConfigReady):
                UpdateEmbed();
                break;
        }
    }

    // Show the selected plugin's own configuration window in the config slot (or nothing).
    private void UpdateEmbed()
    {
        var p = _vm.SelectedPlugin;
        EmbedSlot.Child = _vm.ConfigReady && p is { Hwnd: var h } && h != IntPtr.Zero
            ? GetEmbed(h)
            : null;
    }

    private EmbeddedSatelliteView GetEmbed(IntPtr hwnd)
    {
        if (!_embeds.TryGetValue(hwnd, out var view))
            _embeds[hwnd] = view = new EmbeddedSatelliteView(hwnd);
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
        var logPath = Path.Combine(AppContext.BaseDirectory, "s0-handshake.log");
        try
        {
            var host = new SatelliteHost();
            var result = await host.StartAsync();
            var pid = host.Process?.Id ?? 0;

            _vm.SetOnline(result.Handshake, pid, result.Plugins);
            File.WriteAllText(logPath,
                $"{result.Handshake}   plugins: {result.Plugins.Count}");

            UpdateEmbed();
        }
        catch (Exception ex)
        {
            _vm.SetOffline(ex.Message);
            File.WriteAllText(logPath, "Satellite launch FAILED:\n" + ex);
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

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
