using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Fct.Abstractions.UI;
using Fct.App.Lang;

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
    public string Author { get; init; } = "";
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

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

    // The Avalonia settings page this plugin contributed via IUiContributor.RegisterUi (work item 9),
    // if any. Set once RegisterUi runs; null for plugins that never call AddSettingsPage.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModernUi), nameof(ShowNativeDetails))]
    private UiSurface? _settingsSurface;

    public bool IsLegacy => Kind == PluginKind.Legacy;
    public bool IsNative => Kind == PluginKind.Native;
    public bool IsLive => Status == PluginStatus.Live;
    public bool HasModernUi => SettingsSurface is not null;

    // Which panel the config bay shows for this plugin. A contributed settings page takes priority
    // over the read-only manifest details card.
    public bool ShowNativeDetails => IsNative && !HasModernUi;
    public bool ShowLegacyPlaceholder => IsLegacy && !HasNativeConfig;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(SatelliteStatusText);

    // Kind badge shown in the config bay header.
    public string Runtime => Kind == PluginKind.Legacy ? Resources.Label_ClassicRuntime : Resources.Label_ModernRuntime;

    public string StatusLabel => Status switch
    {
        PluginStatus.Loading => Resources.Status_Starting,
        PluginStatus.Live => Resources.Status_Live,
        PluginStatus.Running => Resources.Status_Running,
        PluginStatus.Loaded => Resources.Status_Loaded,
        PluginStatus.NotLoaded => Resources.Status_NotLoaded,
        PluginStatus.Unavailable => Resources.Status_Unavailable,
        _ => Resources.Status_Error
    };

    // Shown in the config bay when there's nothing embeddable for this plugin.
    public string ConfigPlaceholderTitle =>
        Kind == PluginKind.Native ? Resources.Plugins_ConfiguredInPlugin
        : Status == PluginStatus.NotLoaded ? Resources.Plugins_NoConfigToShow
        : Resources.Plugins_ConfigUnavailable;

    public string ConfigPlaceholderBody =>
        Kind == PluginKind.Native
            ? Resources.Plugins_ModernNoEmbedBody
        : Status == PluginStatus.NotLoaded
            ? Resources.Plugins_ClassicNoWindowBody
            : Resources.Plugins_EngineDownBody;
}
