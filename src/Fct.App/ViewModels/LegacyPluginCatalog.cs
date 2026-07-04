using System;
using System.Collections.Generic;
using Fct.App.Lang;

namespace Fct.App.ViewModels;

// Presentation metadata for the legacy plugins the satellite can host. This is display copy only — the
// *load status* always comes from the satellite. Plugins are matched first by the satellite's PLUGIN key
// (stable for standalone dev runs) and then by the reported title (the catalog-driven key is the plugin's
// synthesized install id, so title is what identifies a product like "FFXIV_ACT_Plugin"). Anything we
// don't recognise falls back to the reported title with a generic role. Plugin Name values are
// third-party product names and are never translated; Role/Description are.
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

    // Title prefixes -> catalog key, so a plugin loaded under its synthesized install id still resolves.
    private static (string Prefix, string Key)[] TitleAliases() => new[]
    {
        ("FFXIV_ACT_Plugin", "ffxiv"),
        ("OverlayPlugin", "overlay"),
        ("Triggernometry", "triggernometry"),
    };

    public static Entry For(string key, string reportedTitle)
    {
        var known = BuildKnown();
        if (known.TryGetValue(key, out var e)) return e;
        foreach (var (prefix, aliasKey) in TitleAliases())
            if (!string.IsNullOrEmpty(reportedTitle) && reportedTitle.StartsWith(prefix, StringComparison.Ordinal))
                return known[aliasKey];
        return new Entry(
            string.IsNullOrWhiteSpace(reportedTitle) ? key : reportedTitle,
            Resources.Catalog_Fallback_Role,
            Resources.Catalog_Fallback_Description);
    }

    // The FFXIV parser is the one plugin shown as "Live" (it is the data source). Identified by its
    // stable standalone key or its reported title, independent of the synthesized install id.
    public static bool IsParser(string key, string reportedTitle)
        => key == "ffxiv"
           || (!string.IsNullOrEmpty(reportedTitle) && reportedTitle.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
}
