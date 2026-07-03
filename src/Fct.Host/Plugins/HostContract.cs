using System;

namespace Fct.Host.Plugins;

/// <summary>
/// The contract version the host implements. A plugin's manifest declares the <c>contract</c> it was
/// built against; the loader gates on a matching <b>major</b> version (additive minor bumps stay
/// compatible, per <c>Fct.Abstractions</c> semver).
/// </summary>
internal static class HostContract
{
    public const string Version = "1.0";

    /// <summary>True when a plugin built against <paramref name="pluginContract"/> is loadable here.</summary>
    public static bool Accepts(string? pluginContract)
    {
        if (string.IsNullOrWhiteSpace(pluginContract)) return false;
        return Major(pluginContract) == Major(Version);
    }

    private static int Major(string version)
    {
        var dot = version.IndexOf('.');
        var head = dot >= 0 ? version.AsSpan(0, dot) : version.AsSpan();
        return int.TryParse(head, out var major) ? major : -1;
    }
}
