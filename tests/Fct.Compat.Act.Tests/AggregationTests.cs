using System;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    [Collection("act")]
    public class AttackTypeTests
    {
        public AttackTypeTests(ActTablesFixture _) { }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        private static AttackType WithSwings(params MasterSwing[] swings)
        {
            var at = new AttackType("All", null);
            foreach (var s in swings) at.AddCombatAction(s);
            return at;
        }

        private static MasterSwing S(Dnum dmg, int seq, bool crit = false)
            => new MasterSwing(2, crit, "none", dmg, T0.AddSeconds(seq), seq, "Attack", "A", "physical", "B");

        [Fact]
        public void Damage_hits_crits_miss_and_swings_are_counted_correctly()
        {
            var at = WithSwings(
                S(new Dnum(100), 0, crit: true),
                S(new Dnum(200), 1),
                S(new Dnum(300), 2),
                S(Dnum.Miss, 3),
                S(Dnum.Death, 4));

            Assert.Equal(600L, at.Damage);   // only positive damage
            Assert.Equal(3, at.Hits);         // blockIsHit=false → Damage>0
            Assert.Equal(1, at.CritHits);
            Assert.Equal(1, at.Misses);       // == Dnum.Miss
            Assert.Equal(4, at.Swings);       // everything except the Death sentinel
            Assert.Equal(300L, at.MaxHit);
            Assert.Equal(100L, at.MinHit);
            Assert.Equal(200.0, at.Average, 3);
            Assert.Equal(200L, at.Median);    // Damage>=0 sorted [100,200,300] → middle
            Assert.Equal(100f / 3f, at.CritPerc, 3);
        }

        [Fact]
        public void Empty_attack_type_is_all_zeros()
        {
            var at = WithSwings();
            Assert.Equal(0L, at.Damage);
            Assert.Equal(0, at.Hits);
            Assert.Equal(0f, at.CritPerc);
            Assert.Equal(0L, at.MaxHit);
            Assert.Equal(TimeSpan.Zero, at.Duration);
        }

        [Fact]
        public void blockIsHit_counts_zero_damage_swings_as_hits()
        {
            var prev = ActGlobals.blockIsHit;
            try
            {
                ActGlobals.blockIsHit = true;
                var at = WithSwings(S(new Dnum(0), 0), S(new Dnum(50), 1));
                Assert.Equal(2, at.Hits); // Damage>=0 includes the zero swing
            }
            finally { ActGlobals.blockIsHit = prev; }
        }

        [Fact]
        public void Dps_uses_own_span_between_first_and_last_swing()
        {
            var at = WithSwings(S(new Dnum(100), 0), S(new Dnum(100), 10));
            Assert.Equal(10.0, at.Duration.TotalSeconds, 3);
            Assert.Equal(20.0, at.DPS, 3); // 200 dmg / 10s
        }
    }

    [Collection("act")]
    public class CombatantAndEncounterTests
    {
        public CombatantAndEncounterTests(ActTablesFixture _) { }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        [Fact]
        public void Outgoing_swing_routes_to_outgoing_damage_and_reverse_to_incoming()
        {
            var enc = ActTables.NewEncounter("Player One");
            enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 1000, T0));

            var pc = enc.GetCombatant("Player One");
            var dummy = enc.GetCombatant("Dummy");

            Assert.NotNull(pc);
            Assert.NotNull(dummy);
            Assert.Equal(1000L, pc.Damage);
            Assert.Equal(0L, pc.DamageTaken);
            Assert.Equal(1000L, dummy.DamageTaken);
            Assert.Equal(0L, dummy.Damage);
        }

        [Fact]
        public void GetCombatant_is_case_insensitive()
        {
            var enc = ActTables.NewEncounter("Player One");
            enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 500, T0));
            Assert.Same(enc.GetCombatant("Player One"), enc.GetCombatant("PLAYER ONE"));
            Assert.Same(enc.GetCombatant("Player One"), enc.GetCombatant("player one"));
        }

        [Fact]
        public void Encounter_damage_sums_all_allies_and_tracks_duration()
        {
            var enc = ActTables.NewEncounter("Player One");
            for (int i = 0; i < 10; i++)
                enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 1000, T0.AddSeconds(i), seq: i));
            enc.AddCombatAction(ActTables.Hit("Player Two", "Dummy", 2000, T0.AddSeconds(5), seq: 100));

            Assert.Equal(12000L, enc.Damage);
            Assert.Equal(9.0, enc.Duration.TotalSeconds, 3); // first hit T0, last T0+9s
            Assert.Equal(3, enc.NumCombatants);              // Player One, Player Two, Dummy
        }

        [Fact]
        public void Crit_percent_and_encdps_match_known_vector()
        {
            // The canonical self-test vector: 10 hits of 1000 over 9s, every 3rd a crit.
            var enc = ActTables.NewEncounter("Player One");
            for (int i = 0; i < 10; i++)
                enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 1000, T0.AddSeconds(i), seq: i, crit: i % 3 == 0));

            var pc = enc.GetCombatant("Player One");
            Assert.Equal(10000L, pc.Damage);
            Assert.Equal(10, pc.Hits);
            Assert.Equal(4, pc.CritHits);
            Assert.Equal(40f, pc.CritDamPerc, 3);
            Assert.Equal(1111.111, pc.EncDPS, 2); // 10000 / 9
        }

        [Fact]
        public void Damage_percent_reflects_share_of_encounter()
        {
            var enc = ActTables.NewEncounter("Player One");
            enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 750, T0, seq: 0));
            enc.AddCombatAction(ActTables.Hit("Player Two", "Dummy", 250, T0.AddSeconds(1), seq: 1));

            Assert.Equal("75%", enc.GetCombatant("Player One").DamagePercent);
            Assert.Equal("25%", enc.GetCombatant("Player Two").DamagePercent);
        }

        [Fact]
        public void Death_sentinel_increments_victim_deaths_and_attacker_kills()
        {
            var enc = ActTables.NewEncounter("Player One");
            // swingType 2 incoming routes to "Incoming Damage"; a Death sentinel marks a death.
            enc.AddCombatAction(new MasterSwing(2, false, "none", Dnum.Death, T0, 0, "Attack", "Boss", "physical", "Player One"));

            Assert.Equal(1, enc.GetCombatant("Player One").Deaths);
        }
    }
}
