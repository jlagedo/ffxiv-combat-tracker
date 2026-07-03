using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Host.Plugins.Ui;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Plugins;

/// <summary>The result of an install: whether it loaded, its detected kind + id, and any error.</summary>
internal sealed record InstallResult(bool Success, LoadKind Kind, string Id, string? Error)
{
    public static InstallResult Fail(string error) => new(false, LoadKind.Native, "", error);
}

/// <summary>
/// The single entry point for adding and removing plugins. Accepts a <b>directory or a zip</b>,
/// classifies the plugin (native / recompiled-shim / real-legacy), routes it to the right executor
/// (in-process ALC, or the satellite), and records it in the persisted registry so it re-loads across
/// restarts. <see cref="UninstallAsync"/> is the symmetric unload = uninstall: tear down live, delete
/// the install folder, drop the registry entry — all without a restart.
/// </summary>
internal sealed class PluginInstaller
{
    private readonly PluginManager _manager;
    private readonly ISatellitePluginChannel _satellite;
    private readonly PluginClassifier _classifier;
    private readonly PluginInstallPaths _paths;
    private readonly PluginRegistryStore _registry;
    private readonly PluginUiCoordinator _ui;
    private readonly INotificationHub? _notifications;
    private readonly ILogger<PluginInstaller> _log;

    private static readonly TimeSpan LegacyUnloadTimeout = TimeSpan.FromSeconds(10);

    public PluginInstaller(
        PluginManager manager,
        ISatellitePluginChannel satellite,
        PluginClassifier classifier,
        PluginInstallPaths paths,
        PluginRegistryStore registry,
        PluginUiCoordinator ui,
        ILoggerFactory loggerFactory,
        INotificationHub? notifications = null)
    {
        _manager = manager;
        _satellite = satellite;
        _classifier = classifier;
        _paths = paths;
        _registry = registry;
        _ui = ui;
        _notifications = notifications;
        _log = loggerFactory.CreateLogger<PluginInstaller>();
    }

