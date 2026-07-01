using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fct.Abstractions;
using Fct.App.Hosting;

namespace Fct.App.ViewModels;

public enum HostState { Starting, Online, Offline }

// The shell coordinator: owns the two plugin rosters (legacy · satellite, modern · host), the
// satellite/host connection state, the notification centre, and top-level navigation. Page view
// models read shared state back through this instance.
public sealed partial class MainViewModel : ObservableObject
{
    // Legacy plugins are reconciled from the satellite's reported windows; modern plugins come from
    // the net10 plugin registry. Kept apart because they are two different runtimes.
    public ObservableCollection<PluginViewModel> LegacyPlugins { get; } = new();
    public ObservableCollection<PluginViewModel> ModernPlugins { get; } = new();

    public NotificationCenterViewModel Notifications { get; }

    public OverviewViewModel OverviewPage { get; }
    public PluginsViewModel PluginsPage { get; }
    public EncountersViewModel EncountersPage { get; }
    public SettingsViewModel SettingsPage { get; }

    private readonly Dictionary<Section, PageViewModel> _pages;
    private readonly INotificationHub? _hub;

    // Raised when the user asks to (re)launch or add a plugin; the window owns those OS interactions.
    public event Action? SatelliteRestartRequested;
    public event Action? AddPluginRequested;

    // ---- design-time ctor: seed a representative roster for the previewer ----
    public MainViewModel()
    {
        Notifications = new NotificationCenterViewModel();
        LegacyPlugins.Add(DemoLegacy("ffxiv", PluginStatus.Live));
        LegacyPlugins.Add(DemoLegacy("overlay", PluginStatus.Running));
        LegacyPlugins.Add(DemoLegacy("triggernometry", PluginStatus.Running));
        ModernPlugins.Add(new PluginViewModel
        {
            Name = "Fct.SamplePlugin", Role = "Modern plugin", Version = "0.1.0", Kind = PluginKind.Native,
            ContractVersion = "1.0", Description = "Reference modern plugin for the app.",
            Status = PluginStatus.Loaded,
        });

        OverviewPage = new OverviewViewModel(this);
        PluginsPage = new PluginsViewModel(this);
        EncountersPage = new EncountersViewModel(this, encounters: null);
        SettingsPage = new SettingsViewModel(this, store: null);
        _pages = BuildPages();
        _currentPage = OverviewPage;

        _selectedPlugin = LegacyPlugins[0];
        _selectedPlugin.IsSelected = true;
        Host = HostState.Online;
        SatelliteState = "Running";
        SatelliteSummary = "Classic plugin engine running · 3 plugins.";
    }

    // ---- runtime ctor: bind the real host services ----
    public MainViewModel(IPluginRegistry registry, IEncounterService encounters,
        INotificationHub notifications, UiSettingsStore settings)
    {
        _hub = notifications;
        Notifications = new NotificationCenterViewModel(notifications);

        foreach (var info in registry.LoadedPlugins)
            ModernPlugins.Add(new PluginViewModel
            {
                Name = info.Id,
                Role = "Modern plugin",
                Version = info.Version,
                Kind = PluginKind.Native,
                ContractVersion = info.ContractVersion,
                Description = "Runs inside the app in its own sandbox.",
                Status = PluginStatus.Loaded,
            });

        OverviewPage = new OverviewViewModel(this);
        PluginsPage = new PluginsViewModel(this);
        EncountersPage = new EncountersViewModel(this, encounters);
        SettingsPage = new SettingsViewModel(this, settings);
        _pages = BuildPages();
        _currentPage = OverviewPage;

        SetStarting();
    }

    private Dictionary<Section, PageViewModel> BuildPages() => new()
    {
        [Section.Overview] = OverviewPage,
        [Section.Plugins] = PluginsPage,
        [Section.Encounters] = EncountersPage,
        [Section.Settings] = SettingsPage,
    };

    // ---- navigation ----
    [RelayCommand]
    private void SelectPage(object? param)
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

    private PageViewModel _currentPage;
    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    // ---- shell actions (owned by the window) ----
    [RelayCommand] private void RestartSatellite() => SatelliteRestartRequested?.Invoke();
    [RelayCommand] private void AddPlugin() => AddPluginRequested?.Invoke();

    // ---- shared selection ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PluginViewModel? _selectedPlugin;

    public bool HasSelection => SelectedPlugin is not null;

