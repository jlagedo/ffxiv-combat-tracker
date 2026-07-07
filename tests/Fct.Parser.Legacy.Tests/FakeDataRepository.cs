using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using FFXIV_ACT_Plugin.Common;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;

namespace Fct.Parser.Legacy.Tests
{
    // A stand-in for the real plugin's IDataRepository so BridgeForwarder's producer path (repository
    // snapshots / resource dictionaries / PID) can be unit-tested headlessly, with no game or plugin.
    internal sealed class FakeDataRepository : IDataRepository
    {
        public List<SdkModels.Combatant> Combatants { get; } = new List<SdkModels.Combatant>();
        public Process Process { get; set; }
        public Dictionary<ResourceType, IDictionary<uint, string>> Resources { get; } =
            new Dictionary<ResourceType, IDictionary<uint, string>>();

        public ReadOnlyCollection<SdkModels.Combatant> GetCombatantList() => Combatants.AsReadOnly();
        public Process GetCurrentFFXIVProcess() => Process;
        public IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType) =>
            Resources.TryGetValue(resourceType, out var d) ? d : new Dictionary<uint, string>();

        // Settable so tests can exercise BridgeForwarder's SessionStateChanged projection (P3.3) —
        // defaults mirror the values the real headless plugin returns per the P0.3 verdict, except
        // GetGameVersion (defaulted here to a non-empty sentinel so tests can tell "forwarded" from
        // "never touched" apart from the real "" unknown-version case, which is exercised explicitly).
        public Language Language { get; set; } = Language.English;
        public byte Region { get; set; } = 0;
        public DateTime ServerTimestamp { get; set; } = DateTime.MinValue;
        public string GameVersion { get; set; } = "1.2.3";
        public bool ChatLogAvailable { get; set; } = true;

        public Language GetSelectedLanguageID() => Language;
        public uint GetCurrentTerritoryID() => 0;
        public uint GetCurrentPlayerID() => 0;
        public SdkModels.Player GetPlayer() => new SdkModels.Player();
        public DateTime GetServerTimestamp() => ServerTimestamp;
        public string GetGameVersion() => GameVersion;
        public bool IsChatLogAvailable() => ChatLogAvailable;
        public string[] GetAntiVirusNames() => Array.Empty<string>();
        public byte GetGameRegion() => Region;
    }
}
