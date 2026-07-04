# Satellite Process Lifecycle — Analysis

Review of the net48 satellite (`Fct.LegacyHost`) spawning, supervision, and teardown paths,
focused on runaway/stray-process risk. Scope: `Fct.Host` (`SatelliteHost`, `SatelliteLifetime`,
`ProcessJob`), `Fct.LegacyHost/Program.cs`, the `Fct.App` shell wiring, and every other
`Process.Start` call site in the repo (integration-test fixtures, oracle batch modes).

Status of each finding: **Open** until fixed; update in place when addressed.

---

## 1. Lifecycle overview (as implemented)

```
Fct.App (net10)                                Fct.LegacyHost (net48)
─────────────────                              ──────────────────────
MainWindow.OnOpened
  └─ StartSatelliteAsync
       └─ SatelliteHost.StartAsync
            ├─ create log pipe  (fct-bridge-<guid>)        satellite → host
            ├─ create cmd pipe  (…-cmd)                    host → satellite
            ├─ create shutdown event (…-shutdown)          host → satellite (one bit)
            ├─ Process.Start(satellite\Fct.LegacyHost.exe --bridge <name>)
            ├─ KillOnExitJob.AddProcess(pid)   ◄── OS-level kill-on-close backstop
            ├─ await pipe connection            Main: ConnectBridge → READY
            ├─ read handshake (60 s ceiling)          LoadPlugins (+250 ms timer)
            │     READY / HWND / PLUGIN… / PLUGINS-END
            └─ PumpBridgeAsync (LOG/EVT/PLUGIN/UNLOADED frames, host lifetime)

Shutdown (either path, idempotent):
  MainWindow.OnClosing  ──┐
  SatelliteLifetime.StopAsync ─┴─ SatelliteHost.ShutdownAsync(8 s)
       ├─ set named shutdown event ──────────► shutdown-waiter thread
       ├─ WaitForExitAsync(8 s)                  └─ Invoke on UI thread:
       └─ on timeout: leave it —                      forwarder.Dispose
          the job object reaps at host exit           FacadeHost.DeInitPlugins
                                                      Application.Exit
```

