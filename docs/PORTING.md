# Porting to modern — the plugin author's guide

This is the hands-on guide to building a plugin for FFXIV Combat Tracker, or bringing an existing ACT
plugin forward. It is example-first: every snippet is lifted from the two reference plugins,
[`samples/Fct.SamplePlugin`](../samples/Fct.SamplePlugin) (native) and
[`samples/Fct.SampleLegacyPlugin`](../samples/Fct.SampleLegacyPlugin) (recompile-shim). For the terse
contract catalog see [`PLUGIN-API.md`](PLUGIN-API.md); for UI styling see [`UI-TOKENS.md`](UI-TOKENS.md).

> **Status:** this is a v1 dev-community test release. The contract is `1.0` and the surface is
> additive, but the app is a prototype — pin the SDK version you build against and expect churn.

---

## The three stops

Migration is a road with three stops. A plugin sits at any stop and moves forward when its author
chooses — the host supports all three at once, and never forces a plugin off the stop it's on.

| Stop | What it is | What the author does | TFM |
|---|---|---|---|
| **1 — Legacy** | runs unmodified in an isolated .NET Framework 4.8 satellite over IPC | **nothing** (drop-in) | net48 |
| **2 — Recompile-shim** | same `IActPluginV1` source recompiled onto modern .NET, WinForms UI intact, in-process | recompile against the shim facade; ship a manifest | net10-windows |
| **3 — Native** | modern `IPlugin` on the typed API, UI on Avalonia | write against `Fct.Abstractions` | net10 |

Direction of travel is always **1 → 2 → 3**, opt-in and incremental. This guide covers the two stops
an author actually writes code for: **Path A — native (stop 3)**, the centerpiece, and **Path B —
recompile-shim (stop 2)**, the on-ramp for an existing ACT plugin. Stop 1 needs no code; the host runs
today's plugins unmodified in a satellite (see [`ARCHITECTURE.md`](ARCHITECTURE.md) §3/§9).

---

## Path A — Write a native plugin (stop 3)

### 1. Project setup

A native plugin is a normal `net10.0` class library that references the SDK and ships as a staged
folder (not a NuGet package consumers reference). The reference csproj
([`Fct.SamplePlugin.csproj`](../samples/Fct.SamplePlugin/Fct.SamplePlugin.csproj)):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Fct.Abstractions\Fct.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\Fct.Abstractions.UI\Fct.Abstractions.UI.csproj" />
  </ItemGroup>

  <!-- Compile-time only: Avalonia is host-provided and shared to the default ALC, so it must not
       ship as a private plugin dependency. -->
  <ItemGroup>
    <PackageReference Include="Avalonia" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Two things matter here:

- **`EnableDynamicLoading=true`** lays out the plugin's private dependencies so the host's collectible
  load context can resolve them.
- **Host-shared packages are `ExcludeAssets="runtime"`.** `Fct.Abstractions`, `Fct.Abstractions.UI`,
  `Microsoft.Extensions.Logging.Abstractions`, and anything `Avalonia*` are supplied by the host and
  shared to the default load context for a single type identity across the boundary — so they must be
  compile-time references only, **never** shipped in your plugin folder. Drop `Fct.Abstractions.UI`
  and the Avalonia reference if your plugin has no UI.

Outside the repo, reference the SDK from the NuGet packages the build produces
(`dist\<mode>\packages\Fct.Abstractions*.nupkg`) instead of `ProjectReference`.

### 2. The manifest — `plugin.json`

Copied to the output folder next to your DLL. Native plugins name their `IPlugin` type in `entry`:

```json
{
  "id": "com.example.myplugin",
  "version": "1.0.0",
  "contract": "1.0",
  "assembly": "MyPlugin.dll",
  "entry": "MyPlugin.MyPlugin",
  "capabilities": [ ],
  "name": "My Plugin",
  "description": "What it does, in one line.",
  "author": "you"
}
```

