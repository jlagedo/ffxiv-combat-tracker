using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Fct.Abstractions;
using Fct.Abstractions.Testing;

namespace Fct.FlowTests
{
    /// <summary>
    /// Shared helpers + tiny "legacy plugin" doubles. Each double makes exactly the call the real
    /// plugin makes (cited by file:line in docs/PLUGIN-API.md), driving it through the ShimStub seam —
    /// it does NOT load the real net48 DLLs.
    /// </summary>
    internal static class Flow
    {
        public static readonly DateTimeOffset T0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static RawLogLine Line(string text, LogMessageType type = (LogMessageType)0, long seq = 1)
            => new RawLogLine(seq, T0, type, text, text);
    }

    /// <summary>
    /// Triggernometry: a regex-over-the-raw-line engine (Trigger.cs:150,615). It only inspects the
    /// pipe-delimited line and ignores typed events — modeled here by subscribing to the shim's
    /// OnLogLineRead surface. Also drives the encounter and named-callback interop it uses.
    /// </summary>
    internal sealed class TrigDouble
    {
        private readonly Regex _rex;

        public TrigDouble(string pattern) => _rex = new Regex(pattern);

        /// <summary>Raw lines this trigger's regex matched.</summary>
        public List<string> Fired { get; } = new List<string>();

        /// <summary>Wire the regex to the legacy Before/OnLogLineRead surface (as ProxyPlugin does).</summary>
        public void Attach(ShimStub shim)
            => shim.OnLogLineRead += (isImport, args) =>
            {
                if (_rex.IsMatch(args.logLine)) Fired.Add(args.logLine);
            };
    }

    /// <summary>
    /// OverlayPlugin (MiniParse) as an encounter reader: it reads ActiveZone.ActiveEncounter +
    /// CombatantData/EncounterData.ExportVariables per tick (MiniParseEventSource.cs:321-360).
    /// Here it reads through the shim's encounter surface.
    /// </summary>
    internal sealed class OpEncounterReaderDouble
    {
        public bool SawInCombat { get; private set; }
        public string? Title { get; private set; }
        public IReadOnlyDictionary<string, string>? ExportVariables { get; private set; }

        public void Poll(ShimStub shim)
        {
            SawInCombat = shim.InCombat;
            var enc = shim.ActiveEncounter;
            Title = enc?.Title;
            ExportVariables = enc?.ExportVariables;
        }
    }
}
