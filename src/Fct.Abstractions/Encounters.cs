using System;
using System.Collections.Generic;

namespace Fct.Abstractions
{
    /// <summary>
    /// Combat-state read/write + encounter access. Replaces Triggernometry's
    /// <c>SetEncounter</c>/<c>EndCombat</c>/<c>InCombat</c> driving and its reflected
    /// <c>GetTextExport</c>/<c>defaultTextFormat</c>/<c>ACTEncounterLog</c> path with typed members.
    /// </summary>
    public interface IEncounterService
    {
        bool InCombat { get; }

        /// <summary>
        /// Open (or continue) an encounter. Equivalent to <c>SetEncounter(now, me, me)</c>.
        /// <paramref name="zone"/> labels the encounter's zone; Triggernometry passes the player name
        /// as both <paramref name="title"/> and zone, so it defaults to <paramref name="title"/>.
        /// </summary>
        void StartCombat(string? title = null, string? zone = null);

        /// <summary>End the active encounter, optionally exporting it.</summary>
        void EndCombat(bool export = false);

        EncounterSnapshot? Active { get; }
        EncounterSnapshot? Last { get; }

        /// <summary>Render an encounter to text in the given format.</summary>
        string ExportText(EncounterSnapshot encounter, EncounterExportFormat format);

        /// <summary>Append a synthetic line to the active encounter's log.</summary>
        void AppendLogLine(string line);
    }

    /// <summary>Output format for <see cref="IEncounterService.ExportText"/>.</summary>
    public enum EncounterExportFormat { Plain, Markdown, Json }

    /// <summary>
    /// An immutable encounter rollup with typed per-combatant metrics — replaces OverlayPlugin's
    /// whole-<c>ExportVariables</c>-dictionary scrape per tick.
    /// </summary>
    public sealed record EncounterSnapshot(
        string Title,
        DateTimeOffset Start,
        TimeSpan Duration,
        bool Active,
        double Dps,
        long Damage,
        IReadOnlyList<CombatantMetrics> Combatants)
    {
        /// <summary>
        /// The opaque ACT <c>EncounterData.ExportVariables</c> dictionary, carried verbatim so the
        /// compat shim can round-trip every key cactbot reads (the fixed metric fields above cannot).
        /// Empty by default; native consumers use the typed fields.
        /// </summary>
        public IReadOnlyDictionary<string, string> ExportVariables { get; init; } = ExportVars.Empty;

        /// <summary>The encounter's zone label (ACT's <c>ZoneName</c>). Null when unset.</summary>
        public string? Zone { get; init; }
    }

    /// <summary>Per-combatant aggregated metrics within an <see cref="EncounterSnapshot"/>.</summary>
    public sealed record CombatantMetrics(
        string Name,
        uint ActorId,
        int Job,
        double EncDps,
        long Damage,
        double DamagePercent,
        long Healing,
        double CritPercent,
        double DirectHitPercent,
        int Deaths)
    {
        /// <summary>
        /// The opaque ACT <c>CombatantData.ExportVariables</c> dictionary (e.g. <c>maxhit</c>,
        /// <c>tohit</c>, <c>swings</c>, <c>Last10/30/60/180DPS</c>), carried verbatim for the compat
        /// shim. Empty by default; native consumers use the typed fields.
        /// </summary>
        public IReadOnlyDictionary<string, string> ExportVariables { get; init; } = ExportVars.Empty;

        /// <summary>Healing that landed on already-full HP (OverlayPlugin's <c>overHeal</c>). 0 by default.</summary>
        public long Overheal { get; init; }

        /// <summary>Incoming damage soaked by shields (OverlayPlugin's <c>damageShield</c>). 0 by default.</summary>
        public long ShieldedDamage { get; init; }

        /// <summary>Healing delivered as a shield/absorb (OverlayPlugin's <c>absorbHeal</c>). 0 by default.</summary>
        public long Absorbed { get; init; }
    }

    /// <summary>Shared empty <c>ExportVariables</c> default (no per-instance allocation).</summary>
    internal static class ExportVars
    {
        public static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    }
}
