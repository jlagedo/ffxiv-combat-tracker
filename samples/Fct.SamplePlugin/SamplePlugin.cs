using System;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Microsoft.Extensions.Logging;

namespace Fct.SamplePlugin;

/// <summary>
/// A minimal native plugin: subscribes to the event bus, reads a snapshot, registers a named
/// callback, produces audio, round-trips a setting through storage, and emits a synthetic custom line
/// via the capability-gated write-back hatch. Everything it touches is the modern contract.
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private IPluginHost? _host;
    private IDisposable? _subscription;
    private IDisposable? _callback;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        host.Logger.LogInformation("SamplePlugin initializing (id {Id}, v{Version})", host.Self.Id, host.Self.Version);

        _subscription = host.Game.Events.Subscribe(GameEventFilter.All, e =>
            host.Logger.LogInformation("SamplePlugin saw {Event} #{Seq}", e.GetType().Name, e.Sequence));

        var snapshot = host.Game.Snapshot();
        host.Logger.LogInformation("SamplePlugin snapshot: {Actors} actor(s), zone '{Zone}'",
            snapshot.Actors.Count, snapshot.Zone.Name);

        _callback = host.Plugins.RegisterCallback("sample.ping", arg =>
            host.Logger.LogInformation("SamplePlugin callback ping: {Arg}", arg));

        host.Audio.Speak("Sample plugin online");

        // A synthetic custom (256+) line onto the live bus (requires the 'raw' capability).
        host.RawLogLines.Emit((LogMessageType)257, "257|sample-online");

        return PersistLaunchAsync(host);
    }

    private static async Task PersistLaunchAsync(IPluginHost host)
    {
        var settings = await host.Storage.LoadSettingsAsync<SampleSettings>() ?? new SampleSettings();
        settings.Launches++;
        await host.Storage.SaveSettingsAsync(settings);
        host.Logger.LogInformation("SamplePlugin launch #{Count}", settings.Launches);
    }

    public ValueTask DisposeAsync()
    {
        _host?.Logger.LogInformation("SamplePlugin disposing");
        _subscription?.Dispose();
        _callback?.Dispose();
        return default;
    }

    private sealed class SampleSettings
    {
        public int Launches { get; set; }
    }
}