`id`/`version`/`contract`/`assembly`/`entry` are required; `capabilities`/`name`/`description`/`author`
are optional. Declare `"raw"` only if you consume packets/custom log lines, and `"ui"` only if you
implement `IUiContributor` (see below). Full field reference: [`PLUGIN-API.md`](PLUGIN-API.md#the-manifest--pluginjson).

### 3. Implement `IPlugin`

The host constructs your `entry` type, calls `InitializeAsync` **once** (time-boxed to ~10 s — honor
the `CancellationToken`), and calls `DisposeAsync` on unload. It loads you into your own collectible
`AssemblyLoadContext`; a throw or hang during init gets you quarantined and unloaded, so **dispose
every handle you take**.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Microsoft.Extensions.Logging;

namespace MyPlugin;

public sealed class MyPlugin : IPlugin
{
    private IPluginHost? _host;
    private IDisposable? _subscription;

    public async Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        host.Logger.LogInformation("MyPlugin online (id {Id}, v{Version})",
            host.Self.Id, host.Self.Version);

        // ... subscribe, read settings, register sinks here ...
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return default;
    }
}
```

### 4. Consume host services

Everything comes off the `IPluginHost` handed to `InitializeAsync`. The snippets below are how the
sample uses each service.

**Event bus (push)** — subscribe to the typed game-event stream; switch on the concrete record and
ignore the rest:

```csharp
_subscription = host.Game.Events.Subscribe(GameEventFilter.All, e =>
{
    switch (e)
    {
        case ActionEffect a:
            host.Logger.LogInformation("{Source} used {Action}", a.Source.Name, a.ActionName);
            break;
        case DeathOccurred d:
            host.Logger.LogInformation("{Victim} died", d.Victim.Name);
            break;
        // unknown events are simply ignored — new event types are additive
    }
});
```

Filter down when you only want some events: `new GameEventFilter(new[] { typeof(ActionEffect), typeof(DeathOccurred) })`.
The `IAsyncEnumerable` overload (`Subscribe(filter, ct)`) supports `await foreach` if you prefer a pump.

**Snapshot (pull)** — a free-threaded, immutable point-in-time view; safe from any thread:

```csharp
var snap = host.Game.Snapshot();
var me = snap.Player;
foreach (var actor in snap.Actors) { /* ... */ }
var zone = snap.Zone.Name;
```

**Encounters** — read the live rollup or drive combat state (the sample does not exercise this):

```csharp
if (host.Encounters.Active is { } enc)
{
    host.Logger.LogInformation("{Dps:F0} raid DPS over {Dur}", enc.Dps, enc.Duration);
    var report = host.Encounters.ExportText(enc, EncounterExportFormat.Markdown);
}
```

**Storage** — round-trip typed settings through your private data directory:

```csharp
sealed class Settings { public int Launches { get; set; } }

var settings = await host.Storage.LoadSettingsAsync<Settings>() ?? new Settings();
settings.Launches++;
await host.Storage.SaveSettingsAsync(settings);
```

**Audio** — produce TTS/sound (any plugin), or register a sink (audio-provider plugins):

```csharp
host.Audio.Speak("Pull in 5");                          // producer
host.Audio.Play(@"C:\sounds\alert.wav", volume: 80);

// Provider: route audio somewhere else (e.g. a Discord bridge). Dispose to unregister.
IDisposable _sink = host.Audio.RegisterSink(new MySink(), priority: 10, terminal: true);

sealed class MySink : IAudioSink
{
    public ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct) { /* ... */ return default; }
    public ValueTask PlayAsync(string filePath, int volume, CancellationToken ct) { /* ... */ return default; }
}
```

**Peer registry** — named callbacks (Triggernometry-style) and typed plugin-to-plugin events:

```csharp
// Named callback others can invoke by string name.
IDisposable _cb = host.Plugins.RegisterCallback("myplugin.ping", arg =>
    host.Logger.LogInformation("ping: {Arg}", arg));

// Typed pub/sub between plugins (distinct from the game-event bus).
public sealed record RaidWipe(string Boss);
host.Plugins.Publish(new RaidWipe("Golbez"));
IDisposable _sub = host.Plugins.Subscribe<RaidWipe>(w => { /* ... */ });
```

### 5. Capability-gated hatches (`"raw"`)

Declare `"raw"` in your manifest to receive the opcode-bearing escape hatches; without it both are
inert no-ops, and the typed bus stays opcode-free for everyone else. The sample taps the read firehose
and re-injects a synthetic custom (256+) line — OverlayPlugin's `RegisterNetworkParser` → custom-line
pattern in miniature:

```csharp
_packetSubscription = host.RawPackets.Subscribe(p =>
{
    if (p.Direction == PacketDirection.Received)
        host.RawLogLines.Emit((LogMessageType)258, $"258|packet-seen|{p.Bytes.Length}");
});
```

Most plugins never need `raw` — reach for it only when you decode packets or emit custom log lines.

### 6. Contribute UI (`"ui"`)

Implement `IUiContributor` alongside `IPlugin` and declare `"ui"`. The host calls `RegisterUi` once, on
the UI thread, after `InitializeAsync` completes. Add a settings page whose view is built lazily:

```csharp
using Avalonia.Controls;
using Fct.Abstractions.UI;

