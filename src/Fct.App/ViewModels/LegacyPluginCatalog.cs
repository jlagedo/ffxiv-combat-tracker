using System.Collections.Generic;
using Fct.App.Lang;

namespace Fct.App.ViewModels;

// Presentation metadata for the legacy plugins the satellite can host, keyed by the satellite's
// PLUGIN key. This is display copy only — the *load status* always comes from the satellite. Keys
// the satellite reports but we don't recognise fall back to the reported title with a generic role.
// Plugin Name values are third-party product names and are never translated; Role/Description are.
internal static class LegacyPluginCatalog
{
    public readonly record struct Entry(string Name, string Role, string Description);

    private static Dictionary<string, Entry> BuildKnown() => new()
    {
        ["ffxiv"] = new("FFXIV_ACT_Plugin", Resources.Catalog_Ffxiv_Role, Resources.Catalog_Ffxiv_Description),
        ["overlay"] = new("OverlayPlugin", Resources.Catalog_Overlay_Role, Resources.Catalog_Overlay_Description),
        ["triggernometry"] = new("Triggernometry", Resources.Catalog_Triggernometry_Role, Resources.Catalog_Triggernometry_Description),
        ["discord"] = new("Discord Triggers", Resources.Catalog_Discord_Role, Resources.Catalog_Discord_Description),
        ["hojoring"] = new("Hojoring", Resources.Catalog_Hojoring_Role, Resources.Catalog_Hojoring_Description),
        ["probe"] = new("Stream Probe", Resources.Catalog_Probe_Role, Resources.Catalog_Probe_Description),
    };

    public static Entry For(string key, string reportedTitle)
        => BuildKnown().TryGetValue(key, out var e)
            ? e
            : new Entry(
                string.IsNullOrWhiteSpace(reportedTitle) ? key : reportedTitle,
                Resources.Catalog_Fallback_Role,
                Resources.Catalog_Fallback_Description);
}
