using System.Collections.Generic;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Typed GameEvent records mapped to the legacy IDataSubscription surface (A5).</summary>
    public sealed class EventMappingFlowTests
    {
        // A5 — native ZoneChanged/PartyChanged → OverlayPlugin IDataSubscription consumer
        // (FFXIVRepository.cs:544,551). The shim maps the typed records to the SDK delegate shapes.
        [Fact]
        public void A5_TypedZonePartyEvents_ReachMappedConsumer()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var op = new OpTypedConsumerDouble();
            op.Attach(shim);

            var bus = host.Bus!;
            bus.Emit(new ZoneChanged(1, Flow.T0, 132, "Limsa Lominsa"));
            bus.Emit(new PartyChanged(2, Flow.T0, new List<uint> { 100, 200, 300 }));

            Assert.Equal(132u, op.ZoneId);
            Assert.Equal("Limsa Lominsa", op.ZoneName);
            Assert.Equal(3, op.PartySize);
            Assert.Equal(new uint[] { 100, 200, 300 }, op.PartyList);
        }
    }
}
