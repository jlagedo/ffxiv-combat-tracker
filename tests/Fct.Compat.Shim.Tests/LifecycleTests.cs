using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D1: the shim's <see cref="LegacyPluginHost"/> discovers a recompiled legacy plugin's
/// <c>IActPluginV1</c>, wires the shared <c>ActGlobals.oFormActMain</c>, and bridges the ACT
/// lifecycle (InitPlugin ⇄ InitializeAsync, DeInitPlugin ⇄ DisposeAsync). The sample legacy plugin
/// is loaded from its staged folder, exactly as the host loads it.
/// </summary>
public class LifecycleTests
{
    private const string EntryTypeName = "Fct.SampleLegacyPlugin.SampleLegacyPlugin";

    private static string SampleDll => Path.Combine(
        AppContext.BaseDirectory, "plugins", "Fct.SampleLegacyPlugin", "Fct.SampleLegacyPlugin.dll");

    [Fact]
    public async Task Drives_the_legacy_plugin_init_and_deinit_lifecycle()
    {
        Assert.True(File.Exists(SampleDll), $"sample legacy plugin not staged at {SampleDll}");

        // Fresh shared hub for this test (the sole toucher of the process-global static).
        ActGlobals.oFormActMain = null;

        var assembly = Assembly.LoadFrom(SampleDll);
        var type = assembly.GetType(EntryTypeName, throwOnError: true)!;
        var initField = type.GetField("InitCount", BindingFlags.Public | BindingFlags.Static)!;
        var deinitField = type.GetField("DeInitCount", BindingFlags.Public | BindingFlags.Static)!;
        initField.SetValue(null, 0);
        deinitField.SetValue(null, 0);

        var host = new FakePluginHost();
        var legacyHost = new LegacyPluginHost(assembly, EntryTypeName);

        await legacyHost.InitializeAsync(host, CancellationToken.None);

        // InitPlugin ran exactly once, and the shared hub is now bound to the modern host.
        Assert.Equal(1, (int)initField.GetValue(null)!);
        Assert.NotNull(ActGlobals.oFormActMain);
        Assert.Same(host, ActGlobals.oFormActMain.Host);

        // The plugin was registered in ActPlugins and received (and wrote to) its status label.
        var data = Assert.Single(ActGlobals.oFormActMain.ActPlugins);
        Assert.Same(legacyHost.Plugin, data.pluginObj);
        Assert.Equal("Sample legacy plugin online", data.lblPluginStatus.Text);
        Assert.Equal("Fct.SampleLegacyPlugin.dll", data.pluginFile.Name);

        await legacyHost.DisposeAsync();

        // DeInitPlugin ran and the plugin was removed from the roster.
        Assert.Equal(1, (int)deinitField.GetValue(null)!);
        Assert.Empty(ActGlobals.oFormActMain.ActPlugins);
    }

    [Fact]
    public async Task Rejects_a_legacy_entry_that_is_not_an_IActPluginV1()
    {
        var host = new FakePluginHost();
        // A type that exists in this assembly but is not an IActPluginV1.
        var legacyHost = new LegacyPluginHost(typeof(LifecycleTests).Assembly, typeof(LifecycleTests).FullName!);

        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => legacyHost.InitializeAsync(host, CancellationToken.None));
    }
}
