using System;
using System.Collections.Generic;
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
/// The single entry point for adding and removing plugins. Accepts a <b>directory, a single .dll, or a
/// zip</b>, classifies the plugin (native / recompiled-shim / real-legacy), routes it to the right
/// executor (in-process ALC, or the satellite), and records it in the persisted registry so it re-loads
/// across restarts. Native/shim plugins are copied into the catalog; <b>real-legacy plugins install by
/// reference</b> — loaded in place from where the user picked them (like ACT itself), never copied.
/// <see cref="UninstallAsync"/> is the symmetric unload = uninstall: tear down live, drop the registry
/// entry, and delete the install folder for catalog-owned plugins (legacy files are left untouched) —
/// all without a restart.
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
            var payloadDir = PreparePayload(sourcePath, ref staging, out var singleDll);

            PluginManifest? manifest = null;
            var manifestPath = Path.Combine(payloadDir, "plugin.json");
            if (File.Exists(manifestPath))
                PluginManifest.TryLoad(manifestPath, out manifest, out _);

            // Classify the payload; the classifier disposes its MetadataLoadContext before we touch the
            // files again. A single picked DLL (e.g. FFXIV_ACT_Plugin.dll in the ACT install, whose folder
            // holds other plugins) is classified alone, not by scanning the whole folder.
            var c = singleDll is not null
                ? _classifier.ClassifyFile(singleDll, manifest)
                : _classifier.Classify(payloadDir, manifest);

            // Legacy plugins install by reference: load in place from where the user picked them (like ACT
            // itself), so folder plugins keep their sibling deps (OverlayPlugin's libs/CEF/deucalion) and
            // uninstall never deletes the user's files. Native/shim plugins are copied into our catalog.
            // A zip is always copied (its extract dir is transient), even for legacy.
            bool inPlace = c.Kind == LoadKind.RealLegacy && staging is null;
            string destDir;
            if (inPlace)
            {
                destDir = singleDll is not null ? Path.GetDirectoryName(singleDll)! : payloadDir;
            }
            else
            {
                destDir = _paths.DirFor(c.Id);
                if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                Directory.CreateDirectory(destDir);
                if (singleDll is not null)
                    File.Copy(singleDll, Path.Combine(destDir, Path.GetFileName(singleDll)), overwrite: true);
                else
                    CopyDirectory(payloadDir, destDir);
            }

            var title = manifest?.Name ?? c.Id;
            var record = new InstalledPluginRecord(c.Id, c.Kind, destDir, c.EntryTypeName, c.Version)
            {
                Title = title,
                AssemblyFile = c.AssemblyFile,
                // Record the satellite package for real-legacy plugins (observability; routing re-resolves).
                Package = c.Kind == LoadKind.RealLegacy
                    ? PackageResolver.Resolve(c.AssemblyFile, c.Id, title).Package : null,
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

            // Legacy plugins are loaded in place (never copied into our catalog), so uninstall only
            // unregisters + unloads them — it must never delete the user's own files. Native/shim
            // plugins live in our install dir, so those are deleted (deferred if still locked).
            bool ownsFiles = kind != LoadKind.RealLegacy;
            if (ownsFiles && dir is not null) DeleteOrDefer(dir, freed);
            _registry.Remove(id);

            var fileState = !ownsFiles ? "left in place" : freed ? "deleted" : "deferred";
            _log.LogInformation(LogEvents.NativePluginUnloaded, "Uninstalled plugin {Id} ({Kind}); files {State}",
                id, kind, fileState);
            _notifications?.Publish(NotificationSeverity.Success, "Plugins", $"{record?.Title ?? id} removed",
                !ownsFiles ? "The plugin was unloaded (its files were left untouched)."
                : freed ? "The plugin was unloaded and deleted." : "The plugin was unloaded; its files will be removed on next launch.");
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
    /// plugin in-process. Real-legacy records are replayed to their package satellites
    /// (<see cref="ReplayLegacyToSatelliteAsync"/>).</summary>
    public async Task LoadPersistedAsync(CancellationToken ct)
    {
        _registry.Load();   // read installed-plugins.json back into memory so installs survive restarts
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

    /// <summary>On startup, (re)send a LOADPLUGIN for every persisted real-legacy plugin so they come
    /// back after a restart — each routes through the router, which spawns exactly the package satellites
    /// that have installed plugins (catalog-driven). Returns the records whose entry DLL no longer exists
    /// (install-by-reference sources can move or vanish) so the shell can offer to re-locate them.</summary>
    public async Task<IReadOnlyList<InstalledPluginRecord>> ReplayLegacyToSatelliteAsync()
    {
        var missing = new List<InstalledPluginRecord>();
        foreach (var record in _registry.All().Where(r => r.Kind == LoadKind.RealLegacy))
        {
            var dll = ResolveEntryDll(record);
            if (dll is null)
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected,
                    "Legacy plugin {Id}: entry DLL missing under {Dir}", record.Id, record.Dir);
                missing.Add(record);
                continue;
            }
            await _satellite.RequestLoadPluginAsync(record.Id, dll, record.Title ?? record.Id).ConfigureAwait(false);
        }
        return missing;
    }

    /// <summary>Re-point a real-legacy record whose install-by-reference source moved: classify the
    /// newly picked DLL, verify it is the same plugin, update the registry record, and ask the
    /// satellite to load it from the new location.</summary>
    public async Task<InstallResult> RelinkLegacyAsync(string id, string dllPath)
    {
        try
        {
            var record = _registry.Find(id);
            if (record is null) return Reject(id, $"No installed plugin '{id}'.");

            var c = _classifier.ClassifyFile(dllPath, manifest: null);
            if (c.Kind != LoadKind.RealLegacy)
                return Reject(record.Title ?? id, $"'{Path.GetFileName(dllPath)}' is not a classic ACT plugin.");
            if (!string.Equals(c.Id, record.Id, StringComparison.OrdinalIgnoreCase))
                return Reject(record.Title ?? id,
                    $"'{Path.GetFileName(dllPath)}' is a different plugin — use Add plugin to install it.");

            var title = record.Title ?? record.Id;
            _registry.Add(record with
            {
                Dir = Path.GetDirectoryName(dllPath)!,
                Version = c.Version,
                AssemblyFile = c.AssemblyFile,
            });
            bool loaded = await _satellite.RequestLoadPluginAsync(record.Id, dllPath, title).ConfigureAwait(false);

            _log.LogInformation(LogEvents.NativePluginLoaded, "Relinked legacy plugin {Id} -> {Dll}", id, dllPath);
            _notifications?.Publish(NotificationSeverity.Success, "Plugins", $"{title} relinked",
                loaded ? "The plugin is running from its new location."
                       : "The plugin will load when the classic engine is running.");
            return new InstallResult(true, LoadKind.RealLegacy, record.Id, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected, ex, "Relink failed for {Id} from {Dll}", id, dllPath);
            _notifications?.Publish(NotificationSeverity.Error, "Plugins", "Plugin relink failed", ex.Message);
            return InstallResult.Fail(ex.Message);
        }
    }

    private InstallResult Reject(string title, string reason)
    {
        _log.LogWarning(LogEvents.NativePluginManifestRejected, "Relink rejected for {Title}: {Reason}", title, reason);
        _notifications?.Publish(NotificationSeverity.Error, "Plugins", "Plugin relink failed", reason);
        return InstallResult.Fail(reason);
    }

    private async Task<bool> RouteLoadAsync(PluginClassification c, string destDir, string title, CancellationToken ct)
    {
        if (c.Kind == LoadKind.RealLegacy)
        {
            var dll = Path.Combine(destDir, c.AssemblyFile);
            // The router resolves the package, spawns its satellite on demand, and forwards the load.
            return await _satellite.RequestLoadPluginAsync(c.Id, dll, title).ConfigureAwait(false);
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

    // Directory -> as-is; single .dll -> its folder as the payload dir + the dll itself (singleDll);
    // zip -> extract into a fresh staging dir and descend to the real payload.
    private string PreparePayload(string sourcePath, ref string? staging, out string? singleDll)
    {
        singleDll = null;

        if (Directory.Exists(sourcePath))
            return sourcePath;

        if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            singleDll = sourcePath;
            return Path.GetDirectoryName(sourcePath) ?? ".";
        }

        if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            staging = _paths.NewStagingDir(Path.GetFileNameWithoutExtension(sourcePath) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ZipFile.ExtractToDirectory(sourcePath, staging);   // modern .NET rejects path traversal
            return ResolvePayloadRoot(staging);
        }

        throw new InvalidOperationException($"'{sourcePath}' is not a plugin directory, .dll, or .zip file.");
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