    /// <summary>Install a plugin from a directory or a <c>.zip</c>, classify it, load it live, and
    /// persist it. Returns the outcome for the UI to surface.</summary>
    public async Task<InstallResult> InstallAsync(string sourcePath, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            _paths.EnsureRoots();
            var payloadDir = PreparePayload(sourcePath, ref staging);

            PluginManifest? manifest = null;
            var manifestPath = Path.Combine(payloadDir, "plugin.json");
            if (File.Exists(manifestPath))
                PluginManifest.TryLoad(manifestPath, out manifest, out _);

            // Classify against the staged copy; the classifier disposes its MetadataLoadContext before
            // we touch the files again, so nothing keeps them locked for the copy/load below.
            var c = _classifier.Classify(payloadDir, manifest);

            var destDir = _paths.DirFor(c.Id);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            CopyDirectory(payloadDir, destDir);

            var title = manifest?.Name ?? c.Id;
            var record = new InstalledPluginRecord(c.Id, c.Kind, destDir, c.EntryTypeName, c.Version)
            {
                Title = title,
                AssemblyFile = c.AssemblyFile,
            };

            bool loaded = await RouteLoadAsync(c, destDir, title, ct).ConfigureAwait(false);
            _registry.Add(record);

            _log.LogInformation(LogEvents.NativePluginLoaded, "Installed plugin {Id} ({Kind}) from {Source} -> {Dest}",
                c.Id, c.Kind, sourcePath, destDir);
            _notifications?.Publish(NotificationSeverity.Success, "Plugins", $"{title} installed",
                loaded ? $"{c.Kind} plugin is running." : $"{c.Kind} plugin installed; it will load when the classic engine is running.");
            return new InstallResult(true, c.Kind, c.Id, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected, ex, "Install failed from {Source}", sourcePath);
            _notifications?.Publish(NotificationSeverity.Error, "Plugins", "Plugin install failed", ex.Message);
            return InstallResult.Fail(ex.Message);
        }
        finally
        {
            if (staging is not null)
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Unload = uninstall: tear the plugin down live, delete its install folder, and remove it
    /// from the registry. Files that are still locked (collectible ALC / CEF) are deferred to next launch.</summary>
    public async Task<bool> UninstallAsync(string id, CancellationToken ct, LoadKind? kindHint = null)
    {
        var record = _registry.Find(id);
        var kind = record?.Kind ?? kindHint ?? LoadKind.Native;
        var dir = record?.Dir;
        try
        {
            bool freed;
            if (kind == LoadKind.RealLegacy)
            {
                // The host already dropped the legacy row (un-embedding its HWND) before calling here.
                freed = await _satellite.RequestUnloadPluginAsync(id, LegacyUnloadTimeout).ConfigureAwait(false);
            }
            else
            {
                _ui.RemovePlugin(id);                              // retract its settings/corner UI
                freed = await _manager.UnloadAsync(id).ConfigureAwait(false);
            }

            if (dir is not null) DeleteOrDefer(dir, freed);
            _registry.Remove(id);

            _log.LogInformation(LogEvents.NativePluginUnloaded, "Uninstalled plugin {Id} ({Kind}); files {State}",
                id, kind, freed ? "deleted" : "deferred");
            _notifications?.Publish(NotificationSeverity.Success, "Plugins", $"{record?.Title ?? id} removed",
                freed ? "The plugin was unloaded and deleted." : "The plugin was unloaded; its files will be removed on next launch.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.NativePluginFaulted, ex, "Uninstall failed for {Id}", id);
            _notifications?.Publish(NotificationSeverity.Error, "Plugins", "Plugin removal failed", ex.Message);
            return false;
        }
    }

    /// <summary>Startup (pre-UI): clear any deferred deletes, then load every persisted <i>net10</i>
    /// plugin in-process. Real-legacy records are replayed to the satellite once it is online
    /// (<see cref="ReplayLegacyToSatellite"/>).</summary>
    public async Task LoadPersistedAsync(CancellationToken ct)
    {
        _registry.ProcessPendingDeletes();
        foreach (var record in _registry.All())
        {
            if (record.Kind == LoadKind.RealLegacy) continue;
            if (!Directory.Exists(record.Dir))
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected, "Installed plugin {Id} dir missing: {Dir}", record.Id, record.Dir);
                continue;
            }
            try { await _manager.LoadDirectoryAsync(record.Dir, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(LogEvents.NativePluginFaulted, ex, "Failed to load persisted plugin {Id}", record.Id); }
        }
    }

    /// <summary>Once the satellite is online, (re)send a LOADPLUGIN for every persisted real-legacy
    /// plugin so they come back after a restart.</summary>
    public void ReplayLegacyToSatellite()
    {
        foreach (var record in _registry.All().Where(r => r.Kind == LoadKind.RealLegacy))
        {
            if (record.Entry is null && !Directory.Exists(record.Dir)) continue;
            var dll = ResolveEntryDll(record);
            if (dll is null) { _log.LogWarning(LogEvents.NativePluginManifestRejected, "Legacy plugin {Id}: entry DLL missing", record.Id); continue; }
            _satellite.RequestLoadPlugin(record.Id, dll, record.Title ?? record.Id);
        }
    }

    private async Task<bool> RouteLoadAsync(PluginClassification c, string destDir, string title, CancellationToken ct)
    {
        if (c.Kind == LoadKind.RealLegacy)
        {
            var dll = Path.Combine(destDir, c.AssemblyFile);
            return _satellite.RequestLoadPlugin(c.Id, dll, title);   // false if the satellite isn't running yet
        }
        var loaded = await _manager.LoadDirectoryAsync(destDir, ct).ConfigureAwait(false);
        return loaded is not null;
    }

    // Delete the install dir; if its files are still locked, defer to next-launch cleanup.
    private void DeleteOrDefer(string dir, bool freed)
    {
        if (!Directory.Exists(dir)) return;
        if (freed)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (Exception ex) { _log.LogDebug(LogEvents.NativePluginUnloaded, ex, "Delete of {Dir} failed; deferring", dir); }
        }
        _registry.MarkPendingDelete(dir);
    }

    // Zip -> extract into a fresh staging dir and descend to the real payload; directory -> as-is.
    private string PreparePayload(string sourcePath, ref string? staging)
    {
        if (Directory.Exists(sourcePath))
            return sourcePath;

        if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            staging = _paths.NewStagingDir(Path.GetFileNameWithoutExtension(sourcePath) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ZipFile.ExtractToDirectory(sourcePath, staging);   // modern .NET rejects path traversal
            return ResolvePayloadRoot(staging);
        }

        throw new InvalidOperationException($"'{sourcePath}' is not a plugin directory or a .zip file.");
    }

    // The plugin files may sit at the extract root or inside a single wrapper folder.
    private static string ResolvePayloadRoot(string extracted)
    {
        if (Directory.GetFiles(extracted, "*.dll").Length > 0 || File.Exists(Path.Combine(extracted, "plugin.json")))
            return extracted;
        var subs = Directory.GetDirectories(extracted);
        return subs.Length == 1 ? subs[0] : extracted;
    }

    private static string? ResolveEntryDll(InstalledPluginRecord record)
    {
        if (!Directory.Exists(record.Dir)) return null;
        // The recorded entry assembly if we have it; else fall back to the first DLL.
        if (record.AssemblyFile is not null)
        {
            var entry = Path.Combine(record.Dir, record.AssemblyFile);
            if (File.Exists(entry)) return entry;
        }
        return Directory.GetFiles(record.Dir, "*.dll").FirstOrDefault();
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
