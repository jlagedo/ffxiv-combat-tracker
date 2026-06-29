using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FFXIV_ACT_Plugin.Common;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Parser.Legacy.Tests
{
    public class RingBufferDataSubscriptionTests
    {
        private readonly ITestOutputHelper _out;
        public RingBufferDataSubscriptionTests(ITestOutputHelper o) => _out = o;

        private static bool WaitFor(Func<bool> cond, int ms = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(2);
            }
            return cond();
        }

        [Fact]
        public void Injected_packets_deliver_in_order()
        {
            using var ring = new RingBufferDataSubscription();
            var got = new List<long>();
            ring.NetworkReceived += (c, e, m) => { lock (got) got.Add(e); };

            for (long i = 0; i < 500; i++)
                ring.InjectNetworkReceived("c", i, new byte[] { (byte)i });

            Assert.True(WaitFor(() => { lock (got) return got.Count == 500; }), "did not receive all 500");
            lock (got)
                for (int i = 0; i < 500; i++)
                    Assert.Equal(i, got[i]); // strict order preserved
            Assert.Equal(0, ring.DroppedCount);
        }

        [Fact]
        public void Upstream_events_deliver_in_order_across_kinds()
        {
            using var ring = new RingBufferDataSubscription();
            var fake = new FakeDataSubscription();
            ring.AttachUpstream(fake);

            var seq = new List<string>();
            ring.NetworkReceived += (c, e, m) => { lock (seq) seq.Add("N" + e); };
            ring.LogLine += (t, s, l) => { lock (seq) seq.Add("L" + l); };
            ring.ZoneChanged += (id, n) => { lock (seq) seq.Add("Z" + n); };

            // Interleave three event kinds; the ring must preserve global arrival order.
            var expected = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                switch (i % 3)
                {
                    case 0: fake.RaiseNetworkReceived("c", i, new byte[0]); expected.Add("N" + i); break;
                    case 1: fake.RaiseLogLine(0, 0, i.ToString()); expected.Add("L" + i); break;
                    case 2: fake.RaiseZoneChanged(0, i.ToString()); expected.Add("Z" + i); break;
                }
            }

            Assert.True(WaitFor(() => { lock (seq) return seq.Count == expected.Count; }), "missing events");
            lock (seq) Assert.Equal(expected, seq);
        }

        [Fact]
        public void All_subscribers_invoked_once_in_registration_order()
        {
            using var ring = new RingBufferDataSubscription();
            var order = new List<int>();
            for (int s = 0; s < 5; s++)
            {
                int id = s;
                ring.NetworkReceived += (c, e, m) => { lock (order) order.Add(id); };
            }

            ring.InjectNetworkReceived("c", 0, new byte[0]);

            Assert.True(WaitFor(() => { lock (order) return order.Count == 5; }), "not all subscribers ran");
            lock (order) Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order); // registration order, once each
        }

        [Fact]
        public void Dispatch_runs_on_one_thread_distinct_from_producer()
        {
            using var ring = new RingBufferDataSubscription();
            var ids = new ConcurrentDictionary<int, byte>();
            ring.NetworkReceived += (c, e, m) => ids.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);

            int producer = Thread.CurrentThread.ManagedThreadId;
            for (long i = 0; i < 1000; i++)
                ring.InjectNetworkReceived("c", i, new byte[0]);

            Assert.True(WaitFor(() => ids.Count >= 1));
            Thread.Sleep(50);
            Assert.Single(ids);                 // exactly one dispatch thread
            Assert.DoesNotContain(producer, ids.Keys); // and it is not the producer
        }

        [Fact]
        public void Full_ring_drops_oldest_and_never_blocks_producer()
        {
            const int cap = 4;
            using var ring = new RingBufferDataSubscription(capacity: cap);

            var blocked = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            ring.NetworkReceived += (c, e, m) =>
            {
                if (!release.IsSet) { blocked.Set(); release.Wait(5000); }
            };

            // First packet parks the single dispatch thread inside the subscriber.
            ring.InjectNetworkReceived("c", 0, new byte[0]);
            Assert.True(blocked.Wait(2000), "dispatch never entered the subscriber");

            // With dispatch parked, flood cap + extra. Producer must not block; ring keeps `cap`.
            const int extra = 6;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < cap + extra; i++)
                ring.InjectNetworkReceived("c", 100 + i, new byte[0]);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000, $"producer blocked for {sw.ElapsedMilliseconds}ms");
            Assert.Equal(extra, ring.DroppedCount); // oldest `extra` dropped, newest `cap` kept

            release.Set();
        }

        [Fact]
        public void Bench_ring_vs_beginInvoke_baseline()
        {
            const int packets = 20_000;
            const int subscribers = 20;

            // Baseline: the real plugin's pattern — BeginInvoke per subscriber + reflected EndInvoke.
            Action<string, long, byte[]> sink = (c, e, m) => { };
            Action<string, long, byte[]> baseline = null;
            for (int i = 0; i < subscribers; i++) baseline += sink;
            var bsw = Stopwatch.StartNew();
            var pending = new List<IAsyncResult>(packets * subscribers);
            for (int p = 0; p < packets; p++)
                foreach (Action<string, long, byte[]> d in baseline.GetInvocationList())
                    pending.Add(d.BeginInvoke("c", p, null, null, null));
            foreach (var ar in pending) ar.AsyncWaitHandle.WaitOne();
            bsw.Stop();

            // Ring path: one enqueue per packet, single-thread in-order fan-out to all subscribers.
            // Capacity above the packet count → lossless, so this is a fair throughput comparison.
            using var ring = new RingBufferDataSubscription(capacity: 32_768);
            long count = 0;
            for (int i = 0; i < subscribers; i++)
                ring.NetworkReceived += (c, e, m) => Interlocked.Increment(ref count);
            var rsw = Stopwatch.StartNew();
            for (long p = 0; p < packets; p++)
                ring.InjectNetworkReceived("c", p, null);
            WaitFor(() => Interlocked.Read(ref count) >= (long)packets * subscribers, 10000);
            rsw.Stop();

            _out.WriteLine($"BeginInvoke baseline: {bsw.ElapsedMilliseconds} ms  ({packets}x{subscribers} dispatches)");
            _out.WriteLine($"Ring dispatcher:      {rsw.ElapsedMilliseconds} ms  (dropped={ring.DroppedCount})");
            Assert.Equal((long)packets * subscribers, Interlocked.Read(ref count));
        }
    }
}
