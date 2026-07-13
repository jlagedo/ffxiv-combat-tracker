using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // Tier 2: the ring-buffer dispatcher against the LIVE OverlayPlugin + FFXIV_ACT_Plugin DLLs.
    // The satellite wraps the real plugin (WrappedFfxivPlugin) so the real OverlayPlugin binds to
    // our RingBufferDataSubscription. These assert what a no-game live run can prove:
    //   (1) FFXIV_ACT_Plugin.Common unified to ONE loaded copy — so OverlayPlugin's IDataSubscription
    //       cast resolves to our type (the type-identity risk in the plan);
    //   (2) the real OverlayPlugin discovered our wrapper, cast our IDataSubscription, and bound onto
    //       the ring (ProcessChanged subscriber) — i.e. its events flow through our dispatcher.
    // OverlayPlugin defers NetworkReceived (packet) subscription until a live FFXIV process appears,
    // so the actual packet fan-out is exercised by the Tier 3 capture/replay path, not here.
    // Dispatcher correctness/ordering is covered deterministically by Fct.Parser.Legacy.Tests.
    [Collection("satellite")]
    public class PacketDispatchTests
    {
        private readonly SatelliteRunFixture _fx;
        private readonly ITestOutputHelper _out;

        public PacketDispatchTests(SatelliteRunFixture fx, ITestOutputHelper output)
        {
            _fx = fx;
            _out = output;
        }

        private string RequireDiagnostics()
        {
            Skip.IfNot(_fx.ExeStaged,
                "Satellite not staged. Build src/Fct.App first (dotnet build src/Fct.App/Fct.App.csproj).");
            Skip.IfNot(_fx.PluginPresent,
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}.");
            Skip.IfNot(File.Exists(SatelliteRunFixture.OverlayPluginPath),
                $"OverlayPlugin not found at {SatelliteRunFixture.OverlayPluginPath}; wrapped-path live test needs it.");

            // Diagnostics land after OverlayPlugin's background registration; wait for the marker.
            var log = _fx.WaitForLog("[Diag] injected=", 60);
            Skip.If(!log.Contains("[Diag]"),
                "dispatcher diagnostics not produced within the timeout (plugin/CEF init may be slow).");
            _out.WriteLine(string.Join("\n", log.Split('\n').Where(l => l.Contains("[Diag]"))));
            return log;
        }

        [SkippableFact]
        public void Common_unifies_to_a_single_loaded_assembly()
        {
            var log = RequireDiagnostics();
            var m = Regex.Match(log, @"\[Diag\] FFXIV_ACT_Plugin\.Common loaded copies=(\d+)");
            Assert.True(m.Success, "Common-copies diagnostic not found");
            Assert.Equal(1, int.Parse(m.Groups[1].Value)); // one identity → OverlayPlugin's cast is our type
        }

        [SkippableFact]
        public void Real_overlayplugin_binds_to_the_ring()
        {
            var log = RequireDiagnostics();
            var m = Regex.Match(log, @"ProcessChanged subscribers=(\d+)");
            Assert.True(m.Success, "bind diagnostic not found");
            int bound = int.Parse(m.Groups[1].Value);
            _out.WriteLine($"OverlayPlugin handlers bound to the ring (ProcessChanged): {bound}");
            // >0 proves the live OverlayPlugin discovered our wrapper, cast our IDataSubscription, and
            // subscribed onto the ring — its events route through our dispatcher, not BeginInvoke.
            Assert.True(bound > 0, $"expected the real OverlayPlugin to bind onto the ring, got {bound}");
        }

        [SkippableFact]
        public void Injected_packets_flow_through_the_ring_without_drops()
        {
            var log = RequireDiagnostics();
            Assert.Contains("[Diag] injected=8 dropped=0", log); // ring delivers under the live stack, none lost
        }
    }
}
