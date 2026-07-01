using System.Collections.Generic;

namespace Fct.App.ViewModels;

// Presentation metadata for the legacy plugins the satellite can host, keyed by the satellite's
// PLUGIN key. This is display copy only — the *load status* always comes from the satellite. Keys
// the satellite reports but we don't recognise fall back to the reported title with a generic role.
internal static class LegacyPluginCatalog
{
    public readonly record struct Entry(string Name, string Role, string Description);

    private static readonly Dictionary<string, Entry> Known = new()
    {
        ["ffxiv"] = new(
            "FFXIV_ACT_Plugin",
            "Network parser · data source",
            "Reads the game's network stream and turns it into combat actions. Every other plugin builds on the data it produces."),
        ["overlay"] = new(
            "OverlayPlugin",
            "Overlays · cactbot bridge",
            "Serves overlays and bridges cactbot — DPS meters, timelines, and raid tools. Overlay windows are drawn by OverlayPlugin itself; configure them from its tab here."),
        ["triggernometry"] = new(
            "Triggernometry",
            "Triggers · timelines · TTS",
            "Matches log lines to fire alerts, timers, and text-to-speech callouts from your own trigger packs."),
        ["discord"] = new(
            "Discord Triggers",
            "Encounter → Discord",
            "Posts encounter results and trigger events to a Discord webhook when a fight ends."),
        ["hojoring"] = new(
            "Hojoring",
            "Spell timers · TTS suite",
            "The Hojoring suite — SpecialSpellTimer, TTSYukkuri, UltraScouter, and XIVLog."),
        ["probe"] = new(
            "Stream Probe",
            "Diagnostics",
            "A diagnostic tap on the combat-action and raw-packet stream, for inspecting the pipeline."),
    };

    public static Entry For(string key, string reportedTitle)
        => Known.TryGetValue(key, out var e)
            ? e
            : new Entry(
                string.IsNullOrWhiteSpace(reportedTitle) ? key : reportedTitle,
                "Classic plugin",
                "A classic ACT plugin, running in the compatibility engine.");
}
