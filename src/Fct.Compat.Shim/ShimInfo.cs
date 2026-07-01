namespace Fct.Compat.Shim;

/// <summary>
/// Identities of the two legacy-impersonating facade assemblies the shim ships. These are the
/// assembly names a recompiled legacy plugin binds to, and the names the host's
/// <c>PluginLoadContext</c> shares to the default ALC so the shim and the plugin agree on type
/// identity. (The runtime adapter — <c>LegacyPluginHost</c> and the SDK mappers — lands in later slices.)
/// </summary>
public static class ShimInfo
{
    /// <summary>Assembly name of the ACT host facade (<c>ActGlobals</c>/<c>FormActMain</c>/…).</summary>
    public const string ActFacadeAssembly = "Advanced Combat Tracker";

    /// <summary>Assembly name of the FFXIV_ACT_Plugin SDK facade (<c>IDataSubscription</c>/models/…).</summary>
    public const string SdkFacadeAssembly = "FFXIV_ACT_Plugin.Common";

    /// <summary>Assembly name of this shim runtime.</summary>
    public const string ShimAssembly = "Fct.Compat.Shim";
}
