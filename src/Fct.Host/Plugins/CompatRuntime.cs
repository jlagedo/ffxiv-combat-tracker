using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Plugins;

/// <summary>
/// The default-ALC side of the opt-in legacy compat runtime. The net10 compat shim and its two
/// legacy-impersonating facades (<c>Advanced Combat Tracker</c> / <c>FFXIV_ACT_Plugin.Common</c>) ship
/// as a staged package under <c>compat\</c> next to the host — deliberately <b>not</b> baked into
/// <c>Fct.App</c>'s static graph / <c>deps.json</c>. <see cref="PluginLoadContext.IsShared"/> routes
/// those names to the default context for a single type identity; this hook is what lets the default
/// context <i>find</i> them once they are no longer on the TPA list. It also serves the reflective
/// <c>LegacyPluginHostFactory</c>'s <see cref="AssemblyLoadContext.LoadFromAssemblyName"/> of the shim.
/// </summary>
internal static class CompatRuntime
{
    private static int _enabled;

    /// <summary>
    /// Subscribes a one-shot <see cref="AssemblyLoadContext.Default"/> resolver that satisfies default-
    /// context binds of the staged compat assemblies from <paramref name="compatDir"/> by simple name.
    /// Idempotent: only the first call subscribes. Must run before any plugin ALC loads or the
    /// reflective legacy factory runs (both are lazy under <c>host.Start()</c>, so calling from the
    /// composition root is early enough).
    /// </summary>
    public static void Enable(string compatDir, ILoggerFactory loggerFactory)
    {
        if (Interlocked.Exchange(ref _enabled, 1) != 0) return;

        var log = loggerFactory.CreateLogger("Fct.Host.Plugins.CompatRuntime");

        if (!Directory.Exists(compatDir))
        {
            // Loud, early failure: a mis-staged build would otherwise surface as an opaque
            // "could not load Fct.Compat.Shim" plugin quarantine much later.
            log.LogWarning(LogEvents.CompatRuntimeStaging,
                "Compat runtime directory not found at {CompatDir}; legacy (shim) plugins will fail to load",
                compatDir);
            return;
        }

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (name.Name is null) return null;
            var path = Path.Combine(compatDir, name.Name + ".dll");
            if (!File.Exists(path)) return null;   // not ours — let other resolvers try

            log.LogDebug(LogEvents.CompatRuntimeStaging, "Resolving {Assembly} from compat runtime", name.Name);
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        };

        log.LogInformation(LogEvents.CompatRuntimeStaging, "Compat runtime enabled from {CompatDir}", compatDir);
    }
}
