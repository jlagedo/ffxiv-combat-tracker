using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Cross-plugin registry interop both ways (A1, B3).</summary>
    public sealed class RegistryFlowTests
    {
        // A1 — native InvokeCallback → Triggernometry named callback (ProxyPlugin.cs:37-77).
        [Fact]
        public void A1_NativeInvoke_ReachesLegacyNamedCallback()
        {
            var registry = new InMemoryRegistry();
            var host = new FakePluginHost(plugins: registry);
            using var shim = new ShimStub(host);

            object? received = null;
            // Trig registers a named callback through the shim.
            using var reg = shim.RegisterNamedCallback("trig.spawn", arg => received = arg);

            // A native plugin invokes it via the registry.
            host.Plugins.InvokeCallback("trig.spawn", "payload-42");

            Assert.Equal("payload-42", received);
        }

        // B3 — legacy plugin publishes a typed event → native Subscribe<T> consumer (peer interop).
        [Fact]
        public void B3_LegacyPublish_ReachesNativeSubscriber()
        {
            var registry = new InMemoryRegistry();
            var host = new FakePluginHost(plugins: registry);

            PeerPing? seen = null;
            using var sub = host.Plugins.Subscribe<PeerPing>(p => seen = p);

            // The legacy peer publishes onto the shared bus.
            host.Plugins.Publish(new PeerPing("cactbot", 7));

            Assert.NotNull(seen);
            Assert.Equal("cactbot", seen!.Source);
            Assert.Equal(7, seen.Value);
        }

        // Unregister via the returned IDisposable stops further invocations (replaces int-id unregister).
        [Fact]
        public void NamedCallback_Dispose_Unregisters()
        {
            var registry = new InMemoryRegistry();
            int count = 0;
            var reg = registry.RegisterCallback("x", _ => count++);

            registry.InvokeCallback("x");
            reg.Dispose();
            registry.InvokeCallback("x");

            Assert.Equal(1, count);
        }

        // G5 — richer named-callback semantics: a duplicate name is rejected unless the registrant
        // opts in, and owner tags flow through (RealPlugin.cs:4176-4249 tracks owner + allowDuplicate).
        [Fact]
        public void G5_NamedCallback_OwnerAndDuplicatePolicy()
        {
            var registry = new InMemoryRegistry();
            var ownerA = new object();
            var ownerB = new object();

            using var first = registry.RegisterCallback("trig.named", _ => { }, owner: ownerA);

            // A second registration of the same name is rejected by default.
            Assert.Throws<System.InvalidOperationException>(
                () => registry.RegisterCallback("trig.named", _ => { }, owner: ownerB));

            // ...but allowed (and fanned out) when the registrant opts in.
            int hits = 0;
            using var dupe = registry.RegisterCallback("trig.named", _ => hits++, owner: ownerB, allowDuplicate: true);
            registry.InvokeCallback("trig.named");
            Assert.Equal(1, hits);
        }

        // G6 — a native consumer obtains a live, typed handle to a Started peer's published service
        // (models Triggernometry's BridgeFFXIV reaching the parser peer, ProxyPlugin.cs:431-477).
        [Fact]
        public void G6_PeerService_TypedHandle()
        {
            var registry = new InMemoryRegistry();
            registry.RegisterPeerService<IPeerParser>("ffxiv.parser", new PeerParserDouble());

            Assert.True(registry.TryGetPeerService<IPeerParser>("ffxiv.parser", out var parser));
            Assert.Equal("started", parser.Status);

            Assert.False(registry.TryGetPeerService<IPeerParser>("not.loaded", out _));
        }

        private sealed record PeerPing(string Source, int Value);

        private interface IPeerParser { string Status { get; } }
        private sealed class PeerParserDouble : IPeerParser { public string Status => "started"; }
    }
}
