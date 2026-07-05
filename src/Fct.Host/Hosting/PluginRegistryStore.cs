using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fct.Host.Plugins;
using Fct.Logging;

namespace Fct.Host.Hosting;

/// <summary>One installed plugin, as persisted. Presence in the registry ⇒ installed ⇒ auto-loaded
/// on next launch. <see cref="Kind"/> is decided once at install so startup never re-classifies.</summary>
internal sealed record InstalledPluginRecord(string Id, LoadKind Kind, string Dir, string? Entry, string Version)
{
    /// <summary>Display title (for legacy rows announced to the satellite / notifications).</summary>
    public string? Title { get; init; }

    /// <summary>The entry assembly's file name (relative to <see cref="Dir"/>); used to re-send a
    /// real-legacy plugin to the satellite on restart.</summary>
    public string? AssemblyFile { get; init; }

    /// <summary>The satellite package this plugin runs in (ISOLATION-PLAN §2), for observability. Real
    /// routing derives it from <see cref="PackageResolver"/> at load time, so this is a back-fillable
    /// hint that can be absent on older records — never the routing authority.</summary>
    public string? Package { get; init; }
}

/// <summary>The on-disk registry document: the installed set plus any dirs whose deletion was
/// deferred because their files were still locked (collectible ALC / CEF) at unload time.</summary>
internal sealed class InstalledPlugins
{
    public List<InstalledPluginRecord> Plugins { get; set; } = new();
    public List<string> PendingDelete { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstalledPlugins))]
internal sealed partial class PluginRegistryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persists the installed-plugin set to <c>&lt;AppData.Root&gt;\installed-plugins.json</c>,
/// mirroring <see cref="UiSettingsStore"/> (best-effort load/save; a read failure falls back to empty).
/// This is the single source of truth for <b>installed</b> plugins across restarts; nothing is
/// auto-discovered from disk.
/// </summary>
internal sealed class PluginRegistryStore
{
    private readonly object _gate = new();
    private readonly string _filePath;

    /// <param name="filePathOverride">Test seam: overrides the registry file location.</param>
    public PluginRegistryStore(string? filePathOverride = null)
        => _filePath = filePathOverride ?? Path.Combine(AppData.Root, "installed-plugins.json");

    private string FilePath => _filePath;

    public InstalledPlugins Current { get; private set; } = new();

    public InstalledPlugins Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), PluginRegistryJsonContext.Default.InstalledPlugins)
                              ?? new InstalledPlugins();
            }
            catch { Current = new InstalledPlugins(); }
            return Current;
        }
    }

    /// <summary>Insert or replace the record for its id, then persist.</summary>
    public void Add(InstalledPluginRecord record)
    {
        lock (_gate)
        {
            Current.Plugins.RemoveAll(p => string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            Current.Plugins.Add(record);
            Save();
        }
    }

    /// <summary>Remove the record for <paramref name="id"/> (if any) and persist.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var n = Current.Plugins.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
        }
    }

    public InstalledPluginRecord? Find(string id)
    {
        lock (_gate)
            return Current.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<InstalledPluginRecord> All()
    {
        lock (_gate) return Current.Plugins.ToArray();
    }

    /// <summary>Record a directory whose files couldn't be deleted yet; retried at next startup.</summary>
    public void MarkPendingDelete(string dir)
    {
        lock (_gate)
        {
            if (!Current.PendingDelete.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                Current.PendingDelete.Add(dir);
                Save();
            }
        }
    }

    /// <summary>Delete every pending directory that is now free; drop the ones that succeed.</summary>
    public void ProcessPendingDeletes()
    {
        lock (_gate)
        {
            if (Current.PendingDelete.Count == 0) return;
            var remaining = new List<string>();
            foreach (var dir in Current.PendingDelete)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { remaining.Add(dir); }
            }
            Current.PendingDelete = remaining;
            Save();
        }
    }

    // Caller holds _gate.
    private void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Current, PluginRegistryJsonContext.Default.InstalledPlugins));
        }
        catch { /* best-effort: a registry write failure must not crash the app */ }
    }
}
