using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Microsoft.Extensions.Logging;

namespace Fct.SamplePlugin;

/// <summary>
/// A minimal native plugin: subscribes to the event bus, reads a snapshot, registers a named
/// callback, produces audio, round-trips a setting through storage, taps the raw-packet firehose,
/// emits a synthetic custom line via the capability-gated write-back hatch, and contributes a
/// settings page + a transient corner control (work item 9's <see cref="IUiContributor"/> face).
/// Everything it touches is the modern contract.
/// </summary>
public sealed class SamplePlugin : IPlugin, IUiContributor
{
    private const string SettingsPageId = "com.fct.sample.settings";
    private const string CornerControlId = "com.fct.sample.corner";

    private IPluginHost? _host;
    private IUiHost? _ui;
    private IDisposable? _subscription;
    private IDisposable? _callback;
    private IDisposable? _packetSubscription;
    private IDisposable? _cornerControl;
    private int _launches;

    public async Task InitializeAsync(IPluginHost host, CancellationToken ct)
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

        // Tap the raw-packet read hatch (requires the 'raw' capability): on an inbound packet, re-emit a
        // synthetic custom line — OverlayPlugin's RegisterNetworkParser → custom-line pattern in miniature.
        _packetSubscription = host.RawPackets.Subscribe(p =>
        {
            if (p.Direction == PacketDirection.Received)
                host.RawLogLines.Emit((LogMessageType)258, $"258|packet-seen|{p.Bytes.Length}");
        });

        // A synthetic custom (256+) line onto the live bus (requires the 'raw' capability).
        host.RawLogLines.Emit((LogMessageType)257, "257|sample-online");

        await PersistLaunchAsync(host);
    }

    // RegisterUi runs after InitializeAsync completes (see IUiContributor), so _launches already
    // reflects this session's count by the time the settings page renders it.
    public void RegisterUi(IUiHost ui)
    {
        _ui = ui;
        ui.AddSettingsPage(new UiSurface(SettingsPageId, "Sample Plugin", BuildSettingsView));
    }

    private async Task PersistLaunchAsync(IPluginHost host)
    {
        var settings = await host.Storage.LoadSettingsAsync<SampleSettings>() ?? new SampleSettings();
        settings.Launches++;
        await host.Storage.SaveSettingsAsync(settings);
        _launches = settings.Launches;
        host.Logger.LogInformation("SamplePlugin launch #{Count}", settings.Launches);
    }

    private Control BuildSettingsView()
    {
        var showCorner = new Button { Content = "Show corner control (5s)" };
        showCorner.Click += (_, _) => ShowCornerControl();

        var reveal = new Button { Content = "Reveal this page" };
        reveal.Click += (_, _) => _ui?.RevealPage(SettingsPageId);

        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Launched {_launches} time(s) this session." },
                showCorner,
                reveal,
            },
        };
    }

    private void ShowCornerControl()
    {
        if (_ui is null) return;
        _cornerControl?.Dispose();
        _cornerControl = _ui.AddCornerControl(new UiSurface(CornerControlId, "Sample corner control", BuildCornerView));

        var ui = _ui;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            ui.Dispatcher.Post(() =>
            {
                _cornerControl?.Dispose();
                _cornerControl = null;
            }));
    }

    private static Control BuildCornerView() => new Border
    {
        Padding = new Thickness(12),
        Child = new TextBlock { Text = "Hello from Fct.SamplePlugin!" },
    };

    public ValueTask DisposeAsync()
    {
        _host?.Logger.LogInformation("SamplePlugin disposing");
        _subscription?.Dispose();
        _callback?.Dispose();
        _packetSubscription?.Dispose();
        _cornerControl?.Dispose();
        return default;
    }

    private sealed class SampleSettings
    {
        public int Launches { get; set; }
    }
}
