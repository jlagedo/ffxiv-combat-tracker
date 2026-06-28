using System;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // Dnum is ACT's damage-number wrapper with sentinel values the engine compares against.
    public class DnumTests
    {
        [Fact]
        public void Sentinels_have_expected_numbers()
        {
            Assert.Equal(0L, (long)Dnum.NoDamage);
            Assert.Equal(-1L, (long)Dnum.Miss);
            Assert.Equal(-9L, (long)Dnum.Unknown);
            Assert.Equal(-10L, (long)Dnum.Death);
            Assert.Equal(-11L, (long)Dnum.ThreatPosition);
        }

        [Fact]
        public void Implicit_long_conversion_round_trips_positive()
        {
            Dnum d = 1234L;
            Assert.Equal(1234L, (long)d);
        }

        [Fact]
        public void Implicit_conversion_clamps_unknown_negatives_below_death()
        {
            // The long→Dnum operator collapses any value below Death(-10) to Unknown(-9);
            // only explicitly-constructed sentinels keep an exact sub-(-10) value.
            Dnum d = -50L;
            Assert.Equal(-9L, (long)d);
        }

        [Fact]
        public void Equality_is_by_number()
        {
            Assert.True(Dnum.Miss == new Dnum(-1L));
            Assert.True(new Dnum(5L) != Dnum.NoDamage);
            Assert.Equal(new Dnum(7L), new Dnum(7L));
        }

        [Fact]
        public void ToString_uses_number_for_positive_and_custom_string_otherwise()
        {
            Assert.Equal("1000", new Dnum(1000L).ToString());
            Assert.Equal("1.0K", new Dnum(1000L, "1.0K").DamageString);
            Assert.Equal("999", new Dnum(999L).DamageString); // empty custom → falls back to number
        }

        [Fact]
        public void CompareTo_orders_by_number()
        {
            Assert.True(new Dnum(5L).CompareTo(new Dnum(10L)) < 0);
            Assert.True(new Dnum(10L).CompareTo(new Dnum(5L)) > 0);
            Assert.Equal(0, new Dnum(5L).CompareTo(new Dnum(5L)));
        }
    }

    public class MasterSwingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        [Fact]
        public void Null_special_defaults_to_none()
        {
            var s = new MasterSwing(2, false, null, new Dnum(5), T0, 0, "Attack", "A", "physical", "B");
            Assert.Equal("none", s.Special);
        }

        [Fact]
        public void Nine_arg_ctor_sets_special_to_none()
        {
            var s = new MasterSwing(2, true, new Dnum(5), T0, 3, "Attack", "A", "physical", "B");
            Assert.Equal("none", s.Special);
            Assert.True(s.Critical);
            Assert.Equal(3, s.TimeSorter);
        }

        [Fact]
        public void CompareTo_orders_by_time_sorter()
        {
            var a = new MasterSwing(2, false, new Dnum(1), T0, 1, "Attack", "A", "physical", "B");
            var b = new MasterSwing(2, false, new Dnum(1), T0, 2, "Attack", "A", "physical", "B");
            Assert.True(a.CompareTo(b) < 0);
            Assert.True(b.CompareTo(a) > 0);
            Assert.True(a.CompareTo(null) > 0);
        }

        [Fact]
        public void Fields_round_trip_from_ctor()
        {
            var s = new MasterSwing(2, false, "crit", new Dnum(42), T0, 7, "Fire", "Caster", "magic", "Target");
            Assert.Equal(2, s.SwingType);
            Assert.Equal(42L, (long)s.Damage);
            Assert.Equal("Fire", s.AttackType);
            Assert.Equal("Caster", s.Attacker);
            Assert.Equal("magic", s.DamageType);
            Assert.Equal("Target", s.Victim);
        }
    }
}
