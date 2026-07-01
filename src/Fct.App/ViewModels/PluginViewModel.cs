using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fct.App.ViewModels;

public enum PluginStatus
{
    Loading,      // host still bringing the satellite up
    Live,         // running AND receiving the game stream (the warm accent)
    Running,      // loaded and active
    Loaded,       // present, idle
    Disabled,     // toggled off by the user
    Preview,      // not yet shipping (native parser)
    NotLoaded,    // a legacy plugin this build doesn't host yet
    Unavailable,  // host/satellite down — cannot load
    Error
}

public enum PluginKind { Legacy, Native }

// One row in the plugin roster. Status is derived so the orb, pill, and label all stay
// in sync with whether the host is up and whether the user has the plugin toggled on.
public sealed partial class PluginViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required PluginKind Kind { get; init; }

    // Matches the satellite's PLUGIN key; links a roster row to its embeddable window.
    public string Key { get; init; } = "";

    // Set when a plugin is added by browsing for a DLL; the satellite hosts it on next launch.
    public string? FilePath { get; init; }

    // The satellite window to embed when this plugin is selected (zero until reported).
    public IntPtr Hwnd { get; set; }

    // Drives the channel rail's active styling; the MainViewModel keeps exactly one set.
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasNativeConfig;

    // Runtime badge, e.g. "net48 · satellite" / "net10 · host".
    public string Runtime => Kind == PluginKind.Legacy ? "net48 · satellite" : "net10 · host";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(IsLive), nameof(StatusLabel),
        nameof(ConfigPlaceholderTitle), nameof(ConfigPlaceholderBody))]
    private PluginStatus _baseStatus = PluginStatus.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(IsLive), nameof(StatusLabel),
        nameof(ConfigPlaceholderTitle), nameof(ConfigPlaceholderBody))]
    private bool _isActive = true;

    // Preview plugins ignore the toggle (nothing to start yet); everything else collapses
    // to Disabled when switched off.
    public PluginStatus Status =>
        BaseStatus == PluginStatus.Preview ? PluginStatus.Preview
        : !IsActive ? PluginStatus.Disabled
        : BaseStatus;

    public bool IsLive => Status == PluginStatus.Live;
    public bool CanToggle => BaseStatus != PluginStatus.Preview;

    // Shown in the config pane when there are no embeddable tabs for this plugin.
    public string ConfigPlaceholderTitle =>
        Kind == PluginKind.Native ? "No native tabs yet"
        : Status == PluginStatus.NotLoaded ? "Not hosted in this build"
        : "Configuration unavailable";

    public string ConfigPlaceholderBody =>
        Kind == PluginKind.Native
            ? "This plugin runs in the .NET 10 host and will ship its own typed configuration. " +
              "Nothing to embed from the legacy satellite."
        : Status == PluginStatus.NotLoaded
            ? "The satellite doesn't load this plugin yet, so it has no configuration tabs to " +
              "embed. It will appear here once the satellite hosts it."
            : "The host isn't online, so this plugin's configuration can't be shown.";

    public string StatusLabel => Status switch
    {
        PluginStatus.Loading => "Starting…",
        PluginStatus.Live => "Live",
        PluginStatus.Running => "Running",
        PluginStatus.Loaded => "Loaded",
        PluginStatus.Disabled => "Disabled",
        PluginStatus.Preview => "Preview",
        PluginStatus.NotLoaded => "Not loaded",
        PluginStatus.Unavailable => "Unavailable",
        _ => "Error"
    };
}