public sealed class MyPlugin : IPlugin, IUiContributor
{
    private const string SettingsPageId = "com.example.myplugin.settings";

    public void RegisterUi(IUiHost ui)
        => ui.AddSettingsPage(new UiSurface(SettingsPageId, "My Plugin", BuildSettingsView));

    private Control BuildSettingsView()
    {
        var title = new TextBlock { Text = "Settings" };
        title.Classes.Add(FctStyleClasses.H1);          // style through the token contract

        var card = new Border();
        card.Classes.Add(FctStyleClasses.Card);
        card.Child = new StackPanel { Spacing = FctMetrics.SpaceSm, Children = { title } };
        return card;
    }
}
```

**Style through the token contract, never with hardcoded colors/fonts** — use the `fct-*` classes
(`FctStyleClasses`), the restyle-able brush/font tokens (`FctTokens`, bound via `DynamicResource`), and
the layout constants (`FctMetrics`). A page built this way tracks any shell restyle at runtime. The
sample's `BuildSettingsView` is the full reference; the catalog and rules are in
[`UI-TOKENS.md`](UI-TOKENS.md). `IUiHost` also offers `RevealPage(id)`, transient `AddCornerControl` /
`RemoveCornerControl`, and `Dispatcher` (`IUiDispatcher`) for marshaling background work onto the UI
thread.

### 7. Install and run

Build your plugin, then install its output folder through the host's plugin catalog. The installer
accepts a **directory, a single `.dll`, or a `.zip`**; directories and DLLs load **in place** (not
copied), zips extract into the catalog. Installed plugins are recorded in a persisted registry and
reload across restarts. On load the host reads your `plugin.json`, gates the `contract` major version,
constructs your `entry` type in a fresh ALC, and calls `InitializeAsync`. If it loads, it shows in the
plugin roster; if init throws, the log names the fault and the plugin is quarantined.

---

## Path B — Recompile an ACT plugin onto the shim (stop 2)

This is the on-ramp for an **existing ACT plugin**: keep your `IActPluginV1` source and WinForms UI,
recompile it onto modern .NET, and run in-process — no rewrite. The reference is
[`samples/Fct.SampleLegacyPlugin`](../samples/Fct.SampleLegacyPlugin).

### 1. Project setup

Target `net10.0-windows` with WinForms enabled, and reference the shim's ACT facade (assembly
`Advanced Combat Tracker`) instead of the real ACT
([`Fct.SampleLegacyPlugin.csproj`](../samples/Fct.SampleLegacyPlugin/Fct.SampleLegacyPlugin.csproj)):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Fct.Compat.Shim.ActFacade\Fct.Compat.Shim.ActFacade.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`net10.0-windows` + `UseWindowsForms` because `IActPluginV1.InitPlugin` takes WinForms `TabPage`/`Label`.
If your plugin also binds the FFXIV SDK, reference the shim's SDK facade (assembly
`FFXIV_ACT_Plugin.Common`, project `Fct.Compat.Shim.SdkFacade`) the same way.

### 2. The manifest — `legacyEntry`, not `entry`

The one manifest difference: a recompiled plugin names its `IActPluginV1` type in **`legacyEntry`**.
The host then drives it through the shim's `LegacyPluginHost` (which is the actual `IPlugin`):

```json
{
  "id": "com.example.mylegacyplugin",
  "version": "1.0.0",
  "contract": "1.0",
  "assembly": "MyLegacyPlugin.dll",
  "legacyEntry": "MyLegacyPlugin.MyLegacyPlugin",
  "capabilities": [ ]
}
```

`entry` and `legacyEntry` are mutually exclusive — set exactly one.

### 3. Your code is unchanged

Your `IActPluginV1` implementation stays exactly as it was under ACT — the shim supplies the host
surface it binds to ([`SampleLegacyPlugin.cs`](../samples/Fct.SampleLegacyPlugin/SampleLegacyPlugin.cs)):

```csharp
using System.Windows.Forms;
using Advanced_Combat_Tracker;   // the shim facade, not the real ACT

