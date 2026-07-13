using System;
using System.Collections.Generic;
using Fct.Abstractions;
using Xunit;

namespace Fct.Parser.Legacy.Tests
{
    // MachinaRegionBridge reflectively pushes the forwarded region
    // into Machina.FFXIV's own OpcodeManager singleton (the seam OverlayPlugin's FFXIVRepository reads
    // region from, NOT IDataRepository.GetGameRegion() — see MachinaRegionBridge.cs's header comment).
    // Machina.FFXIV.dll is not referenced by any project in this solution (it lives only inside whatever
    // satellite process happens to load it), so these tests run in the NORMAL headless case where
    // Machina.FFXIV is entirely absent from the process — exactly the P3.6 done-when's mandatory safety
    // requirement.
    public sealed class MachinaRegionBridgeTests
    {
        // THE MANDATORY SAFETY GATE: with no Machina.FFXIV assembly loaded anywhere in this test process
        // (the normal headless/unit-test case — this project takes no dependency on Machina.FFXIV),
        // TrySetRegion must be a silent no-op for every possible forwarded region, including a non-Global
        // one, and must never throw or otherwise block the consumer stand-in's Fold() call. A KR/CN-only
        // feature must never destabilize the Global-default primary path (plan §7).
        [Theory]
        [InlineData(GameRegion.Unknown)]
        [InlineData(GameRegion.Global)]
        [InlineData(GameRegion.Chinese)]
        [InlineData(GameRegion.Korean)]
        [InlineData(GameRegion.TraditionalChinese)]
        public void TrySetRegion_never_throws_when_Machina_is_absent_from_the_process(GameRegion region)
        {
            var messages = new List<string>();
            var ex = Record.Exception(() => MachinaRegionBridge.TrySetRegion(region, messages.Add));
            Assert.Null(ex);
        }

        // Passing a null logger must not change the no-throw guarantee either (ConsumerStandIn's _log
        // is never null in production — it defaults to a no-op — but the bridge itself accepts null).
        [Fact]
        public void TrySetRegion_never_throws_with_a_null_logger()
        {
            var ex = Record.Exception(() => MachinaRegionBridge.TrySetRegion(GameRegion.Korean, null));
            Assert.Null(ex);
        }

        // THE REFLECTION-TARGET-RESOLUTION ASSERTION: a non-Global forwarded region must resolve to the
        // exact Machina.FFXIV.GameRegion member name TrySetRegion will Enum.Parse and pass to SetRegion —
        // confirmed against the real Machina.FFXIV.GameRegion enum (Machina.FFXIV's GameRegion.cs and the
        // shipped IINACT copy, both Global/Chinese/Korean[/TraditionalChinese] by name).
        // GameRegion.Unknown has no Machina equivalent (Machina's enum has
        // no member numbered 0) and must resolve to null — the signal TrySetRegion uses to leave
        // Machina's own default/last-set region alone rather than guess.
        [Theory]
        [InlineData(GameRegion.Global, "Global")]
        [InlineData(GameRegion.Chinese, "Chinese")]
        [InlineData(GameRegion.Korean, "Korean")]
        [InlineData(GameRegion.TraditionalChinese, "TraditionalChinese")]
        public void ToMachinaRegionName_maps_every_forwarded_region_to_its_exact_Machina_member_name(
            GameRegion region, string expected)
        {
            Assert.Equal(expected, MachinaRegionBridge.ToMachinaRegionName(region));
        }

        [Fact]
        public void ToMachinaRegionName_maps_Unknown_to_null_so_Machinas_own_default_region_is_left_alone()
        {
            Assert.Null(MachinaRegionBridge.ToMachinaRegionName(GameRegion.Unknown));
        }
    }
}
