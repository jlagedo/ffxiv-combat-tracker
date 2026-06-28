using Fct.Parser.Native;
using Xunit;

namespace Fct.Parser.Native.Tests
{
    // Deterministic unit tests over hand-written lines in the real ACT Network_*.log format.
    // Names are fictional; only the structure mirrors real logs.
    public class NetworkLogLineTests
    {
        // Real format, anonymized values.
        private const string Ability =
            "21|2026-04-23T21:02:05.8760000-03:00|10001111|Aria Vale|03|Sprint|10001111|Aria Vale|1E00000E|320000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|185131|185131|10000|10000|||-596.04|-834.21|30.02|1.51|185131|185131|10000|10000|||-596.04|-834.21|30.02|1.51|00006722|0|1|00||01|03|03|0.600|BBCA|deadbeefcafef00d";
        private const string Zone =
            "01|2026-04-23T20:55:26.2500000-03:00|281|Shirogane|a6b95f06a9e581d2";
        private const string AddCombatant =
            "03|2026-04-23T20:55:26.2500000-03:00|10002222|Kael Brightwind|25|64|0000|4E|Behemoth|0|0|297011|297011|10000|10000|||-591.83|-837.50|30.00|2.17|8698552fb502d7b4";
        private const string PrimaryPlayer =
            "02|2026-04-23T20:55:26.2500000-03:00|10002222|Kael Brightwind|659e610a84a79986";

        [Fact]
        public void Parses_type_and_timestamp()
        {
            Assert.True(NetworkLogLine.TryParse(Ability, out var line));
            Assert.Equal(21, line.TypeCode);
            Assert.Equal(LogLineType.NetworkAbility, line.Type);
            Assert.Equal(new DateTimeOffset(2026, 4, 23, 21, 2, 5, 876, TimeSpan.FromHours(-3)), line.Timestamp);
        }

        [Fact]
        public void Projects_ability_source_target_and_name()
        {
            Assert.True(NetworkLogLine.TryParse(Ability, out var line));
            var a = line.Ability;
            Assert.NotNull(a);
            Assert.Equal("10001111", a!.Value.Source.Id);
            Assert.Equal("Aria Vale", a.Value.Source.Name);
            Assert.Equal("03", a.Value.AbilityId);
            Assert.Equal("Sprint", a.Value.AbilityName);
            Assert.Equal("Aria Vale", a.Value.Target.Name);
        }

        [Fact]
        public void Projects_zone_name()
        {
            Assert.True(NetworkLogLine.TryParse(Zone, out var line));
            Assert.Equal(LogLineType.ChangeZone, line.Type);
            Assert.Equal("Shirogane", line.ZoneName);
            Assert.Null(line.Ability);
        }

        [Theory]
        [InlineData(AddCombatant, LogLineType.AddCombatant, "10002222", "Kael Brightwind")]
        [InlineData(PrimaryPlayer, LogLineType.ChangePrimaryPlayer, "10002222", "Kael Brightwind")]
        public void Projects_actor_lines(string raw, LogLineType type, string id, string name)
        {
            Assert.True(NetworkLogLine.TryParse(raw, out var line));
            Assert.Equal(type, line.Type);
            var actor = line.Actor;
            Assert.NotNull(actor);
            Assert.Equal(id, actor!.Value.Id);
            Assert.Equal(name, actor.Value.Name);
        }

        [Fact]
        public void Unknown_type_code_maps_to_Unknown_but_still_parses()
        {
            Assert.True(NetworkLogLine.TryParse("251|2026-04-23T20:55:26.0000000-03:00|some message|abc123", out var line));
            Assert.Equal(251, line.TypeCode);
            Assert.Equal(LogLineType.Unknown, line.Type);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("notanumber|2026-04-23T20:55:26.0000000-03:00|x")]   // bad type
        [InlineData("21|not-a-timestamp|x")]                              // bad timestamp
        [InlineData("21|2026-04-23T20:55:26.0000000-03:00")]             // too few fields
        public void Rejects_malformed_lines(string? raw)
        {
            Assert.False(NetworkLogLine.TryParse(raw, out _));
        }

        [Fact]
        public void Field_accessor_is_bounds_safe()
        {
            Assert.True(NetworkLogLine.TryParse(Zone, out var line));
            Assert.Equal("281", line.Field(2));
            Assert.Null(line.Field(999));
            Assert.Null(line.Field(-1));
        }
    }
}
