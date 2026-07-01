using System;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D3: the ACT named-callback surface on <see cref="FormActMain"/> forwards to the modern
/// <c>IPluginRegistry</c> (Triggernometry peer interop), with G5 owner/duplicate semantics.
/// </summary>
public class RegistryTests
{
    [Fact]
    public void Named_callback_round_trips_through_the_registry()
    {
        var form = new FormActMain(new FakePluginHost(plugins: new InMemoryRegistry()));

        object? received = null;
        using var reg = form.RegisterNamedCallback("trig.cb", o => received = o);
        form.InvokeNamedCallback("trig.cb", "payload");

        Assert.Equal("payload", received);
    }

    [Fact]
    public void Duplicate_names_are_rejected_unless_opted_in()
    {
        var form = new FormActMain(new FakePluginHost(plugins: new InMemoryRegistry()));

        using var first = form.RegisterNamedCallback("dup", _ => { });
        Assert.Throws<InvalidOperationException>(() => form.RegisterNamedCallback("dup", _ => { }));

        // Opting in on both registrations allows the duplicate name.
        using var a = form.RegisterNamedCallback("multi", _ => { }, allowDuplicate: true);
        using var b = form.RegisterNamedCallback("multi", _ => { }, allowDuplicate: true);
    }

    [Fact]
    public void Disposing_the_handle_unregisters()
    {
        var form = new FormActMain(new FakePluginHost(plugins: new InMemoryRegistry()));

        int count = 0;
        var reg = form.RegisterNamedCallback("x", _ => count++);
        form.InvokeNamedCallback("x");
        reg.Dispose();
        form.InvokeNamedCallback("x");

        Assert.Equal(1, count);
    }
}
