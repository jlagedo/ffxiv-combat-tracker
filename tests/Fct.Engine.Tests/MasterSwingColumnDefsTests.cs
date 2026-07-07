using System;
using System.Collections.Generic;
using System.Globalization;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Engine.Tests
{
    // No ExportVariables/oracle gate covers these — they are
    // MasterSwing-level raw columns (StatusDuration + the plugin's showDebug-gated diagnostic set),
    // read only via MasterSwing.GetColumnByName. This is a focused registration-correctness check,
    // optional-but-valuable. EngineTables.Install() is idempotent
    // and already invoked by every other test in this project (ModernEncounterEngine's constructor),
    // so calling it directly here adds no new static-state risk.
    public sealed class MasterSwingColumnDefsTests
    {
        public MasterSwingColumnDefsTests() => EngineTables.Install();

        private static MasterSwing NewSwing(Dictionary<string, object> tags) => new MasterSwing(
            swingType: 2, critical: false, special: "none", damage: new Dnum(100),
            time: DateTime.UtcNow, timeSorter: 0,
            theAttackType: "Attack", attacker: "YOU", theDamageType: "Damage", victim: "Target")
        { Tags = tags };

        [Fact]
        public void StatusDuration_reads_the_d_tagged_value()
        {
            var swing = NewSwing(new Dictionary<string, object> { ["StatusDuration"] = 30.0 });
            Assert.Equal("30", swing.GetColumnByName("StatusDuration"));
        }

        [Fact]
        public void StatusDuration_is_blank_when_the_tag_is_absent()
        {
            var swing = NewSwing(new Dictionary<string, object>());
            Assert.Equal("", swing.GetColumnByName("StatusDuration"));
        }

        [Fact]
        public void Potency_reads_the_lowercase_potency_tag_formatted_to_two_decimals()
        {
            var swing = NewSwing(new Dictionary<string, object> { ["potency"] = 123.456 });
            Assert.Equal("123.46", swing.GetColumnByName("Potency"));
        }

        [Fact]
        public void Potency_defaults_to_zero_when_the_tag_is_absent()
        {
            var swing = NewSwing(new Dictionary<string, object>());
            Assert.Equal("0", swing.GetColumnByName("Potency"));
        }

        [Fact]
        public void StatusEffects_reads_the_string_tag_verbatim()
        {
            var swing = NewSwing(new Dictionary<string, object> { ["StatusEffects"] = "1000|2000" });
            Assert.Equal("1000|2000", swing.GetColumnByName("StatusEffects"));
        }

        [Fact]
        public void DoTBase_formats_a_uint_boxed_tag_without_throwing()
        {
            var swing = NewSwing(new Dictionary<string, object> { ["dotbase"] = 456u });
            Assert.Equal("456", swing.GetColumnByName("DoTBase"));
        }

        [Fact]
        public void DoTBase_formats_a_numeric_string_boxed_tag_without_throwing()
        {
            // Defends the Convert.ToUInt32 choice: per the plan's P0.4 codec note, this tag's boxed
            // CLR type crossing the wire is not fully certain — it must not throw either way.
            var swing = NewSwing(new Dictionary<string, object> { ["dotbase"] = "456" });
            Assert.Equal("456", swing.GetColumnByName("DoTBase"));
        }

        [Theory]
        [InlineData("BuffByte1")]
        [InlineData("BuffByte2")]
        [InlineData("BuffByte3")]
        [InlineData("CritRate")]
        [InlineData("CritEffects")]
        [InlineData("DHRate")]
        [InlineData("DHEffects")]
        public void Remaining_swing_columns_read_their_matching_tag_verbatim(string key)
        {
            var swing = NewSwing(new Dictionary<string, object> { [key] = "7" });
            Assert.Equal("7", swing.GetColumnByName(key));
        }

        [Theory]
        [InlineData("BuffByte1")]
        [InlineData("BuffByte2")]
        [InlineData("BuffByte3")]
        [InlineData("CritRate")]
        [InlineData("CritEffects")]
        [InlineData("DHRate")]
        [InlineData("DHEffects")]
        public void Remaining_swing_columns_are_blank_when_their_tag_is_absent(string key)
        {
            var swing = NewSwing(new Dictionary<string, object>());
            Assert.Equal("", swing.GetColumnByName(key));
        }
    }
}