public sealed class MyLegacyPlugin : IActPluginV1
{
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        pluginStatusText.Text = "My legacy plugin online";
        ActGlobals.oFormActMain.WriteInfoLog("MyLegacyPlugin initialized");
        // build your WinForms UI into pluginScreenSpace exactly as before
    }

    public void DeInitPlugin() { /* ... */ }
}
```

### 4. What the shim maps onto the host

You keep the ACT programming model; the shim re-projects it onto the modern `IPluginHost` so your
plugin runs without ACT present. The bindings you rely on, transparently:

| ACT-era surface | Maps to |
|---|---|
| `ActGlobals.oFormActMain` logging (`WriteInfoLog`/`WriteDebugLog`/…) | `IPluginHost.Logger` |
| `PlayTtsMethod` / `PlaySoundMethod` delegate slots, `TTS`/`PlaySound` | `IAudioOutput` (save-and-replace becomes a terminal sink) |
| `BeforeLogLineRead` / `OnLogLineRead` | the modern `RawLogLine` firehose |
| `AddCombatAction` / `SetEncounter` / `EndCombat` / `InCombat` | `IEncounterService` over the shared `Fct.Aggregation` engine |
| `RegisterNamedCallback` / `InvokeNamedCallback` | `IPluginRegistry` callbacks |
| `IDataRepository` / `IDataSubscription` (SDK) | projected from `IGameSession` (+ `IRawPacketSource`) |

The WinForms `TabPage` you build into is constructed detached and embedded in the shell via Avalonia's
`NativeControlHost`, so your existing UI renders as-is. (Shim internals:
[`ARCHITECTURE.md`](ARCHITECTURE.md) §7/§12.)

### 5. Why `legacyEntry` routes in-process (and real ACT plugins don't)

The host classifies three rungs and routes accordingly:

- **`legacyEntry`** (or an assembly referencing the **unsigned** shim facade) → **recompile-shim**,
  loaded **in-process** in its own ALC.
- An assembly referencing the **strong-named real** `Advanced Combat Tracker`
  (`PublicKeyToken=a946b61e93d97868`) → **real-legacy**, routed to a **net48 satellite** — it can't
  load unmodified on .NET 10.
- An assembly implementing `Fct.Abstractions.IPlugin` → **native**.

So the difference between "recompiled" and "unmodified" is just which `Advanced Combat Tracker` you
built against — the shim facade (in-process) or the real signed ACT (satellite).

---

## Then move stop 2 → stop 3

The recompile-shim keeps you working on modern .NET; going **native** unlocks what the pipe-delimited
log line never could ([`ARCHITECTURE.md`](ARCHITECTURE.md) §10):

- Full position/heading/velocity at packet rate (log lines round or drop these).
- Complete status params/stacks/source as typed records.
- High-resolution sequence + sub-frame timestamps.
- Publishing your **own** typed events for other plugins.
- Direct state queries with no string parsing and no 6× round-trip per action.

Migrate incrementally: swap `ActGlobals`/SDK reads for the typed `IPluginHost` services one subsystem
at a time, and move your WinForms UI to an Avalonia `IUiContributor` page when you're ready. Nothing
forces the jump — do it when the unlocks are worth it.

---

## Troubleshooting

- **Plugin rejected at load / not in the roster.** Check the host log. Common causes: a `contract`
  whose **major** doesn't match the host's (`2.x` on a `1.x` host is rejected; `1.anything` is fine); a
  missing required manifest field; or both/neither of `entry`/`legacyEntry` set.
- **`host.RawPackets` / `host.RawLogLines` do nothing.** You didn't declare `"raw"` in `capabilities`
  — without it they're inert no-ops by design.
- **`RegisterUi` never runs.** You declared `"ui"` but didn't implement `IUiContributor` (logged and
  skipped), or you didn't declare `"ui"` at all.
- **Settings page ignores a shell restyle.** You used `StaticResource` (or hardcoded a color). Bind
  tokens with `DynamicResource` / an observable resource binding — see [`UI-TOKENS.md`](UI-TOKENS.md).
- **Plugin quarantined right after load.** `InitializeAsync` threw or exceeded the ~10 s budget. Do
  slow work off the init path, and honor the `CancellationToken`.
- **A shared assembly shipped in my plugin folder breaks loading.** `Fct.Abstractions(.UI)`, the
  logging abstractions, and `Avalonia*` must be `ExcludeAssets="runtime"` — they're host-provided.
- **Handles leak / the plugin won't unload.** Dispose every subscription/callback/sink handle in
  `DisposeAsync`; a leaked handle can pin the collectible load context.

---

## See also

- [`PLUGIN-API.md`](PLUGIN-API.md) — the full contract catalog (services, events, manifest schema).
- [`UI-TOKENS.md`](UI-TOKENS.md) — the `fct-*` styling contract.
- [`samples/Fct.SamplePlugin`](../samples/Fct.SamplePlugin) — native reference plugin.
- [`samples/Fct.SampleLegacyPlugin`](../samples/Fct.SampleLegacyPlugin) — recompile-shim reference.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) §7/§10 + [`NORTH-STAR.md`](NORTH-STAR.md) — design + migration ladder.
