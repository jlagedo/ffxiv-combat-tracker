using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Raw packet → synthetic custom line round-trip (B4).</summary>
    public sealed class RawPacketFlowTests
    {
        // B4 — OverlayPlugin turns a raw packet into a custom 256+ log line and re-injects it onto
        // the live bus (LineBaseCustom.cs:80-86 → FFXIVRepository.cs:493-515). The contract has no
        // emit/write-back hatch: IRawPacketSource is read-only and AppendLogLine targets the export
        // log, not the event bus. RED until G4 (a gated IRawLogLineEmitter) exists.
        [Fact(Skip = "G4: synthetic-line emit path (IRawLogLineEmitter) not yet in the contract")]
        public void B4_RawPacket_BecomesSyntheticLine_OnTheBus()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var custom = new TrigDouble(@"^257\|");
            custom.Attach(shim);

            // What we WANT: OP decodes a packet and emits a custom 256+ line onto the live bus, which
            // a native RawLogLine consumer then sees. No API exists to do this today.
            // host.RawLogLines.Emit((LogMessageType)257, "257|customdata");  // <-- G4

            Assert.Single(custom.Fired);
        }
    }
}