The **kill-on-close Job Object** (`ProcessJob`, `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) is the
last line of defense: a single static job handle lives for the whole host process; every
satellite is enrolled immediately after spawn. When the host exits — cleanly, by crash, or by
`taskkill /f` — the OS closes the handle and terminates every member, including OverlayPlugin's
CEF subprocesses (spawned later, at plugin load ≥ +250 ms, so they inherit job membership).

## 2. What is solid

| Mechanism | Where | Why it holds |
|---|---|---|
| Kill-on-close job | `src/Fct.Host/ProcessJob.cs` | Static handle, never disposed early; covers crash and force-kill of the host. Enrollment precedes the satellite's child spawning. |
| Graceful drain | `SatelliteHost.ShutdownAsync` + `Program.OnShutdownCommand` | Named-event signal → `DeInitPlugins` on the WinForms UI thread (plugins persist state) → `Application.Exit`; 8 s wait; job reaps on timeout. Idempotent (`HasExited` short-circuit), safe if the satellite never started. |
| Double shutdown callers | `MainWindow.OnClosing` (cancels first close, drains, re-closes) and `SatelliteLifetime.StopAsync` | Second call no-ops once the process has exited. |
| Unique rendezvous names | `fct-bridge-<guid>` pipe/event names per launch | A stale satellite can never cross-connect to a new host instance; two `Fct.App` instances don't interfere. |
| Oracle batch modes exit | `src/Fct.LegacyHost/ParseOracle.cs:139` | `Environment.Exit(0)` in `finally` — the real plugin's foreground threads cannot keep the one-shot process alive. |
| Test fixture teardown | `tests/Fct.Integration.Tests/SatelliteRunFixture.cs:130-135` | `Kill(entireProcessTree: true)` on dispose; ctor swallows launch failures but keeps `_process` set so dispose still kills. |
| Exited notification suppression | `_shuttingDown` flag | The "engine stopped" toast is suppressed during a requested drain, so clean exit isn't reported as a crash. |

## 3. Findings

### F1 — "Restart Classic Engine" spawns a second satellite without stopping the first — HIGH — Open

**Path.** `OverviewView.axaml:47` binds `RestartSatelliteCommand` with no enable-gate →
`MainViewModel.RestartSatellite` (`MainViewModel.cs:213`) → `MainWindow.StartSatelliteAsync`
(fire-and-forget, `MainWindow.axaml.cs:47`) → `SatelliteHost.StartAsync`, which has **no guard
against an already-live `Process`** and never drains or kills the previous instance.

**Effect.** Clicking Restart while the engine is Online (or still Starting) yields two
concurrent satellites: two FFXIV_ACT_Plugin instances, two OverlayPlugin/CEF trees, both
capturing and contending for the MiniParse WebSocket port (10501). Both are job-enrolled, so
nothing survives host exit — but it is a runaway for the whole session. The old
`_server`/`_cmdServer`/`_shutdownEvent`/`Process` fields are silently overwritten and never
disposed (handle leak per restart; `SatelliteHost` is not `IDisposable`).

**Recommendation.**
1. Make `StartAsync` restart-safe: as its first step, if `Process is { HasExited: false }`,
   `await ShutdownAsync(timeout)` and, if still alive, `Process.Kill(entireProcessTree: true)`.
2. Dispose the previous `_server`, `_cmdServer`, `_shutdownEvent`, and `Process` before
   creating the new set (extract a `CleanupPreviousRun()`).
3. Serialize starts with a `SemaphoreSlim(1,1)` so a double-click cannot interleave two
   `StartAsync` executions.
4. Optionally gate `RestartSatelliteCommand` (`CanExecute` on `Host != HostState.Starting`) —
   UI polish, not the safety mechanism; the guard must live in `SatelliteHost`.

### F2 — `StartAsync` hangs forever if the satellite dies before connecting — HIGH — Open

**Path.** `SatelliteHost.cs:134`: `await _server.WaitForConnectionAsync(ct)` — and the only
caller (`MainWindow.StartSatelliteAsync`) passes no token, so `ct = CancellationToken.None`.
The 60 s ceiling (`SatelliteHost.cs:147-148`) starts **after** the connection. If the satellite
crashes at boot before `ConnectBridge` (broken staging, missing .NET Framework runtime, facade
failure), nothing cancels the wait: the `Exited` handler logs, but the host sits in "Starting"
forever with two live pipe servers and an event handle leaked, and the UI never reaches a state
from which recovery is obvious.

**Recommendation.** Cover the connection wait with the same linked-timeout pattern used for the
handshake, and cancel it when the process exits:

```csharp
using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
connectCts.CancelAfter(TimeSpan.FromSeconds(30));
using var exitReg = /* Process.Exited → connectCts.Cancel() */;
try { await _server.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false); }
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // dispose pipes/event, kill the process if it is somehow still alive, then
    throw new InvalidOperationException("Satellite failed to connect to the bridge", …);
}
```

`Process.Exited` is already `EnableRaisingEvents = true`; hook it to the CTS before awaiting.
The thrown exception lands in `StartSatelliteAsync`'s catch → `SetOffline` — the correct UI
outcome. Also handle `Process.Start` returning `null` (currently falls through to the same
infinite wait).

### F3 — Job-enrollment failure aborts the start but leaves a live, un-enrolled satellite — MEDIUM — Open

**Path.** `SatelliteHost.cs:127`: `KillOnExitJob.AddProcess(Process)` throws `Win32Exception`
if `AssignProcessToJobObject` fails. The exception propagates out of `StartAsync`; the UI shows
"stopped" — but the satellite just spawned **keeps running, outside the job**. It is then the
one process in the design that genuinely survives a host kill as an orphan. It also compounds
F1: the user sees "stopped" and clicks Restart, spawning a second satellite next to the
un-enrolled first.

**Recommendation.** Enrollment must be best-effort-but-loud, never start-aborting with the
process left alive. Wrap in try/catch:

```csharp
try { KillOnExitJob.AddProcess(Process); }
catch (Win32Exception ex)
{
    _log.LogWarning(LogEvents.JobObjectUnavailable, ex,
        "Could not enroll satellite {Pid} in the kill-on-close job; not tied to host lifetime",
        SafePid(Process));
    // Either continue un-enrolled (matches the KillOnExitJob == null fallback), or —
    // stricter — Kill(entireProcessTree: true) and rethrow so no unsupervised satellite runs.
}
```

Continue-and-log matches the existing `KillOnExitJob == null` behavior; combined with F4 the
residual orphan risk is closed from the satellite side.

### F4 — No satellite-side host-death detection; the job is the only line of defense — MEDIUM — Open

**Path.** When the job is unavailable (`ProcessJob.TryCreate()` returned null) or enrollment
failed (F3), nothing in the satellite notices the host is gone:

- the shutdown-waiter (`Program.cs:506-532`) blocks forever on the named event — a dead host
  never sets it;
- `SendLine` (`Program.cs:570-578`) swallows broken-pipe writes indefinitely;
- the command-pipe reader (`Program.cs:416-438`) gets `null`/`IOException` when the host's end
  closes, logs "Command pipe reader stopped", and lets the thread die without acting.

The satellite — with FFXIV_ACT_Plugin, OverlayPlugin, and the CEF tree — then runs invisibly
forever after a host crash.

**Recommendation.** Add a cheap watchdog so supervision is two-layered:

1. **Command-pipe rupture ⇒ host gone.** In `StartCommandReader`, after a successful connect,
   treat reader termination (null read or exception) as host death when bridged:
   `if (!_standalone && !_shuttingDown) OnShutdownCommand();`. The graceful path is unaffected —
   the shutdown event fires and sets `_shuttingDown` before the host closes its pipe ends.
2. **(Optional, belt-and-braces) host-PID watch.** Pass the host PID on the command line
   (`--bridge <name> --host-pid <pid>`); a background thread does
   `Process.GetProcessById(pid).WaitForExit()` and calls `OnShutdownCommand()` when it returns.
   This also covers the window where the command pipe never connected.

### F5 — Standalone satellite never exits — MEDIUM — Open

**Path.** `Program.cs:127`: `Application.Run(new ApplicationContext())` has no `MainForm`, so
the message loop never ends on its own. In standalone dev mode (no `--bridge`), the plugin
windows are shown (`Program.cs:165-172`); closing both leaves the process alive with only
hidden windows — Task Manager is the only way out. (Bridged mode is unaffected: the loop ends
via `OnShutdownCommand` or the job.)

**Recommendation.** In the standalone branch, track the shown windows and end the loop when the
last one closes:

```csharp
int open = shown.Count;
foreach (var w in shown)
    w.FormClosed += (_, _) => { if (--open == 0) Application.Exit(); };
