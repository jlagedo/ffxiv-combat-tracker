using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.App.Hosting;
using Fct.App.Plugins;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.App.Plugins.Ui;

/// <summary>
/// The host's <see cref="IUiHost"/> implementation and the single owner of every UI surface a
/// plugin contributes (work item 9 in PLUGIN-API.md). <see cref="FlushRegisterUi"/> is called once,
/// after the shell is live, for every loaded plugin whose manifest declares the <c>"ui"</c>
/// capability; a fault in one plugin's <c>RegisterUi</c> is contained and surfaced as an inline
/// error card, never a dead shell — mirroring <see cref="PluginManager"/>'s init quarantine.
/// Public so it can sit in <see cref="ViewModels.MainViewModel"/>'s DI-injected constructor; the
/// members that reference internal loader types (<see cref="PluginManifest"/>) stay internal.
/// </summary>
/// <remarks>
/// <see cref="RemovePlugin"/> retracts a plugin's contributed surfaces on hot-unload: it clears the
/// plugin's settings page and corner controls and forgets its page ids so a later re-install of the
/// same plugin is not rejected as a duplicate.
/// </remarks>
public sealed class PluginUiCoordinator
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<PluginUiCoordinator> _log;
    private readonly INotificationHub? _notifications;
    private readonly HashSet<string> _settingsPageIds = new(StringComparer.Ordinal);
    // Per-plugin bookkeeping so a plugin's surfaces can be retracted on unload.
    private readonly Dictionary<string, List<string>> _pageIdsByPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _cornerIdsByPlugin = new(StringComparer.Ordinal);

    public PluginUiCoordinator(IUiDispatcher dispatcher, ILoggerFactory loggerFactory, INotificationHub? notifications = null)
    {
        _dispatcher = dispatcher;
        _log = loggerFactory.CreateLogger<PluginUiCoordinator>();
        _notifications = notifications;
    }

    /// <summary>Raised when a plugin's settings page is accepted (id, page).</summary>
    internal event Action<string, UiSurface>? SettingsPageAdded;

    /// <summary>Raised when a plugin's settings surface is retracted on unload (plugin id).</summary>
    internal event Action<string>? SettingsPageRemoved;

    /// <summary>Raised when a plugin asks to bring one of its pages to the foreground.</summary>
    internal event Action<string>? PageRevealRequested;

    /// <summary>Raised when a plugin adds a transient corner control (id, control).</summary>
    internal event Action<string, UiSurface>? CornerControlAdded;

    /// <summary>Raised when a corner control is removed (by its surface id).</summary>
    internal event Action<string>? CornerControlRemoved;

    internal IUiDispatcher Dispatcher => _dispatcher;

    /// <summary>
    /// Calls <see cref="IUiContributor.RegisterUi"/> once per loaded plugin that declares the
    /// <c>"ui"</c> capability, on the UI thread. Plugins without the capability, or that don't
    /// implement <see cref="IUiContributor"/>, are skipped. A throw is contained to that plugin.
    /// </summary>
    internal void FlushRegisterUi(IEnumerable<(PluginManifest Manifest, IPlugin Instance)> plugins)
    {
        var snapshot = plugins as IReadOnlyCollection<(PluginManifest Manifest, IPlugin Instance)> ?? plugins.ToList();
        RunOnUiThread(() =>
        {
            foreach (var (manifest, instance) in snapshot)
                RegisterOne(manifest, instance);
        });
    }

    private void RegisterOne(PluginManifest manifest, IPlugin instance)
    {
        if (!manifest.HasCapability("ui"))
            return;

        if (instance is not IUiContributor contributor)
        {
            _log.LogWarning(LogEvents.NativePluginUiNotContributor,
                "Plugin {Id} declares the 'ui' capability but does not implement IUiContributor — skipped", manifest.Id);
            return;
        }

        try
        {
            contributor.RegisterUi(new PluginUiHost(manifest.Id, this));
            _log.LogInformation(LogEvents.NativePluginUiRegistered, "Plugin {Id} registered its UI", manifest.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(LogEvents.NativePluginUiFaulted, ex, "Plugin {Id} threw from RegisterUi — UI contribution skipped", manifest.Id);
            _notifications?.Publish(NotificationSeverity.Error, manifest.Id, $"{manifest.Id} UI failed to load", ex.Message);

            var displayName = manifest.Name ?? manifest.Id;
            var errorSurface = new UiSurface($"{manifest.Id}.ui-error", displayName,
                () => BuildErrorPlaceholder(displayName, ex));
            SettingsPageAdded?.Invoke(manifest.Id, errorSurface);
        }
    }

    internal void AddSettingsPage(string pluginId, UiSurface page)
    {
        RunOnUiThread(() =>
        {
            if (!_settingsPageIds.Add(page.Id))
            {
                _log.LogWarning(LogEvents.NativePluginUiDuplicateSurface,
                    "Plugin {Id} tried to add a duplicate settings page id {PageId} — ignored", pluginId, page.Id);
                return;
            }
            Track(_pageIdsByPlugin, pluginId, page.Id);
            SettingsPageAdded?.Invoke(pluginId, page);
        });
    }

    internal void RevealPage(string pageId) => RunOnUiThread(() => PageRevealRequested?.Invoke(pageId));

    internal IDisposable AddCornerControl(string pluginId, UiSurface control)
    {
        RunOnUiThread(() =>
        {
            Track(_cornerIdsByPlugin, pluginId, control.Id);
            CornerControlAdded?.Invoke(pluginId, control);
        });
        return new CornerControlHandle(this, control.Id);
    }

    /// <summary>Retract everything <paramref name="pluginId"/> contributed (on hot-unload): its
    /// corner controls and its settings surface, and forget its page ids so a re-install is accepted.</summary>
    public void RemovePlugin(string pluginId)
    {
        RunOnUiThread(() =>
        {
            if (_cornerIdsByPlugin.TryGetValue(pluginId, out var corners))
            {
                foreach (var id in corners) CornerControlRemoved?.Invoke(id);
                _cornerIdsByPlugin.Remove(pluginId);
            }
            if (_pageIdsByPlugin.TryGetValue(pluginId, out var pages))
            {
                foreach (var id in pages) _settingsPageIds.Remove(id);
                _pageIdsByPlugin.Remove(pluginId);
            }
            SettingsPageRemoved?.Invoke(pluginId);
        });
    }

    private static void Track(Dictionary<string, List<string>> map, string pluginId, string id)
    {
        if (!map.TryGetValue(pluginId, out var list)) { list = new List<string>(); map[pluginId] = list; }
        list.Add(id);
    }

    internal void RemoveCornerControl(string id) => RunOnUiThread(() => CornerControlRemoved?.Invoke(id));

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Post(action);
    }

    // Plain literals, not localized resx: this is a last-resort fault placeholder (a plugin's
    // RegisterUi threw), the same un-localized-fault-text posture PluginManager's own quarantine
    // notifications use. Also keeps this file (linked into the headless test project) free of a
    // Fct.App.Lang.Resources dependency, whose ResourceManager needs the real assembly's embedded resx.
    private static Control BuildErrorPlaceholder(string displayName, Exception ex) => new StackPanel
    {
        Margin = new Avalonia.Thickness(16),
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = "Couldn't load settings" },
            new TextBlock
            {
                Text = $"{displayName} failed to load its settings page: {ex.Message}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        },
    };

    private sealed class CornerControlHandle : IDisposable
    {
        private readonly PluginUiCoordinator _owner;
        private readonly string _id;
        private int _disposed;

        public CornerControlHandle(PluginUiCoordinator owner, string id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RemoveCornerControl(_id);
        }
    }
}
