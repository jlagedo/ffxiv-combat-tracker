using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.App.Lang;
using Fct.Host;
using Fct.Host.Hosting;
using Fct.Host.Plugins.Ui;

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

    // The single roster the UI binds to: a live mirror of the two feeds, legacy rows first then
    // modern. The feeds stay the sources of truth (satellite events / registry); WireMergedRoster
    // reflects their changes here so one list shows every plugin, distinguished only by a per-row tag.
    public ObservableCollection<PluginViewModel> Plugins { get; } = new();

    // Transient corner controls contributed by native plugins via IUiHost.AddCornerControl.
    public ObservableCollection<CornerControlViewModel> CornerControls { get; } = new();

    public NotificationCenterViewModel Notifications { get; }

    public OverviewViewModel OverviewPage { get; }
    public PluginsViewModel PluginsPage { get; }
    public EncountersViewModel EncountersPage { get; }
    public ConsoleViewModel ConsolePage { get; }
    public SettingsViewModel SettingsPage { get; }

    private readonly Dictionary<Section, PageViewModel> _pages;
    private readonly INotificationHub? _hub;
    private readonly IPluginRegistry? _registry;
    private readonly IUiDispatcher? _dispatcher;

    // Raised when the user asks to (re)launch or add a plugin; the window owns those OS interactions.
    public event Action? SatelliteRestartRequested;
    public event Action? AddPluginRequested;

    // Raised when the user asks to unload/uninstall a plugin row; the window runs the teardown.
    public event Action<PluginViewModel>? UnloadPluginRequested;

    // Raised when the user asks to re-locate a missing legacy plugin; the window owns the file picker.
    public event Action<PluginViewModel>? LocatePluginRequested;

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
        WireMergedRoster();

        OverviewPage = new OverviewViewModel(this);
        PluginsPage = new PluginsViewModel(this);
        EncountersPage = new EncountersViewModel(this, encounters: null);
        ConsolePage = new ConsoleViewModel(this, ConsoleViewModel.CreateDesignStream());
        SettingsPage = new SettingsViewModel(this, store: null);
        _pages = BuildPages();
        _currentPage = OverviewPage;

        _selectedPlugin = LegacyPlugins[0];
        _selectedPlugin.IsSelected = true;
        Host = HostState.Online;
        SatelliteState = "Running";
        SatelliteStatusKind = PluginStatus.Running;
        SatelliteSummary = "Classic plugin engine running · 3 plugins.";
    }

    // ---- runtime ctor: bind the real host services ----
    public MainViewModel(IPluginRegistry registry, IEncounterService encounters,
        INotificationHub notifications, UiSettingsStore settings, Fct.App.Logging.ILogStream logStream,
        PluginUiCoordinator? uiCoordinator = null)
    {
        _hub = notifications;
        _registry = registry;
        _dispatcher = uiCoordinator?.Dispatcher;
        Notifications = new NotificationCenterViewModel(notifications);

        foreach (var info in registry.LoadedPlugins)
            ModernPlugins.Add(BuildModernRow(info));
        WireMergedRoster();

        OverviewPage = new OverviewViewModel(this);
        PluginsPage = new PluginsViewModel(this);
        EncountersPage = new EncountersViewModel(this, encounters);
        ConsolePage = new ConsoleViewModel(this, logStream);
        SettingsPage = new SettingsViewModel(this, settings);
        _pages = BuildPages();
        _currentPage = OverviewPage;

        if (uiCoordinator is not null)
        {
            uiCoordinator.SettingsPageAdded += OnPluginSettingsPageAdded;
            uiCoordinator.SettingsPageRemoved += OnPluginSettingsPageRemoved;
            uiCoordinator.PageRevealRequested += OnPluginPageRevealRequested;
            uiCoordinator.CornerControlAdded += OnPluginCornerControlAdded;
            uiCoordinator.CornerControlRemoved += OnPluginCornerControlRemoved;
        }

        // Reconcile the modern roster live as plugins hot-load/unload (the registry is no longer a
        // one-time snapshot).
        if (registry is RegistryService rs)
            rs.RosterChanged += OnRosterChanged;

        SetStarting();
    }

    private PluginViewModel BuildModernRow(PluginInfo info) => new()
    {
        Name = info.Name ?? info.Id,
        Role = Resources.Plugins_ModernPluginRole,
        Version = info.Version,
        Kind = PluginKind.Native,
        Key = info.Id,
        ContractVersion = info.ContractVersion,
        Description = info.Description ?? Resources.Plugins_ModernPluginDescription,
        Author = info.Author ?? "",
        Status = PluginStatus.Loaded,
    };

    // The registry roster changed (a native/shim plugin loaded or unloaded); add/drop rows by id.
    private void OnRosterChanged()
    {
        if (_registry is null) return;
        PostToUi(() =>
        {
            var infos = _registry.LoadedPlugins;
            for (int i = ModernPlugins.Count - 1; i >= 0; i--)
                if (!infos.Any(x => x.Id == ModernPlugins[i].Key))
                    ModernPlugins.RemoveAt(i);
            foreach (var info in infos)
                if (ModernPlugins.All(p => p.Key != info.Id))
                    ModernPlugins.Add(BuildModernRow(info));
            RaiseRosterChanged();
        });
    }

    private void PostToUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) action();
        else _dispatcher.Post(action);
    }

    // Mirror both feeds into the single Plugins collection (legacy rows first, then modern) and keep
    // it live. Incremental — never a clear-and-rebuild — so the bound SelectedPlugin is never
    // transiently dropped by the ListBox mid-reconcile.
    private void WireMergedRoster()
    {
        Plugins.Clear();
        foreach (var p in LegacyPlugins) Plugins.Add(p);
        foreach (var p in ModernPlugins) Plugins.Add(p);

        LegacyPlugins.CollectionChanged += (_, e) => MirrorChange(e, legacy: true);
        ModernPlugins.CollectionChanged += (_, e) => MirrorChange(e, legacy: false);
    }

    // Reflect one feed change into Plugins. Legacy rows occupy the front, so a legacy change maps at
    // the same index; a modern change is offset past the legacy segment (LegacyPlugins.Count already
    // reflects the in-progress change).
    private void MirrorChange(NotifyCollectionChangedEventArgs e, bool legacy)
    {
        int offset = legacy ? 0 : LegacyPlugins.Count;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Plugins.Insert(offset + e.NewStartingIndex, (PluginViewModel)e.NewItems![0]!);
                break;
            case NotifyCollectionChangedAction.Remove:
                Plugins.RemoveAt(offset + e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace:
                Plugins[offset + e.OldStartingIndex] = (PluginViewModel)e.NewItems![0]!;
                break;
            case NotifyCollectionChangedAction.Reset:
                // Only the legacy feed resets (Clear() in SetOnline/SetIdle). Drop its whole segment;
                // the modern segment is the tail of length ModernPlugins.Count.
                for (int i = Plugins.Count - ModernPlugins.Count; i > 0; i--)
                    Plugins.RemoveAt(0);
                break;
        }
        OnPropertyChanged(nameof(HasPlugins));
    }

    // ---- native plugin UI contributions (work item 9) ----
    private void OnPluginSettingsPageAdded(string pluginId, UiSurface page)
    {
        var row = ModernPlugins.FirstOrDefault(p => p.Key == pluginId);
        if (row is not null && row.SettingsSurface is null)
            row.SettingsSurface = page;
    }

    // A plugin unloaded — drop its contributed settings surface so its ALC isn't pinned by the control.
    private void OnPluginSettingsPageRemoved(string pluginId)
    {
        var row = ModernPlugins.FirstOrDefault(p => p.Key == pluginId);
        if (row is not null) row.SettingsSurface = null;
    }

    private void OnPluginPageRevealRequested(string pageId)
    {
        var row = ModernPlugins.FirstOrDefault(p => p.SettingsSurface?.Id == pageId);
        if (row is null) return;
        CurrentPage = PluginsPage;
        SelectedPlugin = row;
    }

    private void OnPluginCornerControlAdded(string pluginId, UiSurface control)
        => CornerControls.Add(new CornerControlViewModel { PluginId = pluginId, Surface = control });

    private void OnPluginCornerControlRemoved(string id)
    {
        var existing = CornerControls.FirstOrDefault(c => c.Surface.Id == id);
        if (existing is not null) CornerControls.Remove(existing);
    }

    private Dictionary<Section, PageViewModel> BuildPages() => new()
    {
        [Section.Overview] = OverviewPage,
        [Section.Plugins] = PluginsPage,
        [Section.Encounters] = EncountersPage,
        [Section.Console] = ConsolePage,
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
    [RelayCommand] private void UnloadPlugin(PluginViewModel? row)
    {
        if (row is not null) UnloadPluginRequested?.Invoke(row);
    }
    [RelayCommand] private void LocatePlugin(PluginViewModel? row)
    {
        if (row is not null) LocatePluginRequested?.Invoke(row);
    }

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

    // ---- satellite / host connection state ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsOffline))]
    private HostState _host = HostState.Starting;

    public bool IsOnline => Host == HostState.Online;
    public bool IsOffline => Host == HostState.Offline;

    [ObservableProperty]
    private string _satelliteSummary = "Starting the classic plugin engine…";

    [ObservableProperty]
    private string _satelliteState = "Starting";

    // Drives the status-to-brush converter for the "Classic engine" chip. Kept separate from the
    // (localized, free-form) SatelliteState text so the colour never depends on matching English
    // literals — set alongside SatelliteState at every transition below.
    [ObservableProperty]
    private PluginStatus _satelliteStatusKind = PluginStatus.Loading;

    [ObservableProperty]
    private bool _configReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // The app is running whenever this view model exists.
    public string HostStateLabel => Resources.Status_Running;
    public PluginStatus HostStatusKind => PluginStatus.Running;

    // ---- roster-derived figures ----
    public int LegacyLoadedCount =>
        LegacyPlugins.Count(p => p.Status is PluginStatus.Live or PluginStatus.Running or PluginStatus.Loaded);
    public int ModernLoadedCount => ModernPlugins.Count;
    public int TotalLoadedCount => LegacyLoadedCount + ModernLoadedCount;
    public int FaultCount => LegacyPlugins.Count(p =>
        p.Status is PluginStatus.Error or PluginStatus.NotLoaded or PluginStatus.Missing);

    public string LoadedSummary =>
        string.Format(Resources.Overview_LoadedSummaryFormat, TotalLoadedCount, LegacyLoadedCount, ModernLoadedCount);

    public bool HasModernPlugins => ModernPlugins.Count > 0;
    public bool HasLegacyPlugins => LegacyPlugins.Count > 0;
    public bool HasPlugins => Plugins.Count > 0;

    public void SetStarting()
    {
        Host = HostState.Starting;
        SatelliteState = Resources.Status_Starting;
        SatelliteStatusKind = PluginStatus.Loading;
        SatelliteSummary = Resources.Status_StartingEngineSummary;
        ConfigReady = false;
        ErrorMessage = null;
        RaiseRosterChanged();
    }

    // One legacy roster row from a satellite-reported plugin. The data source (ffxiv) reads Live,
    // other hosted windows Running, and anything without a window Not loaded. Shared by SetOnline and
    // AddLegacyPlugin so the two paths can't drift (e.g. status kind, embeddable-window flag).
    private static PluginViewModel BuildLegacyRow(SatellitePlugin p)
    {
        var meta = LegacyPluginCatalog.For(p.Key, p.Title);
        var hosted = p.Hwnd != IntPtr.Zero;
        return new PluginViewModel
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
            Status = hosted ? (LegacyPluginCatalog.IsParser(p.Key, p.Title) ? PluginStatus.Live : PluginStatus.Running)
                            : PluginStatus.NotLoaded,
        };
    }

    // Recompute the classic-engine state derived from the roster: whether any plugin exposes an
    // embeddable window (gates the config bay via ConfigReady), and the running-plugin summary. Must
    // run after every roster change while online — plugins announced after SetOnline (install / replay
    // / relink) would otherwise leave ConfigReady false and the config bay stuck on its placeholder.
    private void RefreshLegacyDerived()
    {
        ConfigReady = LegacyPlugins.Any(p => p.Hwnd != IntPtr.Zero);
        if (Host != HostState.Online) return;
        var count = LegacyLoadedCount;
        SatelliteSummary = count == 1
            ? Resources.Status_EngineRunningSummary_One
            : string.Format(Resources.Status_EngineRunningSummary_Many, count);
    }

    // Rebuild the classic roster from what the engine actually loaded.
    internal void SetOnline(IReadOnlyList<SatellitePlugin> plugins)
    {
        Host = HostState.Online;
        ErrorMessage = null;
        SatelliteState = Resources.Status_Running;
        SatelliteStatusKind = PluginStatus.Running;

        LegacyPlugins.Clear();
        foreach (var p in plugins)
            LegacyPlugins.Add(BuildLegacyRow(p));

        RefreshLegacyDerived();
        if (SelectedPlugin is null || !LegacyPlugins.Contains(SelectedPlugin))
            SelectedPlugin = LegacyPlugins.FirstOrDefault() ?? ModernPlugins.FirstOrDefault();
        RaiseRosterChanged();

        // With the multi-satellite topology the roster fills via PluginAnnounced events as each package
        // satellite comes up (plugins is empty here), so only announce a count once we actually have one.
        _hub?.Publish(NotificationSeverity.Success, Resources.Notify_Source_ClassicEngine, Resources.Notify_EngineRunningTitle,
            plugins.Count == 0 ? Resources.Status_Running
            : plugins.Count == 1 ? Resources.Notify_EngineReady_One
            : string.Format(Resources.Notify_EngineReady_Many, plugins.Count));
    }

    // A legacy plugin was loaded live on the satellite after startup (install / restart replay /
    // relink). A stale row under the same key (e.g. its "files missing" placeholder) is replaced.
    internal void AddLegacyPlugin(SatellitePlugin p)
    {
        PostToUi(() =>
        {
            var row = BuildLegacyRow(p);
            var existing = LegacyPlugins.FirstOrDefault(x => x.Key == p.Key);
            if (existing is not null)
            {
                LegacyPlugins[LegacyPlugins.IndexOf(existing)] = row;
                if (ReferenceEquals(SelectedPlugin, existing)) SelectedPlugin = row;
            }
            else
            {
                LegacyPlugins.Add(row);
            }
            RefreshLegacyDerived();
            // SetOnline may have run with an empty roster (the satellite starts bare, then plugins are
            // replayed/installed and announced here); select the first one so the config bay isn't
            // stranded empty until the user clicks.
            if (SelectedPlugin is null) SelectedPlugin = row;
            RaiseRosterChanged();
        });
    }

    // A persisted install-by-reference plugin whose source files are gone: show a row the user can
    // re-locate (satellite never announces it, so it would otherwise silently vanish from the roster).
    internal void AddMissingLegacyPlugin(string key, string title, string sourceDir)
    {
        PostToUi(() =>
        {
            if (LegacyPlugins.Any(x => x.Key == key)) return;
            var meta = LegacyPluginCatalog.For(key, title);
            LegacyPlugins.Add(new PluginViewModel
            {
                Name = meta.Name,
                Role = meta.Role,
                Description = meta.Description,
                Version = "—",
                Kind = PluginKind.Legacy,
                Key = key,
                SourceDir = sourceDir,
                Status = PluginStatus.Missing,
            });
            RaiseRosterChanged();
        });
    }

    // A legacy plugin was unloaded from the satellite (uninstall / satellite ack).
    internal void RemoveLegacyPlugin(string key)
    {
        PostToUi(() =>
        {
            var row = LegacyPlugins.FirstOrDefault(x => x.Key == key);
            if (row is null) return;
            LegacyPlugins.Remove(row);
            if (ReferenceEquals(SelectedPlugin, row))
                SelectedPlugin = LegacyPlugins.FirstOrDefault() ?? (PluginViewModel?)ModernPlugins.FirstOrDefault();
            RefreshLegacyDerived();
            RaiseRosterChanged();
        });
    }

    public void SetOffline(string error)
    {
        Host = HostState.Offline;
        ConfigReady = false;
        SatelliteState = Resources.Status_Stopped;
        SatelliteStatusKind = PluginStatus.Unavailable;
        SatelliteSummary = Resources.Status_EngineStoppedSummary;
        ErrorMessage = error;
        foreach (var p in LegacyPlugins)
        {
            p.Status = PluginStatus.Unavailable;
            p.HasNativeConfig = false;
            p.Hwnd = IntPtr.Zero;
        }
        RaiseRosterChanged();

        _hub?.Publish(NotificationSeverity.Error, Resources.Notify_Source_ClassicEngine, Resources.Notify_EngineStartFailedTitle, error);
    }

    // The classic engine is intentionally not running (auto-launch disabled). Neutral — no error.
    public void SetIdle()
    {
        Host = HostState.Offline;
        ConfigReady = false;
        SatelliteState = Resources.Status_NotStarted;
        SatelliteStatusKind = PluginStatus.Loading;
        SatelliteSummary = Resources.Status_EngineNotStartedSummary;
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
        OnPropertyChanged(nameof(HasPlugins));
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
