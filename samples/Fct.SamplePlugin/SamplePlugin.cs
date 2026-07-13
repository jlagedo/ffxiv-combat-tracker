using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.SamplePlugin.ViewModels;
using Fct.SamplePlugin.Views;
using Microsoft.Extensions.Logging;

namespace Fct.SamplePlugin;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// A native plugin, front to back — a small "live combat monitor" you can read top-to-bottom.
//
// It teaches the four things every plugin does, each in its own numbered lesson below:
//   1. Configuration   — load/save typed settings through your private storage.
//   2. The event bus   — subscribe to the typed game stream (the HOT PATH) the right way.
//   3. Reading state   — pull a snapshot and the live encounter rollup.
//   4. Contributing UI — a live Avalonia panel, refreshed off the hot path.
//
// A plugin implements IPlugin (lifecycle). Add IUiContributor to also contribute UI. The host loads
// you into your own collectible AssemblyLoadContext, calls InitializeAsync ONCE, and DisposeAsync on
// unload — so every handle you take here, you dispose there.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SamplePlugin : IPlugin, IUiContributor
{
    private const string SettingsPageId = "com.fct.sample.settings";
    private const string CornerControlId = "com.fct.sample.corner";

    private IPluginHost? _host;
    private IUiHost? _ui;

    // Every handle we take gets disposed in DisposeAsync — a leaked one can pin the collectible ALC.
    private IDisposable? _events;
    private IDisposable? _packets;
    private IDisposable? _cornerControl;
    private CancellationTokenSource? _refreshCts;

    // Hot-path counters. Written from the bus handler, read from the UI refresh loop — so use
    // Interlocked / volatile, never a lock (a lock on the hot path is a stall waiting to happen).
    private long _actionsSeen;
    private long _deaths;

    private SampleSettings _settings = new();
    private volatile bool _announceDeaths;   // hot-path snapshot of _settings.AnnounceDeaths
    private MonitorViewModel? _vm;           // the UI's bound state (see Lesson 4)

    public async Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        host.Logger.LogInformation("SamplePlugin online (id {Id}, v{Version})", host.Self.Id, host.Self.Version);

        // ── LESSON 1: CONFIGURATION ──────────────────────────────────────────────────────────────
        // Settings are a plain class, persisted as JSON in your private data directory. Load returns
        // null on first run — coalesce to defaults. Keep the object; save it whenever it changes.
        _settings = await host.Storage.LoadSettingsAsync<SampleSettings>() ?? new SampleSettings();
        _settings.Launches++;
        _announceDeaths = _settings.AnnounceDeaths;
        await host.Storage.SaveSettingsAsync(_settings);
        host.Logger.LogInformation("SamplePlugin launch #{Count}", _settings.Launches);

        // The UI's view model exists independently of the view — the refresh loop can feed it before
        // the settings page is ever opened. It calls back here when the user flips a setting.
        _vm = new MonitorViewModel(SetAnnounceDeaths, ShowCornerControl) { AnnounceDeaths = _settings.AnnounceDeaths };

        // ── LESSON 2: THE EVENT BUS (the hot path) ─────────────────────────────────────────────────
        // Subscribe to the typed game stream. BEST PRACTICE, in order of impact:
        //  • Filter to ONLY the event types you need — never GameEventFilter.All if you want two of
        //    them. The host then never even queues the rest to you.
        //  • Keep the handler cheap and non-blocking: bump counters, stash a value. No I/O, no
        //    per-event logging, no locks. Your subscription has a bounded queue; a slow handler just
        //    gets drop-oldest backpressure (it never stalls the game), but you still lose events.
        //  • Do NOT touch the UI here. Marshal to the UI thread on a throttled cadence instead
        //    (Lesson 4) — one repaint per frame, not one per swing.
        //  • Switch on the concrete record; ignore the rest. New event types are additive, so an
        //    unrecognized event is never an error.
        var filter = new GameEventFilter(new[] { typeof(ActionEffect), typeof(DeathOccurred) });
        _events = host.Game.Events.Subscribe(filter, OnGameEvent);

        // ── Audio (cheap, fire-and-return) ─────────────────────────────────────────────────────────
        // Speak/Play fan out to every registered sink and return immediately — safe to call from the
        // hot path (see OnGameEvent) because they never block the producer.
        host.Audio.Speak("Sample plugin online");

        // ── Capability-gated hatches ("raw") ─────────────────────────────────────────────────────
        // Declared in plugin.json under "capabilities". Without "raw" these are inert no-ops and the
        // typed bus stays opcode-free for everyone else. This mirrors OverlayPlugin's
        // RegisterNetworkParser → custom-line round-trip in miniature. Most plugins never need this.
        _packets = host.RawPackets.Subscribe(p =>
        {
            if (p.Direction == PacketDirection.Received)
                host.RawLogLines.Emit((LogMessageType)258, $"258|packet-seen|{p.Bytes.Length}");
        });
        host.RawLogLines.Emit((LogMessageType)257, "257|sample-online");

        // ── LESSON 4 (driver): refresh the UI off the hot path ───────────────────────────────────
        // A 500 ms loop projects the counters onto the live panel — decoupled from event rate, the
        // same cadence the host's own encounter projection uses. Started here, cancelled in Dispose.
        _refreshCts = new CancellationTokenSource();
        _ = RunRefreshLoopAsync(_refreshCts.Token);
    }

    // The hot-path handler. Called in-order on a background pump; keep it tiny.
    private void OnGameEvent(GameEvent e)
    {
        switch (e)
        {
            case ActionEffect:
                Interlocked.Increment(ref _actionsSeen);
                break;
            case DeathOccurred d:
                Interlocked.Increment(ref _deaths);
                if (_announceDeaths)                       // reads the volatile snapshot, no lock
                    _host?.Audio.Speak($"{d.Victim.Name} down");
                break;
        }
    }

    // ── LESSON 3: READING STATE (pull) ───────────────────────────────────────────────────────────
    // Two pull surfaces, both free-threaded:
    //  • Snapshot()  — an immutable point-in-time view (player, actors, zone, party, target…).
    //  • Encounters  — the live DPS/encounter rollup, the single source of truth the engine owns.
    // Read them when you need a value, e.g. once per UI refresh — not per event.
    private (string zone, bool inCombat, double dps) ReadState()
    {
        var host = _host!;
        var zone = host.Game.Snapshot().Zone.Name;
        var enc = host.Encounters.Active;                  // null when out of combat
        return (string.IsNullOrEmpty(zone) ? "—" : zone, host.Encounters.InCombat, enc?.Dps ?? 0);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // LESSON 4: CONTRIBUTING UI (XAML + MVVM)
    //
    // RegisterUi runs ONCE on the UI thread, after InitializeAsync completes. Add a settings page (the
    // modern replacement for the legacy WinForms TabPage). Its view builds lazily on the UI thread the
    // first time it's shown.
    //
    // The view IS the XAML (Views/MonitorView.axaml) — layout, styling, and data bindings all live
    // there. Here we do the one thing XAML can't: hand it its DataContext (the view model). That is
    // the whole idiom — build the view, set DataContext, and let bindings do the rest. Compare with
    // BuildCornerView below, which is trivial enough to build in code.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public void RegisterUi(IUiHost ui)
    {
        _ui = ui;
        ui.AddSettingsPage(new UiSurface(SettingsPageId, "Sample Monitor",
            () => new MonitorView { DataContext = _vm }));
    }

    private void SetAnnounceDeaths(bool on)
    {
        _settings.AnnounceDeaths = on;
        _announceDeaths = on;                              // publish to the hot path
        _ = _host!.Storage.SaveSettingsAsync(_settings);   // persist (fire-and-forget is fine here)
    }

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                // The loop runs on a background thread, so hop to the UI thread to touch controls.
                // BEST PRACTICE: marshal via the host dispatcher (the modern InvokeRequired/Invoke).
                _ui?.Dispatcher.Post(RefreshOnce);
        }
        catch (OperationCanceledException) { /* normal on unload */ }
    }

    // Runs on the UI thread. Reads the cheap counters + pull surfaces and pushes them into the view
    // model; the XAML bindings repaint. Setting ObservableProperties must happen on the UI thread,
    // which is why the refresh loop marshals through the dispatcher.
    private void RefreshOnce()
    {
        if (_vm is null || _host is null) return;
        var (zone, inCombat, dps) = ReadState();
        _vm.Zone = zone;
        _vm.InCombat = inCombat;
        _vm.ActionsSeen = Interlocked.Read(ref _actionsSeen);
        _vm.Deaths = Interlocked.Read(ref _deaths);
        _vm.Dps = dps;
    }

    // A transient corner notification (Triggernometry's CornerControlAdd). Dispose to remove.
    private void ShowCornerControl()
    {
        if (_ui is null) return;
        _cornerControl?.Dispose();
        _cornerControl = _ui.AddCornerControl(new UiSurface(CornerControlId, "Sample alert", BuildCornerView));

        var ui = _ui;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            ui.Dispatcher.Post(() => { _cornerControl?.Dispose(); _cornerControl = null; }));
    }

    private static Control BuildCornerView() => new Border
    {
        Padding = new Thickness(FctMetrics.SpaceMd),
        Child = new TextBlock { Text = "Hello from Fct.SamplePlugin!" },
    };

    // Teardown: cancel the loop, dispose every handle. The host awaits this before unloading the ALC.
    public async ValueTask DisposeAsync()
    {
        _host?.Logger.LogInformation("SamplePlugin disposing");
        _refreshCts?.Cancel();
        _events?.Dispose();
        _packets?.Dispose();
        _cornerControl?.Dispose();
        _refreshCts?.Dispose();
        await Task.CompletedTask;
    }

    // Your settings type: a plain class, serialized by System.Text.Json. Keep it small and versionable.
    private sealed class SampleSettings
    {
        public int Launches { get; set; }
        public bool AnnounceDeaths { get; set; }
    }
}
