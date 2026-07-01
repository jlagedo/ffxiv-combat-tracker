using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Raw packet → synthetic custom line round-trip (B4).</summary>
    public sealed class RawPacketFlowTests
    {
        // B4 — OverlayPlugin turns a raw packet into a custom 256+ log line and re-injects it onto
        // the live bus (LineBaseCustom.cs:80-86 → FFXIVRepository.cs:493-515), which a native
        // RawLogLine consumer then sees. The gated IRawLogLineEmitter (G4) is the write-back hatch.
        [Fact]
        public void B4_RawPacket_BecomesSyntheticLine_OnTheBus()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var custom = new TrigDouble(@"^257\|");
            custom.Attach(shim);

            // OP decodes a packet and emits a custom 256+ line onto the live bus.
            host.RawLogLines.Emit((LogMessageType)257, "257|customdata");

            Assert.Single(custom.Fired);
        }
    }
}