    partial void OnSelectedPluginChanged(PluginViewModel? oldValue, PluginViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    // ---- satellite / host / game connection state ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsOffline), nameof(GameLive),
        nameof(GameStateLabel))]
    private HostState _host = HostState.Starting;

    public bool IsOnline => Host == HostState.Online;
    public bool IsOffline => Host == HostState.Offline;

    [ObservableProperty]
    private string _satelliteSummary = "Starting the classic plugin engine…";

    [ObservableProperty]
    private string _satelliteState = "Starting";

    [ObservableProperty]
    private bool _configReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // The app is running whenever this view model exists.
    public string HostStateLabel => "Running";

    // The game is "Live" when the classic engine is up AND the parser is streaming (reads Live).
    public bool GameLive => IsOnline && LegacyPlugins.Any(p => p.Key == "ffxiv" && p.IsLive);
    public string GameStateLabel =>
        !IsOnline ? "Not connected"
        : GameLive ? "Live"
        : "Waiting for combat";

    // ---- roster-derived figures ----
    public int LegacyLoadedCount =>
        LegacyPlugins.Count(p => p.Status is PluginStatus.Live or PluginStatus.Running or PluginStatus.Loaded);
    public int ModernLoadedCount => ModernPlugins.Count;
    public int TotalLoadedCount => LegacyLoadedCount + ModernLoadedCount;
    public int FaultCount => LegacyPlugins.Count(p => p.Status is PluginStatus.Error or PluginStatus.NotLoaded);

    public string LoadedSummary =>
        $"{TotalLoadedCount} loaded · {LegacyLoadedCount} classic · {ModernLoadedCount} modern";

    public bool HasModernPlugins => ModernPlugins.Count > 0;
    public bool HasLegacyPlugins => LegacyPlugins.Count > 0;

    public void SetStarting()
    {
        Host = HostState.Starting;
        SatelliteState = "Starting…";
        SatelliteSummary = "Starting the classic plugin engine…";
        ConfigReady = false;
        ErrorMessage = null;
        RaiseRosterChanged();
    }

    // Rebuild the classic roster from what the engine actually loaded; the data source (ffxiv)
    // reads Live, other reported windows Running, and anything without a window Not loaded.
    internal void SetOnline(IReadOnlyList<SatellitePlugin> plugins)
    {
        Host = HostState.Online;
        ErrorMessage = null;
        SatelliteState = "Running";
        SatelliteSummary = plugins.Count == 1
            ? "Classic plugin engine running · 1 plugin."
            : $"Classic plugin engine running · {plugins.Count} plugins.";

        LegacyPlugins.Clear();
        foreach (var p in plugins)
        {
            var meta = LegacyPluginCatalog.For(p.Key, p.Title);
            var hosted = p.Hwnd != IntPtr.Zero;
            LegacyPlugins.Add(new PluginViewModel
            {
                Name = meta.Name,
                Role = meta.Role,
                Description = meta.Description,
                Version = "—",
                Kind = PluginKind.Legacy,
                Key = p.Key,
                Hwnd = p.Hwnd,
                SatelliteStatusText = p.Status,
                HasNativeConfig = hosted,
                Status = hosted ? (p.Key == "ffxiv" ? PluginStatus.Live : PluginStatus.Running)
                                : PluginStatus.NotLoaded,
            });
        }

        ConfigReady = plugins.Any(p => p.Hwnd != IntPtr.Zero);
        if (SelectedPlugin is null || !LegacyPlugins.Contains(SelectedPlugin))
            SelectedPlugin = LegacyPlugins.FirstOrDefault() ?? ModernPlugins.FirstOrDefault();
        RaiseRosterChanged();

        _hub?.Publish(NotificationSeverity.Success, "Classic engine", "Classic engine running",
            plugins.Count == 1 ? "1 classic plugin ready." : $"{plugins.Count} classic plugins ready.");
    }

    public void SetOffline(string error)
    {
        Host = HostState.Offline;
        ConfigReady = false;
        SatelliteState = "Stopped";
        SatelliteSummary = "Classic plugin engine stopped.";
        ErrorMessage = error;
        foreach (var p in LegacyPlugins)
        {
            p.Status = PluginStatus.Unavailable;
            p.HasNativeConfig = false;
            p.Hwnd = IntPtr.Zero;
        }
        RaiseRosterChanged();

        _hub?.Publish(NotificationSeverity.Error, "Classic engine", "Couldn't start the classic engine", error);
    }

    // The classic engine is intentionally not running (auto-launch disabled). Neutral — no error.
    public void SetIdle()
    {
        Host = HostState.Offline;
        ConfigReady = false;
        SatelliteState = "Not started";
        SatelliteSummary = "Classic plugin engine not started — start it from the Plugins page.";
        ErrorMessage = null;
        LegacyPlugins.Clear();
        RaiseRosterChanged();
    }

    internal void RaiseRosterChanged()
    {
        OnPropertyChanged(nameof(LegacyLoadedCount));
        OnPropertyChanged(nameof(ModernLoadedCount));
        OnPropertyChanged(nameof(TotalLoadedCount));
        OnPropertyChanged(nameof(FaultCount));
        OnPropertyChanged(nameof(LoadedSummary));
        OnPropertyChanged(nameof(HasLegacyPlugins));
        OnPropertyChanged(nameof(HasModernPlugins));
        OnPropertyChanged(nameof(GameLive));
        OnPropertyChanged(nameof(GameStateLabel));
    }

    private static PluginViewModel DemoLegacy(string key, PluginStatus status)
    {
        var meta = LegacyPluginCatalog.For(key, key);
        return new PluginViewModel
        {
            Name = meta.Name, Role = meta.Role, Description = meta.Description, Version = "—",
            Kind = PluginKind.Legacy, Key = key, HasNativeConfig = true, Status = status,
        };
    }
}
