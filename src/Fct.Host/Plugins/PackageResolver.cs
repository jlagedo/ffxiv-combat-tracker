using System;
using Fct.Bridge;

namespace Fct.Host.Plugins;

/// <summary>Whether a satellite hosts the sole data source (the parser, which forwards its stream up)
/// or a consumer that reads the host-fanned projection.</summary>
internal enum SatelliteRole { Producer, Consumer }

/// <summary>The satellite a legacy plugin package runs in: its routing identity, its role, and the
/// downstream stream set its facade subscribes to (empty for the producer, which never subscribes).</summary>
internal sealed record PackageDescriptor(string Package, SatelliteRole Role, string[] Subscriptions);

/// <summary>
/// Maps a classified real-legacy plugin to the satellite package that hosts it (ISOLATION-PLAN §2:
/// one satellite process per package). This is the install-catalog seam where plugin-name knowledge is
/// allowed (invariant §4) — it never reaches host routing or engine logic, which key on stream names.
/// Keyed defensively by every stable install signal (entry assembly file, classified id, reported
/// title), so a plugin resolves the same whether picked as a DLL, replayed from the registry, or
/// announced by the satellite. Unknown legacy plugins still get their own isolated consumer satellite.
/// </summary>
internal static class PackageResolver
{
    /// <summary>Resolve the package for a plugin from its entry assembly file name, classified id, and
    /// reported title (any may be null). The three P7 packages plus a per-plugin isolated fallback.</summary>
    public static PackageDescriptor Resolve(string? assemblyFile, string? id, string? title)
    {
        // The parser — the sole producer. It forwards its full stream up; it never subscribes.
        if (Matches("ffxiv_act_plugin", assemblyFile, id, title))
            return new PackageDescriptor("ffxiv", SatelliteRole.Producer, Array.Empty<string>());

        // Triggernometry — reads log lines (rawlog) + the encounter replica (swings/lifecycle) + zone/
        // party/combatant state; drives audio/callbacks back through the host services (P6).
        if (Matches("triggernometry", assemblyFile, id, title))
            return new PackageDescriptor("triggernometry", SatelliteRole.Consumer, new[]
            {
                SatelliteProtocol.StreamSwings,
                SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamZoneParty,
                SatelliteProtocol.StreamCombatants,
                SatelliteProtocol.StreamRepository,
            });

        // ACT-Discord-Triggers — its real job is the terminal audio sink (a control-plane REGISTERSINK);
        // it consumes little combat data, so a minimal read set suffices.
        if (Matches("discord", assemblyFile, id, title))
            return new PackageDescriptor("discord", SatelliteRole.Consumer, new[]
            {
                SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamSwings,
            });

        // OverlayPlugin (cactbot lives inside it) — the deepest consumer, so the full read set (P8): the
        // encounter replica (MiniParse DPS), the log-line firehose (cactbot event source + custom-line
        // fan-back), zone/party/player + combatants (FFXIVRepository poll surface), the repository mirror
        // (GetCombatantList / resource dictionaries / GetCurrentFFXIVProcess), and — uniquely so far — the
        // raw-packet firehose its NetworkProcessors bind through IDataSubscription.NetworkReceived.
        if (Matches("overlay", assemblyFile, id, title))
            return new PackageDescriptor("overlay", SatelliteRole.Consumer, new[]
            {
                SatelliteProtocol.StreamSwings,
                SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamPackets,
                SatelliteProtocol.StreamZoneParty,
                SatelliteProtocol.StreamCombatants,
                SatelliteProtocol.StreamRepository,
            });

        // ACT.Hojoring — the four-plugin suite (SpecialSpellTimer/UltraScouter/TTSYukkuri/XIVLog) shares
        // process-wide singletons in FFXIV.Framework, so it MUST land in one satellite (ISOLATION-PLAN §2):
        // any of the four entry assemblies (or the bare suite signal) resolves to the same "hojoring"
        // package. A pull consumer — it takes the overlay read set MINUS packets (no RegisterNetworkParser
        // consumer in the suite): the encounter replica + log lines + zone/party + combatants + repository.
        if (Matches("hojoring", assemblyFile, id, title)
            || Matches("specialspelltimer", assemblyFile, id, title)
            || Matches("ultrascouter", assemblyFile, id, title)
            || Matches("ttsyukkuri", assemblyFile, id, title)
            || Matches("xivlog", assemblyFile, id, title))
            return new PackageDescriptor("hojoring", SatelliteRole.Consumer, new[]
            {
                SatelliteProtocol.StreamSwings,
                SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamZoneParty,
                SatelliteProtocol.StreamCombatants,
                SatelliteProtocol.StreamRepository,
            });

        // Any other legacy plugin: isolate it in its own consumer satellite with the common read set,
        // so an unrecognized plugin is still process-isolated (and this generalizes to the P9 packages).
        var package = Sanitize(!string.IsNullOrWhiteSpace(id) ? id! : StripDll(assemblyFile) ?? "legacy");
        return new PackageDescriptor(package, SatelliteRole.Consumer, new[]
        {
            SatelliteProtocol.StreamSwings,
            SatelliteProtocol.StreamRawLog,
        });
    }

    // True when the token appears (case-insensitively) in any of the plugin's stable signals.
    private static bool Matches(string token, string? assemblyFile, string? id, string? title)
        => Contains(assemblyFile, token) || Contains(id, token) || Contains(title, token);

    private static bool Contains(string? value, string token)
        => value is not null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? StripDll(string? assemblyFile)
        => assemblyFile is null ? null
           : assemblyFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               ? assemblyFile.Substring(0, assemblyFile.Length - 4) : assemblyFile;

    // A package name is a satellite identity + handshake token; keep it a simple lowercase identifier.
    private static string Sanitize(string value)
    {
        var chars = value.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }
}
