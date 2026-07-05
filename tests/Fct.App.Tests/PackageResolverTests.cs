using Fct.Bridge;
using Fct.Host.Plugins;
using Xunit;

namespace Fct.App.Tests;

// ISOLATION-PLAN P7.1: the install-catalog seam that maps a classified real-legacy plugin to the
// satellite package (identity + role + downstream subscription set) that hosts it. Pure + plugin-free.
public sealed class PackageResolverTests
{
    [Fact]
    public void Parser_resolves_to_the_producer_ffxiv_package_with_no_subscriptions()
    {
        var d = PackageResolver.Resolve("FFXIV_ACT_Plugin.dll", "ffxiv_act_plugin", "FFXIV_ACT_Plugin");
        Assert.Equal("ffxiv", d.Package);
        Assert.Equal(SatelliteRole.Producer, d.Role);
        Assert.Empty(d.Subscriptions);   // the producer forwards; it never subscribes
    }

    [Fact]
    public void Triggernometry_resolves_to_a_consumer_subscribing_swings_and_rawlog()
    {
        var d = PackageResolver.Resolve("Triggernometry.dll", "triggernometry", "Triggernometry");
        Assert.Equal("triggernometry", d.Package);
        Assert.Equal(SatelliteRole.Consumer, d.Role);
        Assert.Contains(SatelliteProtocol.StreamSwings, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamRawLog, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamRepository, d.Subscriptions);
    }

    [Fact]
    public void Discord_triggers_resolves_to_a_consumer_package()
    {
        var d = PackageResolver.Resolve("ACT_DiscordTriggers.dll", "act_discordtriggers", "Discord Triggers");
        Assert.Equal("discord", d.Package);
        Assert.Equal(SatelliteRole.Consumer, d.Role);
        Assert.Contains(SatelliteProtocol.StreamRawLog, d.Subscriptions);
    }

    [Fact]
    public void OverlayPlugin_resolves_to_a_consumer_subscribing_the_full_set_including_packets()
    {
        var d = PackageResolver.Resolve("OverlayPlugin.dll", "overlay", "OverlayPlugin");
        Assert.Equal("overlay", d.Package);
        Assert.Equal(SatelliteRole.Consumer, d.Role);
        // The P8-distinguishing subscription: OverlayPlugin's NetworkProcessors are the first consumer to
        // need the raw-packet firehose.
        Assert.Contains(SatelliteProtocol.StreamPackets, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamSwings, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamRawLog, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamZoneParty, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamCombatants, d.Subscriptions);
        Assert.Contains(SatelliteProtocol.StreamRepository, d.Subscriptions);
    }

    [Fact]
    public void An_unknown_legacy_plugin_gets_its_own_isolated_consumer_satellite()
    {
        var d = PackageResolver.Resolve("SomeOtherPlugin.dll", "someotherplugin", "Some Other Plugin");
        Assert.Equal("someotherplugin", d.Package);   // its own identity, not shared
        Assert.Equal(SatelliteRole.Consumer, d.Role);
        Assert.NotEmpty(d.Subscriptions);
    }

    [Fact]
    public void Resolution_matches_on_any_stable_signal_and_ignores_case()
    {
        // Title-only (a manifest-named install where the id is a raw assembly name) still resolves.
        Assert.Equal("triggernometry", PackageResolver.Resolve(null, null, "Triggernometry").Package);
        // Assembly-file-only, mixed case.
        Assert.Equal("discord", PackageResolver.Resolve("act_discordtriggers.DLL", null, null).Package);
        Assert.Equal("ffxiv", PackageResolver.Resolve("ffxiv_act_plugin.dll", null, null).Package);
    }
}
