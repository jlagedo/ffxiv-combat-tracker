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

        private sealed record PeerPing(string Source, int Value);
    }
}
