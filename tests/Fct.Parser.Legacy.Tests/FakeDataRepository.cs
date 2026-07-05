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

        public Language GetSelectedLanguageID() => Language.English;
        public uint GetCurrentTerritoryID() => 0;
        public uint GetCurrentPlayerID() => 0;
        public SdkModels.Player GetPlayer() => new SdkModels.Player();
        public DateTime GetServerTimestamp() => DateTime.UtcNow;
        public string GetGameVersion() => "0.0";
        public bool IsChatLogAvailable() => true;
        public string[] GetAntiVirusNames() => Array.Empty<string>();
        public byte GetGameRegion() => 0;
    }
}
