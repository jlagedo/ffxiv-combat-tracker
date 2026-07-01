using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using FFXIV_ACT_Plugin.Common;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D7: after a shimmed plugin loads, <c>LegacyPluginHost</c> publishes a synthetic FFXIV_ACT_Plugin
/// stand-in into <c>ActGlobals.oFormActMain.ActPlugins</c>. This exercises OverlayPlugin's exact
/// discovery path — scan the roster by title, then reflect the public <c>DataRepository</c>/
/// <c>DataSubscription</c> properties off <c>pluginObj</c> — and asserts both resolve to the projected
/// SDK surfaces.
/// </summary>
public class DiscoveryTests
{
    private const string EntryTypeName = "Fct.SampleLegacyPlugin.SampleLegacyPlugin";

    private static string SampleDll => Path.Combine(
        AppContext.BaseDirectory, "plugins", "Fct.SampleLegacyPlugin", "Fct.SampleLegacyPlugin.dll");

    [Fact]
    public async Task Publishes_a_reflectable_ffxiv_plugin_standin()
    {
        Assert.True(File.Exists(SampleDll), $"sample legacy plugin not staged at {SampleDll}");
        ActGlobals.oFormActMain = null;

        var assembly = Assembly.LoadFrom(SampleDll);
        var host = new FakePluginHost();
        var legacyHost = new LegacyPluginHost(assembly, EntryTypeName);
        await legacyHost.InitializeAsync(host, CancellationToken.None);

        // OverlayPlugin's discovery: find the FFXIV plugin by title, read DataRepository off pluginObj.
        var ffxiv = ActGlobals.oFormActMain!.ActPlugins.Single(
            p => p.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
        Assert.Contains("FFXIV_ACT_Plugin", ffxiv.pluginFile.Name);   // Hojoring's file-name match

        var repo = ffxiv.pluginObj.GetType().GetProperty("DataRepository")!.GetValue(ffxiv.pluginObj);
        var sub = ffxiv.pluginObj.GetType().GetProperty("DataSubscription")!.GetValue(ffxiv.pluginObj);

        Assert.IsAssignableFrom<IDataRepository>(repo);
        Assert.IsAssignableFrom<IDataSubscription>(sub);

        await legacyHost.DisposeAsync();
    }
}
