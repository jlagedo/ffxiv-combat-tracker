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

        /// <summary>Open (or continue) an encounter. Equivalent to <c>SetEncounter(now, me, me)</c>.</summary>
        void StartCombat(string? title = null);

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
        IReadOnlyList<CombatantMetrics> Combatants);

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
        int Deaths);
}
