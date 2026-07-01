using System;
using System.Collections.Generic;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// In-memory <see cref="IEncounterService"/>. <see cref="StartCombat"/>/<see cref="EndCombat"/>
    /// toggle <see cref="InCombat"/> and build an <see cref="EncounterSnapshot"/> from the seeded
    /// combatants + <see cref="SeedExportVariables"/> (the G1 bag), so a native reader can round-trip
    /// the opaque cactbot payload. <see cref="AppendLogLine"/> is captured verbatim.
    /// </summary>
    public sealed class FakeEncounterService : IEncounterService
    {
        private static readonly DateTimeOffset DefaultStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public bool InCombat { get; private set; }
        public EncounterSnapshot? Active { get; private set; }
        public EncounterSnapshot? Last { get; private set; }

        public List<string> AppendedLines { get; } = new List<string>();

        /// <summary>Combatants placed on the snapshot built by <see cref="StartCombat"/>.</summary>
        public IReadOnlyList<CombatantMetrics> SeedCombatants { get; set; } = Array.Empty<CombatantMetrics>();

        /// <summary>The opaque ACT ExportVariables (G1) placed on the encounter snapshot.</summary>
        public IReadOnlyDictionary<string, string> SeedExportVariables { get; set; } = new Dictionary<string, string>();

        /// <summary>Time source for the snapshot's <c>Start</c>; a fixed default when unset.</summary>
        public Func<DateTimeOffset>? Now { get; set; }

        /// <summary>Override for <see cref="ExportText"/>; a canned string when unset.</summary>
        public Func<EncounterSnapshot, EncounterExportFormat, string>? Exporter { get; set; }

        public void StartCombat(string? title = null, string? zone = null)
        {
            InCombat = true;
            var start = Now?.Invoke() ?? DefaultStart;
            Active = new EncounterSnapshot(title ?? string.Empty, start, TimeSpan.Zero, true, 0, 0, SeedCombatants)
            {
                ExportVariables = SeedExportVariables,
                Zone = zone ?? title,
            };
        }

        public void EndCombat(bool export = false)
        {
            InCombat = false;
            if (Active != null)
            {
                Last = Active with { Active = false };
                Active = null;
            }
        }

        public string ExportText(EncounterSnapshot encounter, EncounterExportFormat format)
        {
            if (encounter == null) throw new ArgumentNullException(nameof(encounter));
            return Exporter?.Invoke(encounter, format) ?? $"[{format}] {encounter.Title}";
        }

        public void AppendLogLine(string line) => AppendedLines.Add(line);
    }
}
