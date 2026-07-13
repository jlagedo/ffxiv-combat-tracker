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
/// across restarts. Every kind <b>installs by reference</b> — loaded in place from where the user picked
/// it (like ACT itself), never copied — so a plugin keeps its sibling dependencies; only a zip is copied
/// into the catalog, because its extract dir is transient.
/// <see cref="UninstallAsync"/> is the symmetric unload = uninstall: tear down live, drop the registry
/// entry, and delete the install folder only for catalog-owned (zip) plugins (referenced files are left
/// untouched) — all without a restart.
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

    /// <summary>An in-process (native / recompiled-shim) load began for (id, title). The shell shows a
    /// loading placeholder while the plugin initializes; success surfaces via the registry roster and
    /// failure via <see cref="NativeLoadFailed"/>. Real-legacy loads are signalled by the satellite router.</summary>
    public event Action<string, string>? NativeLoadStarting;

    /// <summary>An in-process load faulted or was rejected — no roster row will appear, so the shell
    /// drops the pending placeholder.</summary>
    public event Action<string>? NativeLoadFailed;

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
    /// <remarks>The synchronous prefix — zip extraction (an OverlayPlugin package carries CEF), the
    /// metadata-load-context classification, and the recursive catalog copy — is heavy, and the shell
    /// awaits this straight off the UI thread. Run the whole routine on the thread pool so a frame is
    /// never blocked; feedback stays thread-safe (notifications go through the hub, roster changes
    /// marshal via the UI dispatcher).</remarks>
    public Task<InstallResult> InstallAsync(string sourcePath, CancellationToken ct)
        => Task.Run(() => InstallCoreAsync(sourcePath, ct), ct);

    private async Task<InstallResult> InstallCoreAsync(string sourcePath, CancellationToken ct)
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

            // Every kind installs by reference: load in place from where the user picked it (like ACT
            // itself), so the plugin keeps its sibling deps (a native plugin's own dependency DLLs,
            // OverlayPlugin's libs/CEF/deucalion) and uninstall never deletes the user's files. A single
            // picked DLL loads from its own folder — the deps sit right next to it. Only a zip is copied
            // (its extract dir is transient), and that copy is the one thing we own.
            bool inPlace = staging is null;
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
            _log.LogWarning(LogEvents.NativePluginInstallFailed, ex, "Install failed from {Source}", sourcePath);
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
    /// <remarks>Runs on the thread pool: the shell awaits this off the UI thread, and the recursive
    /// delete of a catalog-owned (CEF-sized) tree plus the registry write can run synchronously when the
    /// unload await completes without yielding.</remarks>
    public Task<bool> UninstallAsync(string id, CancellationToken ct, LoadKind? kindHint = null)
        => Task.Run(() => UninstallCoreAsync(id, ct, kindHint), ct);

    private async Task<bool> UninstallCoreAsync(string id, CancellationToken ct, LoadKind? kindHint)
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

            // We own (and delete) only files we copied into our catalog — zip installs. Everything else
            // is install-by-reference (loaded in place), so uninstall only unregisters + unloads it and
            // must never delete the user's own files. Owned dirs are deleted (deferred if still locked).
            bool ownsFiles = dir is not null && _paths.Owns(dir);
            if (ownsFiles) DeleteOrDefer(dir!, freed);
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
            _log.LogWarning(LogEvents.NativePluginInstallFailed, ex, "Uninstall failed for {Id}", id);
            _notifications?.Publish(NotificationSeverity.Error, "Plugins", "Plugin removal failed", ex.Message);
            return false;
        }
    }

    /// <summary>Host startup (runs when the host is started from the shell, after the window paints):
    /// clear any deferred deletes, then load every persisted <i>net10</i> plugin in-process. Real-legacy
    /// records are replayed to their package satellites (<see cref="ReplayLegacyToSatelliteAsync"/>).</summary>
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
    public Task<InstallResult> RelinkLegacyAsync(string id, string dllPath)
        => Task.Run(() => RelinkLegacyCoreAsync(id, dllPath));

    // Off the UI thread: classification opens the newly picked DLL through a metadata-load context.
    private async Task<InstallResult> RelinkLegacyCoreAsync(string id, string dllPath)
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
            _log.LogWarning(LogEvents.NativePluginInstallFailed, ex, "Relink failed for {Id} from {Dll}", id, dllPath);
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
            // The router resolves the package, spawns its satellite on demand, and forwards the load —
            // and raises its own pending/failed signals, so we don't here.
            return await _satellite.RequestLoadPluginAsync(c.Id, dll, title).ConfigureAwait(false);
        }

        // In-process (native / recompiled-shim): show a loading placeholder while InitializeAsync runs
        // (time-boxed). Success surfaces through the registry roster; a fault/reject returns null (or
        // throws), so drop the placeholder.
        NativeLoadStarting?.Invoke(c.Id, title);
        try
        {
            var loaded = await _manager.LoadDirectoryAsync(destDir, ct).ConfigureAwait(false);
            if (loaded is null) { NativeLoadFailed?.Invoke(c.Id); return false; }
            // Startup's one-time RegisterUi flush (MainWindow.OnOpened) already ran, so a plugin
            // installed live would otherwise never contribute its UI until the next restart — a
            // 'ui'-capable plugin would show the fallback details card. Flush it now; the coordinator
            // marshals to the UI thread and skips non-UI plugins.
            _ui.FlushRegisterUi(new[] { (loaded.Manifest, loaded.Instance) });
            return true;
        }
        catch
        {
            NativeLoadFailed?.Invoke(c.Id);
            throw;
        }
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
