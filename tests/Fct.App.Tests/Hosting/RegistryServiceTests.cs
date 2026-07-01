using System;
using Fct.Abstractions;
using Fct.App.Hosting;
using Xunit;

namespace Fct.App.Tests.Hosting;

public class RegistryServiceTests
{
    [Fact]
    public void Named_callback_fans_out_and_dispose_unregisters()
    {
        var registry = new RegistryService();
        object? seen = null;
        var handle = registry.RegisterCallback("cb", a => seen = a);

        registry.InvokeCallback("cb", 42);
        Assert.Equal(42, seen);

        handle.Dispose();
        seen = null;
        registry.InvokeCallback("cb", 99);
        Assert.Null(seen);
    }

    [Fact]
    public void Duplicate_name_rejected_unless_opted_in()
    {
        var registry = new RegistryService();
        registry.RegisterCallback("cb", _ => { });

        Assert.Throws<InvalidOperationException>(() => registry.RegisterCallback("cb", _ => { }));

        var second = registry.RegisterCallback("cb", _ => { }, allowDuplicate: true);
        Assert.NotNull(second);
    }

    [Fact]
    public void Typed_publish_reaches_subscriber()
    {
        var registry = new RegistryService();
        string? got = null;
        using var _ = registry.Subscribe<string>(s => got = s);

        registry.Publish("hello");

        Assert.Equal("hello", got);
    }

    [Fact]
    public void Roster_reflects_loaded_plugins()
    {
        var registry = new RegistryService();
        Assert.Empty(registry.LoadedPlugins);

        registry.SetRoster(new[] { new PluginInfo("a", "1.0", "1.0") });

        Assert.Single(registry.LoadedPlugins);
        Assert.Equal("a", registry.LoadedPlugins[0].Id);
    }

    [Fact]
    public void Peer_service_handle_is_type_gated()
    {
        var registry = new RegistryService();
        Assert.False(registry.TryGetPeerService<IFormattable>("peer", out _));

        registry.RegisterPeerService("peer", (IFormattable)123);

        Assert.True(registry.TryGetPeerService<IFormattable>("peer", out var svc));
        Assert.NotNull(svc);
    }
}
