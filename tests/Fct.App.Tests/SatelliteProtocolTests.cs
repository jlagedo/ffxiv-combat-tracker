using System;
using Fct.Bridge;
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

        [Fact]
        public void TryParsePlugin_parses_key_hwnd_status_title()
        {
            Assert.True(SatelliteProtocol.TryParsePlugin(
                "PLUGIN ffxiv|1A2B|FFXIV_ACT_Plugin Started|FFXIV_ACT_Plugin", out var p));
            Assert.Equal("ffxiv", p.Key);
            Assert.Equal(new IntPtr(0x1A2B), p.Hwnd);
            Assert.Equal("FFXIV_ACT_Plugin Started", p.Status);
            Assert.Equal("FFXIV_ACT_Plugin", p.Title);
        }

        [Theory]
        [InlineData("PLUGIN overlay|0|x|y")]   // zero handle is invalid
        [InlineData("PLUGIN overlay|1A2B")]    // too few fields
        [InlineData("HWND 1A2B")]              // wrong line
        [InlineData("")]
        [InlineData(null)]
        public void TryParsePlugin_rejects_invalid_lines(string? line)
            => Assert.False(SatelliteProtocol.TryParsePlugin(line, out _));

        // --- P6 host-routed service commands ---

        [Theory]
        [InlineData("say the pull|now, at 5%\ttab", 80, 2, true)]   // '|', tab, comma survive base64
        [InlineData("plain text", 100, 0, false)]
        [InlineData("", 100, 1, false)]
        public void Speak_round_trips(string text, int volume, int channel, bool sync)
        {
            var line = SatelliteProtocol.FormatSpeak(text, volume, channel, sync);
            Assert.DoesNotContain('\n', line);
            Assert.True(SatelliteProtocol.TryParseSpeak(line, out var t, out var v, out var ch, out var s));
            Assert.Equal(text, t);
            Assert.Equal(volume, v);
            Assert.Equal(channel, ch);
            Assert.Equal(sync, s);
        }

        [Theory]
        [InlineData(@"C:\sounds\ping|alert.wav", 55)]
        [InlineData("relative/path.wav", 100)]
        public void PlaySound_round_trips(string path, int volume)
        {
            var line = SatelliteProtocol.FormatPlaySound(path, volume);
            Assert.True(SatelliteProtocol.TryParsePlaySound(line, out var p, out var v));
            Assert.Equal(path, p);
            Assert.Equal(volume, v);
        }

        [Theory]
        [InlineData("tts")]
        [InlineData("sound")]
        [InlineData("both")]
        public void RegisterSink_round_trips(string caps)
        {
            Assert.True(SatelliteProtocol.TryParseRegisterSink(SatelliteProtocol.FormatRegisterSink(caps), out var c));
            Assert.Equal(caps, c);
            Assert.True(SatelliteProtocol.TryParseUnregisterSink(SatelliteProtocol.FormatUnregisterSink(caps), out var u));
            Assert.Equal(caps, u);
        }

        [Theory]
        [InlineData("PLAYSND 55|zzz")]   // a PlaySound line is not a Speak/RegisterSink line
        [InlineData("REGISTERSINK tts")]
        [InlineData("SUBSCRIBE swings")]
        [InlineData(null)]
        public void Speak_parser_rejects_foreign_lines(string? line)
            => Assert.False(SatelliteProtocol.TryParseSpeak(line, out _, out _, out _, out _));

        [Theory]
        [InlineData(256, "00:1234:cactbot|say|hi\tthere")]   // custom line with '|' and tab
        [InlineData(257, "")]
        public void LogLine_round_trips(int id, string text)
        {
            var line = SatelliteProtocol.FormatLogLine(id, text);
            Assert.DoesNotContain('\n', line);
            Assert.True(SatelliteProtocol.TryParseLogLine(line, out var gotId, out var gotText));
            Assert.Equal(id, gotId);
            Assert.Equal(text, gotText);
        }

        [Theory]
        [InlineData("peer.spawn|weird name", true)]   // name with '|' and space survives base64
        [InlineData("simple", false)]
        public void RegisterCb_round_trips(string name, bool dup)
        {
            Assert.True(SatelliteProtocol.TryParseRegisterCb(SatelliteProtocol.FormatRegisterCb(name, dup), out var n, out var d));
            Assert.Equal(name, n);
            Assert.Equal(dup, d);
            Assert.True(SatelliteProtocol.TryParseUnregisterCb(SatelliteProtocol.FormatUnregisterCb(name), out var un));
            Assert.Equal(name, un);
        }

        [Theory]
        [InlineData("cb.name", "arg|with\ttabs")]
        [InlineData("x", "")]
        public void InvokeCb_round_trips(string name, string arg)
        {
            var line = SatelliteProtocol.FormatInvokeCb(name, arg);
            Assert.DoesNotContain('\n', line);
            Assert.True(SatelliteProtocol.TryParseInvokeCb(line, out var n, out var a));
            Assert.Equal(name, n);
            Assert.Equal(arg, a);
        }
    }
}
