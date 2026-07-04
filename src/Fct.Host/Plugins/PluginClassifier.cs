using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Fct.Host.Plugins;

/// <summary>The outcome of classifying an installed plugin directory: how to host it, plus the
/// identity + entry type the loader needs (entry is null for real-legacy — the satellite discovers it).</summary>
internal sealed record PluginClassification(LoadKind Kind, string Id, string Version, string? EntryTypeName, string AssemblyFile);

/// <summary>
/// Decides how a plugin must be hosted — <b>manifest-first, detect-if-absent</b>. When a
/// <c>plugin.json</c> is present it is authoritative (preserving the "manifest, not reflection-by-title"
/// contract). Otherwise the entry assembly is inspected <b>without executing it</b> via
/// <see cref="MetadataLoadContext"/>: the referenced <c>Advanced Combat Tracker</c> identity (real
/// strong-name vs the shim facade) and the implemented plugin interface decide the kind, and identity
/// is synthesized from the assembly name/version.
/// </summary>
internal sealed class PluginClassifier
{
    // The real ACT's strong-name public-key token (see CLAUDE.md). A plugin that references
    // "Advanced Combat Tracker" under this token is a real net48 legacy plugin; anything else that
    // references that assembly name is bound to our (unsigned) shim facade.
    private const string RealActToken = "a946b61e93d97868";

    private const string ActAssemblyName = "Advanced Combat Tracker";
    private const string AbstractionsName = "Fct.Abstractions";
    private const string PluginInterface = "Fct.Abstractions.IPlugin";
    private const string ActPluginInterface = "Advanced_Combat_Tracker.IActPluginV1";

    /// <summary>Classify the plugin in <paramref name="pluginDir"/>. When <paramref name="manifest"/>
    /// is non-null it is trusted; otherwise the directory's assemblies are inspected.</summary>
    public PluginClassification Classify(string pluginDir, PluginManifest? manifest)
    {
        if (manifest is not null) return FromManifest(manifest);

        var dlls = Directory.GetFiles(pluginDir, "*.dll");
        if (dlls.Length == 0)
            throw new InvalidOperationException($"No assemblies found in '{pluginDir}'.");

        using var mlc = new MetadataLoadContext(BuildResolver(dlls));

        foreach (var dll in dlls)
        {
            Assembly asm;
            try { asm = mlc.LoadFromAssemblyPath(dll); }
            catch { continue; }
            if (TryInspect(asm, dll, out var c)) return c;
        }

        throw new InvalidOperationException(
            $"No plugin entry assembly found in '{pluginDir}' (no type implementing IPlugin or IActPluginV1).");
    }

    /// <summary>Classify a single DLL the user picked directly (e.g. <c>FFXIV_ACT_Plugin.dll</c> from the
    /// ACT install, whose folder holds many unrelated plugins). Only that DLL is inspected; its siblings
    /// are made resolvable so its metadata reads, but they are not themselves classified.</summary>
    public PluginClassification ClassifyFile(string dllPath, PluginManifest? manifest)
    {
        if (manifest is not null) return FromManifest(manifest);

        var siblings = Directory.GetFiles(Path.GetDirectoryName(dllPath) ?? ".", "*.dll");
        using var mlc = new MetadataLoadContext(BuildResolver(siblings));
        Assembly asm;
        try { asm = mlc.LoadFromAssemblyPath(dllPath); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not read '{dllPath}' as a .NET assembly.", ex); }

        if (TryInspect(asm, dllPath, out var c)) return c;
        throw new InvalidOperationException(
            $"'{dllPath}' is not a plugin (no type implementing IPlugin or IActPluginV1).");
    }

    private static PluginClassification FromManifest(PluginManifest manifest)
    {
        var kind = manifest.LegacyEntry is not null ? LoadKind.RecompiledShim : LoadKind.Native;
        var entry = manifest.LegacyEntry ?? manifest.Entry;
        return new PluginClassification(kind, manifest.Id, manifest.Version, entry, manifest.Assembly);
    }

    // Inspect one already-loaded (metadata-only) assembly: the referenced "Advanced Combat Tracker"
    // identity (real strong-name vs shim facade) and the implemented plugin interface decide the kind.
    private bool TryInspect(Assembly asm, string dll, [NotNullWhen(true)] out PluginClassification? result)
    {
        result = null;
        var refs = asm.GetReferencedAssemblies();

        var actRef = refs.FirstOrDefault(r => r.Name == ActAssemblyName);
        if (actRef is not null)
        {
            var token = TokenHex(actRef.GetPublicKeyToken());
            bool isReal = string.Equals(token, RealActToken, StringComparison.OrdinalIgnoreCase);
            var kind = isReal ? LoadKind.RealLegacy : LoadKind.RecompiledShim;
            // Real-legacy: don't resolve its (net48, absent) type graph — the satellite finds the
            // entry type at load. Shim: the facade is in the app dir, so the entry type resolves.
            var entry = isReal ? null : FindEntryType(asm, ActPluginInterface);
            result = Synthesize(kind, asm, dll, entry);
            return true;
        }

        if (refs.Any(r => r.Name == AbstractionsName))
        {
            var entry = FindEntryType(asm, PluginInterface);
            if (entry is not null)
            {
                result = Synthesize(LoadKind.Native, asm, dll, entry);
                return true;
            }
        }

        return false;
    }

    private static PluginClassification Synthesize(LoadKind kind, Assembly asm, string dll, string? entry)
    {
        var name = asm.GetName();
        var id = (name.Name ?? Path.GetFileNameWithoutExtension(dll)).ToLowerInvariant();
        var version = name.Version?.ToString() ?? "0.0.0";
        return new PluginClassification(kind, id, version, entry, Path.GetFileName(dll));
    }

    // The full name of the first public, concrete type implementing the given interface, or null.
    private static string? FindEntryType(Assembly asm, string interfaceFullName)
    {
        foreach (var type in SafeGetTypes(asm))
        {
            if (type is null || !type.IsPublic || type.IsAbstract || type.IsInterface) continue;
            Type[] ifaces;
            try { ifaces = type.GetInterfaces(); }
            catch { continue; }
            if (ifaces.Any(i => i.FullName == interfaceFullName))
                return type.FullName;
        }
        return null;
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types; }
        catch { return Array.Empty<Type?>(); }
    }

    // Resolver over the plugin's own DLLs plus the runtime + app dirs (so IPlugin / the shim facade /
    // the core library resolve). Deduped by simple name — PathAssemblyResolver rejects duplicates.
    private static PathAssemblyResolver BuildResolver(IEnumerable<string> pluginDlls)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Plugin DLLs win over runtime/app copies of the same simple name.
        foreach (var dll in pluginDlls)
            byName[Path.GetFileNameWithoutExtension(dll)] = dll;
        foreach (var dir in new[] { AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory() })
        {
            foreach (var dll in SafeEnumerateDlls(dir))
            {
                var key = Path.GetFileNameWithoutExtension(dll);
                if (!byName.ContainsKey(key)) byName[key] = dll;
            }
        }
        return new PathAssemblyResolver(byName.Values);
    }

    private static IEnumerable<string> SafeEnumerateDlls(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll"); }
        catch { return Array.Empty<string>(); }
    }

    private static string TokenHex(byte[]? token)
        => token is null || token.Length == 0 ? "" : string.Concat(token.Select(b => b.ToString("x2")));
}
