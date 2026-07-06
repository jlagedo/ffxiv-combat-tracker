using System;
using Fct.Abstractions;

namespace Fct.Parser.Legacy
{
    // ISOLATION-PLAN P5 — SDK-type-free seam for the synthetic parser stand-in.
    //
    // RunConsume (the plugin-free consumer) drives the stand-in ONLY through this interface, whose
    // signatures name no FFXIV_ACT_Plugin.Common type. So the consumer/replica path never JITs a method
    // touching the SDK unless --stand-in actually activates it: the concrete implementation and every
    // SDK-typed member live in THIS assembly (the SDK-referencing one), reached only after the SDK has
    // been made resolvable — an assembly boundary, not a JIT-granularity class boundary.
    public interface IConsumerStandIn : IDisposable
    {
        // Add the synthetic FFXIV_ACT_Plugin to ActGlobals.oFormActMain.ActPlugins (exact title/status)
        // so discovery-by-reflection (OverlayPlugin/Triggernometry/Hojoring) binds it as the real parser.
        void Register();

        // Route one decoded host frame into the stand-in's repository mirror + SDK event surface.
        void Fold(GameEvent evt);

        // Reflect + bind the stand-in exactly as a consumer plugin does (title scan, cast
        // DataSubscription/DataRepository to the real SDK types, poll GetCombatantList) and report it as
        // a plain, SDK-type-free result — the in-satellite discovery gate.
        StandInVerification SelfVerify();
    }

    // A plain, SDK-type-free snapshot of the stand-in discovery result, safe to return across the seam.
    public sealed class StandInVerification
    {
        public bool Found { get; set; }
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public bool SdkTypesBound { get; set; }
        public int LogLines { get; set; }
        public int Combatants { get; set; }
        // NetworkReceived/NetworkSent raised from fanned RawPacketReceived frames (ISOLATION-PLAN P8).
        public int Packets { get; set; }
        // _iocContainer is the real parser-embedded Microsoft.MinIoC.Container AND resolves ILogFormat +
        // ILogOutput non-null — Hojoring's XIVPluginHelper.Attach() gate (ISOLATION-PLAN P9a).
        public bool RealIocContainer { get; set; }
        // PIPELINE-COMPLETENESS-PLAN P1.4/G4: the stand-in's ConsumerDataRepository.GetGameVersion(),
        // read purely for gate observability — today always the hardcoded "0.0" stub (no producer env
        // tap, no priming path carries it; P3+P4 fix this).
        public string GameVersion { get; set; } = "";
        // PIPELINE-COMPLETENESS-PLAN P1.5/G4: the remaining four IDataRepository env scalars, appended
        // append-only alongside GameVersion for the identical gate-observability reason. Language is the
        // raw SDK Language enum cast to int (the SDK enum has no defined 0 member, so a plain int survives
        // the artifact round-trip unambiguously); ServerTimestampTicks is GetServerTimestamp().Ticks (a
        // DateTime does not serialize losslessly through a single TSV field otherwise).
        public int Language { get; set; }
        public byte Region { get; set; }
        public long ServerTimestampTicks { get; set; }
        public bool IsChatLogAvailable { get; set; }
    }

    // Factory kept SDK-type-free at the seam: the returned interface lets RunConsume hold the stand-in
    // without its own metadata naming any SDK type. Create() must run AFTER the SDK is made resolvable
    // (RunConsume loads the installed parser DLL + runs its module initializer first).
    public static class ConsumerStandInFactory
    {
        public static IConsumerStandIn Create(Action<string> log = null) => new ConsumerStandIn(log, null);

        // Write-back overload (P6): writeBack(id, line) is called when a discovered consumer writes a
        // custom log line through the stand-in's ILogOutput.WriteLine — RunConsume routes it up the bridge.
        public static IConsumerStandIn Create(Action<string> log, Action<int, string> writeBack)
            => new ConsumerStandIn(log, writeBack);
    }
}
