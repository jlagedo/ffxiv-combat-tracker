using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.Abstractions;

namespace Fct.Compat.Shim;

/// <summary>
/// The generic <see cref="IPlugin"/> that hosts a legacy ACT plugin recompiled against the compat
/// shim. It resolves the plugin's <c>IActPluginV1</c> (named by the manifest's <c>legacyEntry</c>),
/// wires the process-shared <c>ActGlobals.oFormActMain</c> over the modern host, and bridges the ACT
/// lifecycle: <c>InitPlugin</c> ⇄ <see cref="InitializeAsync"/>, <c>DeInitPlugin</c> ⇄
/// <see cref="DisposeAsync"/>. The plugin author writes no new entry point — the manifest points here.
/// </summary>
public sealed class LegacyPluginHost : IPlugin
{
    private readonly Assembly _pluginAssembly;
    private readonly string _legacyEntryTypeName;

    private FormActMain? _hub;
    private IActPluginV1? _plugin;
    private ActPluginData? _data;
    private TabPage? _tab;
    private Label? _statusLabel;

    public LegacyPluginHost(Assembly pluginAssembly, string legacyEntryTypeName)
    {
        _pluginAssembly = pluginAssembly ?? throw new ArgumentNullException(nameof(pluginAssembly));
        _legacyEntryTypeName = legacyEntryTypeName ?? throw new ArgumentNullException(nameof(legacyEntryTypeName));
    }

    /// <summary>The <c>IActPluginV1</c> this host is driving (null until initialized).</summary>
    public IActPluginV1? Plugin => _plugin;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        ct.ThrowIfCancellationRequested();

        _hub = EnsureHub(host);

        var type = _pluginAssembly.GetType(_legacyEntryTypeName, throwOnError: true)!;
        if (!typeof(IActPluginV1).IsAssignableFrom(type))
            throw new InvalidOperationException($"Legacy entry '{_legacyEntryTypeName}' does not implement IActPluginV1.");

        _plugin = (IActPluginV1)Activator.CreateInstance(type)!;

        // The WinForms surface ACT hands every plugin. Constructed detached (never shown here); the UI
        // slice embeds it via Avalonia NativeControlHost.
        _tab = new TabPage();
        _statusLabel = new Label();

        _data = new ActPluginData
        {
            pluginObj = _plugin,
            tpPluginSpace = _tab,
            lblPluginStatus = _statusLabel,
            lblPluginTitle = new Label { Text = type.Name },
            pluginFile = new FileInfo(_pluginAssembly.Location),
            pluginVersion = _pluginAssembly.GetName().Version?.ToString() ?? "0.0.0.0",
        };
        _hub.ActPlugins.Add(_data);

        _plugin.InitPlugin(_tab, _statusLabel);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _plugin?.DeInitPlugin();
        }
        finally
        {
            if (_hub is not null && _data is not null) _hub.ActPlugins.Remove(_data);
            _tab?.Dispose();
            _statusLabel?.Dispose();
            _data?.lblPluginTitle?.Dispose();
        }
        return default;
    }

    // One FormActMain per process, shared across every shimmed plugin (as in real ACT). Plugin loads
    // are sequential, so a plain null-check is sufficient; the first plugin's host backs the shared
    // services (they are process-wide singletons, identical across plugins).
    private static FormActMain EnsureHub(IPluginHost host)
    {
        ActGlobals.oFormActMain ??= new FormActMain(host);
        var hub = ActGlobals.oFormActMain;

        // Project the modern event stream onto the SDK's IDataSubscription surface (process-lifetime,
        // like the hub itself). A recompiled plugin reflects hub-side DataSubscription to bind its events.
        if (hub.DataSubscription is null)
            hub.AttachDataSubscription(new DataSubscriptionAdapter(host.Game.Events));

        return hub;
    }
}
