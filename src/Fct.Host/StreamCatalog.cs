using System;
using System.Collections.Generic;
using Fct.Abstractions;
using Fct.Bridge;

namespace Fct.Host
{
    /// <summary>
    /// Maps the canonical SUBSCRIBE stream tokens (P4) to a concrete <see cref="GameEventFilter"/> the
    /// host bus fans down to a satellite. The host routes by stream name, never by plugin identity
    /// (invariant §4): a satellite asks for "swings"/"packets"/… and gets exactly those. Unknown tokens
    /// are ignored. Returns null when nothing recognizable was requested, so the caller creates no egress
    /// (an empty type set would otherwise match ALL events).
    /// </summary>
    internal static class StreamCatalog
    {
        public static GameEventFilter? ToFilter(IEnumerable<string> tokens)
        {
            var types = new List<Type>();
            bool rawLog = false, rawPackets = false;

            foreach (var raw in tokens)
            {
                switch (raw?.Trim())
                {
                    case SatelliteProtocol.StreamSwings:
                        types.Add(typeof(CombatSwing));
                        types.Add(typeof(SetEncounterRequested));
                        types.Add(typeof(ZoneChangeRequested));
                        types.Add(typeof(EndCombatRequested));
                        break;
                    case SatelliteProtocol.StreamPackets:
                        types.Add(typeof(RawPacketReceived));
                        rawPackets = true;
                        break;
                    case SatelliteProtocol.StreamCombatants:
                        types.Add(typeof(CombatantAdded));
                        types.Add(typeof(CombatantRemoved));
                        break;
                    case SatelliteProtocol.StreamZoneParty:
                        types.Add(typeof(ZoneChanged));
                        types.Add(typeof(PartyChanged));
                        types.Add(typeof(PrimaryPlayerChanged));
                        break;
                    case SatelliteProtocol.StreamRawLog:
                        rawLog = true;
                        break;
                    case SatelliteProtocol.StreamRepository:
                        types.Add(typeof(RepositorySnapshot));
                        types.Add(typeof(ResourceDictionaryForwarded));
                        types.Add(typeof(GameProcessChanged));
                        break;
                    default:
                        break; // unknown token — ignored
                }
            }

            if (types.Count == 0 && !rawLog)
                return null;

            return new GameEventFilter(types.ToArray(), IncludeRawLogLines: rawLog, IncludeRawPackets: rawPackets);
        }
    }
}
