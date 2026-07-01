using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fct.App.ViewModels;

public enum PluginStatus
{
    Loading,      // satellite still coming up
    Live,         // running AND receiving the game stream (the warm accent)
    Running,      // loaded and active
    Loaded,       // present, idle (native plugins)
    NotLoaded,    // reported by the satellite but with no window / not hosted
    Unavailable,  // host/satellite down — cannot load
    Error
}

public enum PluginKind { Legacy, Native }

// One row in the plugin roster. Legacy rows are reconciled from the satellite's reported windows;
// native rows come from the net10 plugin registry. Status is observable so the orb, badge, and
// label stay in sync.
public sealed partial class PluginViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required PluginKind Kind { get; init; }

    // Legacy: matches the satellite's PLUGIN key; links this row to its embeddable window.
    public string Key { get; init; } = "";

    // Native: manifest identity shown in the details panel.
    public string ContractVersion { get; init; } = "";
    public string Capabilities { get; init; } = "";

    // The satellite window to embed when this legacy plugin is selected (zero until reported).
    public IntPtr Hwnd { get; set; }

    // The raw status text the satellite reported for a legacy plugin (e.g. "load failed").
    public string? SatelliteStatusText { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLegacyPlaceholder))]
    private bool _hasNativeConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(StatusLabel),
        nameof(ConfigPlaceholderTitle), nameof(ConfigPlaceholderBody))]
    private PluginStatus _status = PluginStatus.Loading;

    public bool IsLegacy => Kind == PluginKind.Legacy;
    public bool IsNative => Kind == PluginKind.Native;
    public bool IsLive => Status == PluginStatus.Live;

    // Which panel the config bay shows for this plugin.
    public bool ShowNativeDetails => IsNative;
    public bool ShowLegacyPlaceholder => IsLegacy && !HasNativeConfig;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(SatelliteStatusText);

    // Runtime badge, e.g. "net48 · satellite" / "net10 · host".
    public string Runtime => Kind == PluginKind.Legacy ? "net48 · satellite" : "net10 · host";

    public string StatusLabel => Status switch
    {
        PluginStatus.Loading => "Starting…",
        PluginStatus.Live => "Live",
        PluginStatus.Running => "Running",
        PluginStatus.Loaded => "Loaded",
        PluginStatus.NotLoaded => "Not loaded",
        PluginStatus.Unavailable => "Unavailable",
        _ => "Error"
    };

    // Shown in the config bay when there's nothing embeddable for this plugin.
    public string ConfigPlaceholderTitle =>
        Kind == PluginKind.Native ? "Configured in the plugin"
        : Status == PluginStatus.NotLoaded ? "No configuration to show"
        : "Configuration unavailable";

    public string ConfigPlaceholderBody =>
        Kind == PluginKind.Native
            ? "Native plugins run in the .NET 10 host and ship their own typed settings. There's no legacy tab to embed."
        : Status == PluginStatus.NotLoaded
            ? "The satellite reported this plugin but exposed no configuration window."
            : "The satellite isn't online, so this plugin's configuration can't be shown.";
}
