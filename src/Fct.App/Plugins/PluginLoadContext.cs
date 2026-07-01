using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Fct.App.Plugins;

/// <summary>
/// One collectible <see cref="AssemblyLoadContext"/> per plugin: assembly/version isolation +
/// hot-unload. The plugin's private dependencies (its own Newtonsoft, etc.) resolve through its
/// <c>.deps.json</c> via <see cref="AssemblyDependencyResolver"/>; the contract + UI + logging + UI
/// framework assemblies are <b>shared up to the default context</b> so their types have one identity
/// across the host↔plugin boundary. No strong-name impersonation, no <c>AssemblyResolve</c> games.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath)
        : base(name: $"plugin:{System.IO.Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared → return null so the DEFAULT context resolves it (single type identity).
        if (IsShared(assemblyName.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>
    /// Assemblies on the plugin-facing surface (or its transitive closure) that must have a single
    /// identity across the boundary: the contract, the UI contract + Avalonia, and the logging
    /// abstraction the <c>IPluginHost.Logger</c> type comes from.
    /// </summary>
    internal static bool IsShared(string? name)
    {
        if (name is null) return false;
        return name == "Fct.Abstractions"
            || name == "Fct.Abstractions.UI"
            || name == "Microsoft.Extensions.Logging.Abstractions"
            || name.StartsWith("Avalonia", StringComparison.Ordinal);
    }
}