```

(Or run the `ApplicationContext` with the FFXIV window as `MainForm`.)

### F6 — `ReplayRouteTests` leaks a hung satellite on timeout — LOW — Open

**Path.** `tests/Fct.Integration.Tests/ReplayRouteTests.cs:53`:
`Assert.True(p.WaitForExit(120_000), …)` — on timeout the assert throws, the `using` block
disposes the `Process` handle, but **`Dispose` does not terminate the process**. A wedged
`--replay` run outlives the test session on a dev box or CI agent.

**Recommendation.** Kill before asserting:

```csharp
if (!p.WaitForExit(120_000))
{
    try { p.Kill(entireProcessTree: true); } catch { }
    Assert.Fail("replay did not finish in time");
}
```

(`SatelliteRunFixture.Dispose` already does this correctly for the fixture-launched satellite.)

### F7 — Small correctness nits — LOW — Open

- **Stale-closure PID in the `Exited` handler.** `SatelliteHost.cs:113-116` reads the
  `Process` *property* inside the handler; after a restart replaces the property, the old
  process's exit logs the **new** process's PID/exit code. Capture a local
  (`var p = Process;`) when subscribing.
- **`_shuttingDown` never resets.** Once `ShutdownAsync` has run (e.g. a timed-out drain
  followed by a restart), a later genuine crash of the new satellite raises no "engine
  stopped" notification. Reset it at the top of `StartAsync` (natural home once F1's
  restart-safe preamble exists).
- **Crash leaves the UI "Online".** `Process.Exited` only publishes a toast; nothing calls
  `MainViewModel.SetOffline`, so the shell shows a running engine with dead plugin HWNDs, and
  the Plugins-page recovery banner (gated on `HasError`) never appears. Surface an event from
  `SatelliteHost` (e.g. `SatelliteExited`) that `MainWindow` routes to `SetOffline` on the UI
  thread when `!_shuttingDown`.

## 4. Recommended fix order

1. **F1 + F2 together** — both live in `SatelliteHost.StartAsync` and share the cleanup helper
   (dispose previous handles, kill previous process, cancel-on-exit connect wait, start
   serialization). This closes every UI-reachable runaway path.
2. **F3** — one try/catch; closes the only true post-host-exit orphan on the host side.
3. **F4** — satellite-side watchdog; makes supervision two-layered so no single failure
   (job creation, enrollment) leaves an immortal satellite.
4. **F7** — fold the `_shuttingDown` reset and captured-local `Exited` handler into the F1
   change; add the `SatelliteExited` → `SetOffline` wiring.
5. **F5, F6** — independent one-liners (dev-mode and test hygiene).

## 5. Explicitly checked, no issue found

- **CEF children escape the job?** No — CefSharp does not use `CREATE_BREAKAWAY_FROM_JOB`;
  children spawned after enrollment inherit membership.
- **Pipe-name collision between instances / after crash-restart?** No — GUID-suffixed names
  per launch.
- **Double shutdown (window close + hosted-service stop)?** Safe — `ShutdownAsync`
  short-circuits on `HasExited` / null process.
- **`PumpBridgeAsync` after satellite exit?** Terminates — pipe close yields a null read;
  logged as "Bridge closed by satellite".
- **Oracle/replay/mass modes leaving foreground threads alive?** No — `RunPumped` forces
  `Environment.Exit(0)`; `EngineAggregator` loads no real plugin.
- **`ProcessJobTests` child leak?** No — `finally` kills the `cmd /c pause` child.
- **Second `Fct.App` instance interference?** None — each host owns its own job object and
  GUID-named rendezvous objects.
