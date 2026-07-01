using System;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>The raw-log-line lifeline for regex engines (A3).</summary>
    public sealed class RawLogLineFlowTests
    {
        // A3 — native-emitted RawLogLine → Triggernometry/cactbot regex (Trigger.cs:615;
        // FFXIVOptionalEventSource.cs:57). Typed events must NOT reach the line-regex path.
        [Fact]
        public void A3_RawLogLine_ReachesRegexEngine_TypedFilteredOut()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var trig = new TrigDouble(@"You use (?<skill>.+)\.");
            trig.Attach(shim);

            var bus = host.Bus!;
            // A typed event: never surfaces on the raw-line regex path.
            bus.Emit(new ZoneChanged(1, Flow.T0, 132, "Limsa"));
            // A matching raw line.
            bus.Emit(Flow.Line("You use Fire IV.", seq: 2));
            // A non-matching raw line.
            bus.Emit(Flow.Line("The attack misses.", seq: 3));

            Assert.Equal(new[] { "You use Fire IV." }, trig.Fired);
        }

        // The event bus honors GameEventFilter: a typed-only filter drops the RawLogLine firehose.
        [Fact]
        public void Bus_Filter_DropsRawLogLines_WhenExcluded()
        {
            var bus = new InMemoryEventBus();
            int raw = 0, typed = 0;
            var filter = new GameEventFilter(Types: new[] { typeof(ZoneChanged) }, IncludeRawLogLines: false);
            using var sub = bus.Subscribe(filter, e =>
            {
                if (e is RawLogLine) raw++; else typed++;
            });

            bus.Emit(Flow.Line("ignored", seq: 1));
            bus.Emit(new ZoneChanged(2, Flow.T0, 1, "z"));

            Assert.Equal(0, raw);
            Assert.Equal(1, typed);
        }

        // A throwing handler is isolated: it neither kills the stream nor starves a peer subscriber.
        [Fact]
        public void Bus_ThrowingHandler_IsIsolated()
        {
            var bus = new InMemoryEventBus();
            using var bad = bus.Subscribe(GameEventFilter.All, _ => throw new InvalidOperationException("boom"));
            int good = 0;
            using var ok = bus.Subscribe(GameEventFilter.All, _ => good++);

            bus.Emit(Flow.Line("line", seq: 1));
            bus.Emit(Flow.Line("line", seq: 2));

            Assert.Equal(2, good);
        }
    }
}
