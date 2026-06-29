using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Fct.App.ViewModels;

public enum HostState { Starting, Online, Offline }

// The shell coordinator: owns the shared plugin roster + satellite state, and the top-level
// navigation (CurrentPage). Page-specific behaviour lives in the per-page view models, which
// read shared state back through this instance.
public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<PluginViewModel> Plugins { get; } = new();

    public DashboardViewModel DashboardPage { get; }
    public PluginsViewModel PluginsPage { get; }
    public OverlaysViewModel OverlaysPage { get; }
    public SettingsViewModel SettingsPage { get; }

    private readonly Dictionary<Section, PageViewModel> _pages;

    public MainViewModel()
    {
        Plugins.Add(new PluginViewModel
        {
            Name = "FFXIV_ACT_Plugin",
            Role = "Network parser · data source",
            Version = "2.7.5.0",
            Kind = PluginKind.Legacy,
            Key = "ffxiv",
            Description =
                "Reads the game's network stream and turns it into combat actions. " +
                "Every other plugin builds on the data it produces.",
        });
        Plugins.Add(new PluginViewModel
        {
            Name = "OverlayPlugin",
            Role = "Overlays · cactbot bridge",
            Version = "0.16.5",
            Kind = PluginKind.Legacy,
            Key = "overlay",
            Description =
                "Serves overlays over WebSocket and bridges cactbot — DPS meters, " +
                "timelines, and raid tools rendered in the browser layer.",
        });
        Plugins.Add(new PluginViewModel
        {
            Name = "Triggernometry",
            Role = "Triggers · timelines · TTS",
            Version = "1.9.5.0",
            Kind = PluginKind.Legacy,
            Key = "triggernometry",
            Description =
                "Matches log lines to fire alerts, timers, and text-to-speech callouts " +
                "from your own trigger packs.",
        });
        Plugins.Add(new PluginViewModel
        {
            Name = "Discord Triggers",
            Role = "Encounter → Discord",
            Version = "2.0.0",
            Kind = PluginKind.Legacy,
            Key = "discord",
            Description =
                "Posts encounter results and trigger events to a Discord webhook when a " +
                "fight ends.",
        });
        Plugins.Add(new PluginViewModel
        {
            Name = "Native Parser",
            Role = "Native pipeline · preview",
            Version = "0.1-preview",
            Kind = PluginKind.Native,
            Key = "native",
            IsActive = false,
            BaseStatus = PluginStatus.Preview,
            Description =
                "Clean-room packet parser built for the .NET 10 host. Opt-in and " +
                "hot-swappable — no host restart on patch day. Arriving in a later slice.",
        });

        _selectedPlugin = Plugins[0];
        _selectedPlugin.IsSelected = true;

        DashboardPage = new DashboardViewModel(this);
        PluginsPage = new PluginsViewModel(this);
        OverlaysPage = new OverlaysViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        _pages = new Dictionary<Section, PageViewModel>
        {
            [Section.Dashboard] = DashboardPage,
            [Section.Plugins] = PluginsPage,
            [Section.Overlays] = OverlaysPage,
            [Section.Settings] = SettingsPage,
        };
        _currentPage = PluginsPage;

        SelectPageCommand = new RelayCommand(Navigate);

        SetStarting();
    }

    // ---- navigation ----
    public RelayCommand SelectPageCommand { get; }

    private PageViewModel _currentPage;
    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetField(ref _currentPage, value);
    }

    // Accepts a Section (typed) or its name (from a XAML CommandParameter string); the enum
    // is the source of truth — the string is parsed once here, at the boundary.
    private void Navigate(object? param)
    {
        Section? target = param switch
        {
            Section s => s,
            string name when Enum.TryParse<Section>(name, out var s) => s,
            _ => null,
        };
        if (target is { } section && _pages.TryGetValue(section, out var page))
            CurrentPage = page;
    }

    // ---- shared selection ----
    private PluginViewModel? _selectedPlugin;
    public PluginViewModel? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            var old = _selectedPlugin;
            if (!SetField(ref _selectedPlugin, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
        }
    }

    // ---- satellite / host state ----
    private HostState _host = HostState.Starting;
    public HostState Host
    {
        get => _host;
        set { if (SetField(ref _host, value)) { Raise(nameof(IsOnline)); Raise(nameof(IsOffline)); } }
    }

    public bool IsOnline => Host == HostState.Online;
    public bool IsOffline => Host == HostState.Offline;

    private string _satelliteSummary = "Launching .NET Framework 4.8 satellite…";
    public string SatelliteSummary
    {
        get => _satelliteSummary;
        set => SetField(ref _satelliteSummary, value);
    }

    private string _satelliteState = "Starting";
    public string SatelliteState
    {
        get => _satelliteState;
        set => SetField(ref _satelliteState, value);
    }

    private bool _configReady;
    public bool ConfigReady
    {
        get => _configReady;
        set => SetField(ref _configReady, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (SetField(ref _errorMessage, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public int LoadedCount =>
        Plugins.Count(p => p.Status is PluginStatus.Live or PluginStatus.Running or PluginStatus.Loaded);

    public string LoadedSummary => $"{LoadedCount} of {Plugins.Count(p => p.Kind == PluginKind.Legacy)} legacy plugins";

    public void SetStarting()
    {
        Host = HostState.Starting;
        SatelliteState = "Starting";
        SatelliteSummary = "Launching .NET Framework 4.8 satellite…";
        ConfigReady = false;
        ErrorMessage = null;
        foreach (var p in Plugins)
            if (p.BaseStatus != PluginStatus.Preview) p.BaseStatus = PluginStatus.Loading;
        RaiseRosterChanged();
    }

    // Reconcile the roster with what the satellite actually loaded: each reported plugin gets
    // its embed window + a live status; the data source (ffxiv) reads as Live, the rest Running.
    // Legacy plugins this build doesn't host yet fall back to "Not loaded".
    internal void SetOnline(string handshake, int pid, IReadOnlyList<SatellitePlugin> plugins)
    {
        Host = HostState.Online;
        ErrorMessage = null;
        SatelliteState = "Online";
        SatelliteSummary = $"pid {pid} · {Clr(handshake)} · x64";

        var byKey = plugins.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var vm in Plugins)
        {
            if (vm.BaseStatus == PluginStatus.Preview) continue;

            if (byKey.TryGetValue(vm.Key, out var reported) && reported.Hwnd != IntPtr.Zero)
            {
                vm.Hwnd = reported.Hwnd;
                vm.HasNativeConfig = true;
                vm.BaseStatus = vm.Key == "ffxiv" ? PluginStatus.Live : PluginStatus.Running;
            }
            else
            {
                vm.Hwnd = IntPtr.Zero;
                vm.HasNativeConfig = false;
                vm.BaseStatus = PluginStatus.NotLoaded;
            }
        }

        ConfigReady = plugins.Any(p => p.Hwnd != IntPtr.Zero);
        RaiseRosterChanged();
    }

    public void SetOffline(string error)
    {
        Host = HostState.Offline;
        ConfigReady = false;
        SatelliteState = "Offline";
        SatelliteSummary = "Satellite not running";
        ErrorMessage = error;
        foreach (var p in Plugins)
            if (p.BaseStatus != PluginStatus.Preview)
            {
                p.BaseStatus = PluginStatus.Unavailable;
                p.HasNativeConfig = false;
                p.Hwnd = IntPtr.Zero;
            }
        RaiseRosterChanged();
    }

    // Re-raise the roster-derived figures after the collection or a plugin's status changes.
    internal void RaiseRosterChanged()
    {
        Raise(nameof(LoadedCount));
        Raise(nameof(LoadedSummary));
    }

    // Pull the CLR version out of "READY pid=.. x64=True clr=.." for the status chip.
    private static string Clr(string handshake)
    {
        const string key = "clr=";
        var i = handshake.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "CLR 4.x";
        var rest = handshake[(i + key.Length)..].Trim();
        var end = rest.IndexOf(' ');
        return "CLR " + (end < 0 ? rest : rest[..end]);
    }
}
