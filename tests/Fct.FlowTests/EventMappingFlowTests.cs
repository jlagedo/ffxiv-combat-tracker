using System.Collections.Generic;
using System.Linq;
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

        // P1.6 (alliance party gate) — G7: in alliance content the SDK's PartyListChanged(partyList,
        // partySize) reports partySize (the player's 8-person party) DISTINCT from the up-to-24
        // visible alliance roster. PartyChanged now carries a PartySize field (Events.cs), but nothing
        // forwards or folds it yet — the shim's PartyChanged handler (ShimStub.cs:35) still derives
        // the SDK partySize argument from Members.Count. This must stay RED until P3.5 rewires
        // ShimStub/ConsumerDataSubscription/DataSubscriptionAdapter to re-raise p.PartySize.
        [Fact]
        public void A5b_AllianceGathering_PartySizeDistinctFromMemberCount_PendingP3()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var op = new OpTypedConsumerDouble();
            op.Attach(shim);

            var bus = host.Bus!;
            var allianceRoster = Enumerable.Range(1, 24).Select(i => (uint)i).ToList();
            bus.Emit(new PartyChanged(1, Flow.T0, allianceRoster, PartySize: 8));

            // The 24-member alliance roster crosses intact regardless of the gate's outcome.
            Assert.Equal(24, op.PartyList!.Count);

            // RED today: op.PartySize reports 24 (Members.Count) instead of the true 8-person party
            // size — ShimStub.cs:35 ignores PartyChanged.PartySize entirely.
            Assert.Equal(8, op.PartySize);
        }
    }
}
