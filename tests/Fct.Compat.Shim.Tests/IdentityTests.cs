using System;
using Advanced_Combat_Tracker;
using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Common.Models;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D0: the two facades carry the legacy assembly identities a recompiled plugin binds to, and the
/// shim's <c>FormActMain</c> is a POCO over the modern host (not a WinForms Form).
/// </summary>
public class IdentityTests
{
    [Fact]
    public void ActFacade_impersonates_Advanced_Combat_Tracker()
    {
        var asm = typeof(IActPluginV1).Assembly.GetName();
        Assert.Equal(ShimInfo.ActFacadeAssembly, asm.Name);
        Assert.Equal(new Version(3, 8, 5, 288), asm.Version);
        // The whole ACT surface a plugin compiles against lives in that one assembly.
        Assert.Equal(ShimInfo.ActFacadeAssembly, typeof(ActGlobals).Assembly.GetName().Name);
        Assert.Equal(ShimInfo.ActFacadeAssembly, typeof(FormActMain).Assembly.GetName().Name);
        Assert.Equal(ShimInfo.ActFacadeAssembly, typeof(Advanced_Combat_Tracker.LogLineEventArgs).Assembly.GetName().Name);
    }

    [Fact]
    public void SdkFacade_impersonates_FFXIV_ACT_Plugin_Common()
    {
        var asm = typeof(IDataSubscription).Assembly.GetName();
        Assert.Equal(ShimInfo.SdkFacadeAssembly, asm.Name);
        Assert.Equal(new Version(3, 0, 0, 0), asm.Version);
        Assert.Equal(ShimInfo.SdkFacadeAssembly, typeof(IDataRepository).Assembly.GetName().Name);
        Assert.Equal(ShimInfo.SdkFacadeAssembly, typeof(Combatant).Assembly.GetName().Name);
        Assert.Equal(ShimInfo.SdkFacadeAssembly, typeof(Player).Assembly.GetName().Name);
        Assert.Equal(ShimInfo.SdkFacadeAssembly, typeof(NetworkBuff).Assembly.GetName().Name);
    }

    [Fact]
    public void FormActMain_is_a_poco_over_the_modern_host()
    {
        // NOT a WinForms Form — the whole point of the net10 re-projection.
        Assert.False(typeof(System.Windows.Forms.Form).IsAssignableFrom(typeof(FormActMain)));

        var host = new FakePluginHost();
        var form = new FormActMain(host);

        Assert.Same(host, form.Host);
        Assert.Equal(new Version(3, 8, 5, 288), form.GetVersion());
        Assert.Empty(form.ActPlugins);
        Assert.Equal(1f, form.DpiScale);
        Assert.True(form.InitActDone);
    }

    [Fact]
    public void PluginGetSelfData_resolves_by_identity_and_through_alias()
    {
        var host = new FakePluginHost();
        var form = new FormActMain(host);

        var plain = new FakeActPlugin();
        var inner = new FakeActPlugin();
        var wrapper = new FakeAliasPlugin(inner);

        var plainData = new ActPluginData { pluginObj = plain };
        var wrapperData = new ActPluginData { pluginObj = wrapper };
        form.ActPlugins.Add(plainData);
        form.ActPlugins.Add(wrapperData);

        Assert.Same(plainData, form.PluginGetSelfData(plain));
        // A wrapped plugin looking itself up by `this` resolves through IActPluginAlias.Inner.
        Assert.Same(wrapperData, form.PluginGetSelfData(inner));
        Assert.Null(form.PluginGetSelfData(new FakeActPlugin()));
    }

    private sealed class FakeActPlugin : IActPluginV1
    {
        public void InitPlugin(System.Windows.Forms.TabPage pluginScreenSpace, System.Windows.Forms.Label pluginStatusText) { }
        public void DeInitPlugin() { }
    }

    private sealed class FakeAliasPlugin : IActPluginV1, IActPluginAlias
    {
        public FakeAliasPlugin(IActPluginV1 inner) => Inner = inner;
        public IActPluginV1 Inner { get; }
        public void InitPlugin(System.Windows.Forms.TabPage pluginScreenSpace, System.Windows.Forms.Label pluginStatusText) { }
        public void DeInitPlugin() { }
    }
}
