using System;
using System.Linq;
using System.Reflection;
using Fct.Abstractions;

namespace Fct.Parser.Legacy
{
    // OverlayPlugin's FFXIVRepository reads the client's FFXIV
    // region from Machina's own singleton (FFXIVRepository.GetMachinaRegion(),
    // Assembly.Load("Machina.FFXIV") -> Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.GameRegion,
    // reflection), NEVER from IDataRepository.GetGameRegion() (confirmed against
    // E:\dev\OverlayPlugin\OverlayPlugin.Core\Integration\FFXIVRepository.cs:384-403). So a KR/CN client
    // needs the forwarded region pushed into Machina's OpcodeManager directly, or OverlayPlugin's own
    // Machina-based packet capture (NetworkParser) keeps decoding with the Global opcode table.
    //
    // Machina.FFXIV.dll is NOT referenced by this project (it lives only in whichever satellite process
    // loads it — the real parser via Costura-embedded resources, or OverlayPlugin's own copy for its
    // network capture) — this bridge is reflection-only, best-effort, and MUST NEVER throw or block the
    // consumer stand-in's Fold(): Machina defaults its own OpcodeManager to Global
    // (E:\dev\ffxiv\act\machina\Machina.FFXIV\Headers\Opcodes\OpcodeManager.cs SetRegion's own
    // unknown-region fallback), already correct for the primary (Global) audience, so this gates
    // nothing (plan §7) — it matters only for KR/CN clients, and only once Machina.FFXIV happens to be
    // loaded in this process.
    internal static class MachinaRegionBridge
    {
        // Best-effort: reflectively resolve Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance and
        // invoke its SetRegion(Machina.FFXIV.GameRegion) with the name-mapped forwarded region. No-ops
        // silently (never throws past this method) when: Machina.FFXIV isn't loaded in this process yet,
        // the reflected shape doesn't match what's expected (a future Machina revision), or the forwarded
        // region has no Machina equivalent (Unknown — Machina's own enum starts at Global=1).
        public static void TrySetRegion(GameRegion region, Action<string> log)
        {
            var name = ToMachinaRegionName(region);
            if (name == null) return;   // Unknown/unconfigured -- leave Machina's own default/last-set region alone

            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Machina.FFXIV");
                var managerType = asm?.GetType("Machina.FFXIV.Headers.Opcodes.OpcodeManager");
                var regionType = asm?.GetType("Machina.FFXIV.GameRegion");
                if (managerType == null || regionType == null || !regionType.IsEnum) return;
                if (!Enum.GetNames(regionType).Contains(name)) return;   // a Machina build missing this member

                var instance = managerType
                    .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var setRegion = managerType.GetMethod("SetRegion",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { regionType }, null);
                if (instance == null || setRegion == null) return;

                setRegion.Invoke(instance, new[] { Enum.Parse(regionType, name) });
                log?.Invoke($"[StandIn] Machina OpcodeManager.SetRegion({name}) applied (G6, KR/CN only)");
            }
            catch (Exception ex)
            {
                // Never blocks bring-up: Machina absent, an unexpected shape, or a reflection fault all
                // land here as a silent no-op — this feature is KR/CN-only and gates nothing (plan §7).
                log?.Invoke("[StandIn] Machina SetRegion reflection skipped: " + ex.Message);
            }
        }

        // Fct.Abstractions.GameRegion -> Machina.FFXIV.GameRegion member name (by name, not a numeric
        // cast — Machina's enum is a different type with a different member set: no Unknown/0 member).
        // Internal (not private) so the mapping itself — "the reflection target resolution logic" — is
        // directly assertable from Fct.Parser.Legacy.Tests without needing a live Machina.FFXIV in the
        // test process.
        internal static string ToMachinaRegionName(GameRegion region)
        {
            switch (region)
            {
                case GameRegion.Global: return "Global";
                case GameRegion.Chinese: return "Chinese";
                case GameRegion.Korean: return "Korean";
                case GameRegion.TraditionalChinese: return "TraditionalChinese";
                default: return null;   // Unknown — no Machina equivalent
            }
        }
    }
}
