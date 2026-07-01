using System;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Fct.Compat.ActEngine.TestSupport;
using Xunit;

namespace Fct.Compat.Shim.Tests
{
    /// <summary>
    /// D5: the ACT encounter driver on <see cref="FormActMain"/> (<c>SetEncounter</c>/
    /// <c>AddCombatAction</c>/<c>EndCombat</c>) folds swings into the shared aggregation engine
    /// (<c>ActiveZone.ActiveEncounter</c>) and mirrors combat state onto the modern
    /// <see cref="Fct.Abstractions.IEncounterService"/> (flow A4/B2).
    /// </summary>
    public class EncounterTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        private static FormActMain NewForm(out FakePluginHost host)
        {
            host = new FakePluginHost();
            var form = new FormActMain(host);
            ActEngineHarness.InstallRoutingTables();
            return form;
        }

        [Fact]
        public void SetEncounter_opens_combat_and_mirrors_to_the_host()
        {
            var form = NewForm(out var host);

            Assert.False(host.Encounters.InCombat);
            form.SetEncounter(T0, "YOU", "Dummy");

            Assert.True(host.Encounters.InCombat);
            Assert.True(form.InCombat);
            Assert.NotNull(host.Encounters.Active);
            Assert.True(form.ActiveZone.ActiveEncounter.Active);
        }

        [Fact]
        public void AddCombatAction_aggregates_into_the_active_encounter()
        {
            var form = NewForm(out _);
            form.SetEncounter(T0, "YOU", "Dummy");

            for (int i = 0; i < 10; i++)
                form.AddCombatAction(ActEngineHarness.Hit("YOU", "Dummy", 1000, T0.AddSeconds(i), seq: i));

            var enc = form.ActiveZone.ActiveEncounter;
            Assert.Equal(10000, enc.GetCombatant("YOU").Damage);
            Assert.Equal(10000, enc.Damage);   // summed over allies (anchor = YOU); Dummy is the foe
        }

        [Fact]
        public void Before_and_after_combat_action_events_fire_per_swing()
        {
            var form = NewForm(out _);
            form.SetEncounter(T0, "YOU", "Dummy");

            int before = 0, after = 0;
            form.BeforeCombatAction += (_, __) => before++;
            form.AfterCombatAction += (_, __) => after++;
            form.AddCombatAction(ActEngineHarness.Hit("YOU", "Dummy", 500, T0));

            Assert.Equal(1, before);
            Assert.Equal(1, after);
        }

        [Fact]
        public void EndCombat_closes_and_mirrors_to_the_host()
        {
            var form = NewForm(out var host);
            form.SetEncounter(T0, "YOU", "Dummy");

            bool ended = false;
            form.OnCombatEnd += (_, __) => ended = true;
            form.EndCombat(false);

            Assert.False(host.Encounters.InCombat);
            Assert.False(form.InCombat);
            Assert.True(ended);
            Assert.False(form.ActiveZone.ActiveEncounter.Active);
            Assert.NotNull(host.Encounters.Last);
        }
    }
}
