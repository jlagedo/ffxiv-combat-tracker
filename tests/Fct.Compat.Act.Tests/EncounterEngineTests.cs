using System;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // The encounter-boundary engine (ACT FormActMain): SetEncounter starts/continues combat,
    // an idle gap beyond the limit ends it (CheckIdleEndCombat), and the next hostile action
    // opens a fresh encounter. The real FFXIV plugin reads InCombat back off this form to decide
    // which heals to report, so these transitions are what make heal attribution match ACT.
    [Collection("act")]
    public sealed class EncounterEngineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static MasterSwing Dmg(string atk, string vic, DateTime t) =>
            new MasterSwing(2, false, "none", new Dnum(100), t, 0, "Attack", atk, "physical", vic);

        private static FormActMain NewForm()
        {
            ActTables.EnsureInstalled();
            ActGlobals.charName = "YOU";
            return new FormActMain { CurrentZone = "Zone" };
        }

        [Fact]
        public void Idle_gap_ends_combat_and_next_action_starts_new_encounter()
        {
            var form = NewForm();
            int starts = 0, ends = 0;
            form.OnCombatStart += (_, __) => starts++;
            form.OnCombatEnd += (_, __) => ends++;

            form.SetEncounter(T0, "YOU", "Enemy");
            form.AddCombatAction(Dmg("YOU", "Enemy", T0));
            Assert.True(form.InCombat);
            Assert.Equal(1, form.ActiveZone.Items.Count);
            Assert.Equal(1, starts);

            // 5 s of quiet log time: under the 6 s idle limit, combat continues.
            Assert.False(form.AdvanceClock(T0.AddSeconds(5)));
            Assert.True(form.InCombat);

            // 7 s gap from the last hostile action: idle-end fires.
            Assert.True(form.AdvanceClock(T0.AddSeconds(7)));
            Assert.False(form.InCombat);
            Assert.Equal(1, ends);
            Assert.False(form.ActiveZone.ActiveEncounter.Active);

            // The next hostile action opens a second encounter.
            var t1 = T0.AddSeconds(10);
            form.SetEncounter(t1, "YOU", "Enemy");
            form.AddCombatAction(Dmg("YOU", "Enemy", t1));
            Assert.True(form.InCombat);
            Assert.Equal(2, form.ActiveZone.Items.Count);
            Assert.Equal(2, starts);
        }

        [Fact]
        public void Continuous_action_within_idle_limit_stays_one_encounter()
        {
            var form = NewForm();
            for (int i = 0; i < 10; i++)
            {
                var t = T0.AddSeconds(i * 3); // 3 s apart, under the 6 s limit
                form.AdvanceClock(t);
                form.SetEncounter(t, "YOU", "Enemy");
                form.AddCombatAction(Dmg("YOU", "Enemy", t));
            }
            Assert.True(form.InCombat);
            Assert.Equal(1, form.ActiveZone.Items.Count);
        }

        [Fact]
        public void EndCombat_marks_encounter_inactive_and_fires_once()
        {
            var form = NewForm();
            int ends = 0;
            form.OnCombatEnd += (_, __) => ends++;
            form.SetEncounter(T0, "YOU", "Enemy");
            Assert.True(form.InCombat);

            form.EndCombat(true);
            form.EndCombat(true); // idempotent: no second OnCombatEnd
            Assert.False(form.InCombat);
            Assert.Equal(1, ends);
        }

        [Fact]
        public void Idle_end_disabled_keeps_combat_open()
        {
            var form = NewForm();
            form.IdleEndEnabled = false;
            form.SetEncounter(T0, "YOU", "Enemy");
            Assert.False(form.AdvanceClock(T0.AddSeconds(60)));
            Assert.True(form.InCombat);
        }
    }
}
