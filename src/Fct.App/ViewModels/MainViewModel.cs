using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Fct.App.ViewModels;

public enum HostState { Starting, Online, Offline }

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<PluginViewModel> Plugins { get; } = new();

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

        SelectSectionCommand = new RelayCommand(p => SelectedSection = p as string ?? SelectedSection);
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());

        ManageCommand = new RelayCommand(() => PluginsMode = "Manage");
        ConfigureCommand = new RelayCommand(() => PluginsMode = "Configure");
        AddPluginCommand = new RelayCommand(() => AddPluginRequested?.Invoke());
        RemovePluginCommand = new RelayCommand(p => RemovePlugin(p as PluginViewModel));
        MoveUpCommand = new RelayCommand(p => Move(p as PluginViewModel, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as PluginViewModel, +1));

        SetStarting();
    }

    public RelayCommand SelectSectionCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand ManageCommand { get; }
    public RelayCommand ConfigureCommand { get; }
    public RelayCommand AddPluginCommand { get; }
    public RelayCommand RemovePluginCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    // Raised when the user asks to relaunch the host after a failed start; the window
    // owns the satellite lifecycle and wires this up.
    public event Action? RetryRequested;

    // Raised when the user clicks "Add plugin"; the window owns the file picker.
    public event Action? AddPluginRequested;

    private string _selectedSection = "Plugins";
    public string SelectedSection
    {
        get => _selectedSection;
        set { if (SetField(ref _selectedSection, value)) Raise(nameof(ShowGenericHeader)); }
    }

    // The shared content header is replaced by the Plugins page's own rail/bay chrome.
    public bool ShowGenericHeader => SelectedSection != "Plugins";

    // The Plugins page has two surfaces in the same space: "Configure" (the embedded
    // plugin tabs) and "Manage" (add / remove / enable / reorder the roster).
    private string _pluginsMode = "Configure";
    public string PluginsMode
    {
        get => _pluginsMode;
        set { if (SetField(ref _pluginsMode, value)) { Raise(nameof(IsConfigure)); Raise(nameof(IsManage)); } }
    }

    public bool IsConfigure => _pluginsMode == "Configure";
    public bool IsManage => _pluginsMode == "Manage";

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

    // Reorder the roster in place (the channel rail and rack both reflect the order).
    private void Move(PluginViewModel? p, int dir)
    {
        if (p is null) return;
        var i = Plugins.IndexOf(p);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= Plugins.Count) return;
        Plugins.Move(i, j);
    }

    // Remove an added/loaded plugin; keep a sensible selection on whatever remains.
    private void RemovePlugin(PluginViewModel? p)
    {
        if (p is null) return;
        var i = Plugins.IndexOf(p);
        if (i < 0) return;
        Plugins.Remove(p);
        if (ReferenceEquals(SelectedPlugin, p))
            SelectedPlugin = Plugins.Count > 0 ? Plugins[Math.Min(i, Plugins.Count - 1)] : null;
        Refresh();
    }

    // Add a plugin by file. The satellite owns plugin loading, so a freshly added plugin
    // reads as "Not loaded" until the host relaunches and hosts it.
    public void AddPlugin(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var vm = new PluginViewModel
        {
            Name = name,
            Role = "Added · hosted on next launch",
            Version = "—",
            Kind = PluginKind.Legacy,
            FilePath = path,
            BaseStatus = PluginStatus.NotLoaded,
            Description =
                $"Added from {path}. The .NET 4.8 satellite loads plugins when the host starts, " +
                "so this plugin's configuration tabs appear here after the next relaunch.",
        };
        Plugins.Add(vm);
        SelectedPlugin = vm;
        PluginsMode = "Manage";
        Refresh();
    }

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
        Refresh();
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
        Refresh();
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
        Refresh();
    }

    private void Refresh()
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
