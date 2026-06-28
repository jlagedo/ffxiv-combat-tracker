using System;
using Fct.App;
using Xunit;

namespace Fct.App.Tests
{
    public class SatelliteProtocolTests
    {
        [Theory]
        [InlineData("READY pid=123 x64=True clr=4.0.30319.42000", true)]
        [InlineData("READY", true)]
        [InlineData("HWND 1A2B", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsReady_matches_only_ready_lines(string? line, bool expected)
            => Assert.Equal(expected, SatelliteProtocol.IsReady(line));

        [Theory]
        [InlineData("READY pid=1 x64=True clr=4", true)]
        [InlineData("READY pid=1 x64=true clr=4", true)]   // case-insensitive
        [InlineData("READY pid=1 x64=False clr=4", false)] // 32-bit satellite is unusable
        [InlineData("READY pid=1", false)]
        public void IsReady64_requires_x64_true(string line, bool expected)
            => Assert.Equal(expected, SatelliteProtocol.IsReady64(line));

        [Fact]
        public void TryParseHwnd_parses_hex_handle()
        {
            Assert.True(SatelliteProtocol.TryParseHwnd("HWND 1A2B3C", out var h));
            Assert.Equal(new IntPtr(0x1A2B3C), h);
        }

        [Fact]
        public void TryParseHwnd_trims_and_parses_large_64bit_handle()
        {
            Assert.True(SatelliteProtocol.TryParseHwnd("HWND 7FFD1234ABCD  ", out var h));
            Assert.Equal(new IntPtr(0x7FFD1234ABCD), h);
        }

        [Theory]
        [InlineData("HWND 0")]        // zero handle is invalid
        [InlineData("HWND ")]         // no value
        [InlineData("HWND xyz")]      // not hex
        [InlineData("READY x64=True")]// wrong line
        [InlineData("")]
        [InlineData(null)]
        public void TryParseHwnd_rejects_invalid_lines(string? line)
        {
            Assert.False(SatelliteProtocol.TryParseHwnd(line, out var h));
            Assert.Equal(IntPtr.Zero, h);
        }
    }
}
