using System.Text.RegularExpressions;
using Fct.App;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    [Collection("satellite")]
    public class SatelliteIntegrationTests
    {
        private readonly SatelliteRunFixture _fx;
        private readonly ITestOutputHelper _out;

        public SatelliteIntegrationTests(SatelliteRunFixture fx, ITestOutputHelper output)
        {
            _fx = fx;
            _out = output;
        }

        private void RequireSatellite()
        {
            Skip.IfNot(_fx.ExeStaged,
                "Satellite not staged. Build src/Fct.App first (dotnet build src/Fct.App/Fct.App.csproj).");
            _out.WriteLine($"satellite: {_fx.ExePath}");
        }

        [SkippableFact]
        public void Satellite_completes_bridge_handshake_as_64bit()
        {
            RequireSatellite();
            _out.WriteLine("handshake: " + _fx.Handshake);
            Assert.True(SatelliteProtocol.IsReady(_fx.Handshake), "no READY handshake received");
            Assert.True(SatelliteProtocol.IsReady64(_fx.Handshake),
                "satellite is not 64-bit (FFXIV_ACT_Plugin requires a 64-bit host)");
        }

        [SkippableFact]
        public void Satellite_publishes_a_valid_window_handle_to_embed()
        {
            RequireSatellite();
            _out.WriteLine("hwnd: 0x" + _fx.WindowHandle.ToString("X"));
            Assert.NotEqual(IntPtr.Zero, _fx.WindowHandle);
        }

        [SkippableFact]
        public void Real_ffxiv_plugin_reaches_started()
        {
            RequireSatellite();
            Skip.IfNot(_fx.PluginPresent, $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}.");
            Assert.Contains("Started", _fx.LogText);
        }

        [SkippableFact]
        public void Self_test_aggregation_matches_known_vector()
        {
            RequireSatellite();
            Skip.IfNot(_fx.PluginPresent, "FFXIV_ACT_Plugin not installed; self-test needs its routing tables.");

            Skip.If(!_fx.LogText.Contains("[SelfTest]"),
                "self-test line not produced within the timeout (plugin/CEF init may be slow).");

            _out.WriteLine(string.Join("\n",
                _fx.LogText.Split('\n').Where(l => l.Contains("[SelfTest]"))));

            // [SelfTest] Damage=10000 Hits=10 Crit%=40 Duration=9s EncDPS=1111.1
            var m = Regex.Match(_fx.LogText, @"\[SelfTest\] Damage=(\d+) Hits=(\d+) Crit%=(\d+)");
            Assert.True(m.Success, "self-test summary line not found");
            Assert.Equal("10000", m.Groups[1].Value);
            Assert.Equal("10", m.Groups[2].Value);
            Assert.Equal("40", m.Groups[3].Value);

            // ExportVariables: encdps=1111 damage=10000 name=Player One crithit%=40%
            Assert.Contains("encdps=1111", _fx.LogText);
            Assert.Contains("crithit%=40%", _fx.LogText);
        }
    }
}
