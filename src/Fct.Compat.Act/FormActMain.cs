using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    // Clean-room facade for ACT's main form. Implements only the surface the real
    // FFXIV_ACT_Plugin and OverlayPlugin touch. Derives from Form because the plugins
    // cast it to Control and call Invoke/Visible/InvokeRequired.
    public class FormActMain : Form
    {
        // These delegates are nested types of FormActMain in ACT; plugins reference them as
        // FormActMain+NullDelegate / FormActMain+AttackTypeGraphGenerator.
        public delegate void NullDelegate();
        public delegate System.Drawing.Bitmap AttackTypeGraphGenerator(
            AttackType AttackTypeSource, int SizeX, int SizeY, string Sorting);
        public delegate DateTime DateTimeLogParser(string logLine);
        public delegate void PlaySoundDelegate(string WavFilePath, int VolumePercent);
        public delegate void PlayTtsDelegate(string TtsString);

        // Audio/TTS playback hooks. These exist with ACT's exact delegate types so trigger plugins
        // (Discord-Triggers, TTSYukkuri) that read and override them load and route normally. They must
        // stay plain FIELDS — precompiled plugins read + restore them by ldfld/stfld (Discord-Triggers
        // saves oldTTS then reassigns then restores). Initialized to a no-op SENTINEL (captured below):
        // when a plugin replaces a slot, its reference differs from the sentinel, which is how the
        // satellite detects the takeover to register a terminal host sink (TtsHijacked/SoundHijacked).
        public PlaySoundDelegate PlaySoundMethod;
        public PlayTtsDelegate PlayTtsMethod;
        private readonly PlayTtsDelegate _ttsSentinel;
        private readonly PlaySoundDelegate _soundSentinel;

        // Host-routed service seam (P6): when the satellite installs a route, TTS/PlaySound marshal to
        // the host audio fan-out over the bridge instead of invoking the local slots — every inter-plugin
        // audio path crosses the host (routing invariant). The slots then serve only as the
        // sink-registration signal a plugin toggles. Null in a dev-standalone run and in unit tests, where
        // TTS/PlaySound route to the local slots as before (preserving the last-writer-wins behavior).
        public static IHostServiceRoute ServiceRoute;

        // ACT's audio entry points. Callers (cactbot say/play_sound, Hojoring) invoke these. With a host
        // route installed they cross the bridge to the host's IAudioOutput; otherwise they hit the local
        // delegate slots (silence with the sentinel default). Volume is fixed at 100 (ACT's default).
        public void TTS(string SpeechText)
        {
            var route = ServiceRoute;
            if (route != null) route.Speak(SpeechText, 100, 0, false);
            else PlayTtsMethod?.Invoke(SpeechText);
        }

        public void PlaySound(string WavFilePath)
        {
            var route = ServiceRoute;
            if (route != null) route.PlaySound(WavFilePath, 100);
            else PlaySoundMethod?.Invoke(WavFilePath, 100);
        }

        // True once a plugin has replaced a slot with its own delegate (the ldfld/stfld hijack). The
        // satellite polls these to register/unregister a terminal host sink for this satellite; the
        // fields must stay plain fields, so the takeover is detected by reference, not intercepted.
        public bool TtsHijacked => PlayTtsMethod != null && !ReferenceEquals(PlayTtsMethod, _ttsSentinel);
        public bool SoundHijacked => PlaySoundMethod != null && !ReferenceEquals(PlaySoundMethod, _soundSentinel);

        // --- Named callbacks (Triggernometry peer interop) ---
        // Register/invoke/unregister route through the host so a callback registered in one satellite is
        // invocable from another (routing invariant: every inter-plugin path crosses the host). Local int
        // ids (ACT's convention) map to the host's per-name registration; only string args cross the wire.
        // With no ServiceRoute installed (standalone/unit tests) they degrade to a local in-process registry.
        private int _nextCallbackId = 1;
        private readonly System.Collections.Generic.Dictionary<int, KeyValuePair<string, Action<object>>> _callbacksById
            = new System.Collections.Generic.Dictionary<int, KeyValuePair<string, Action<object>>>();
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> _callbackIdsByName
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>(StringComparer.Ordinal);
        private readonly object _cbLock = new object();

        // Register a named callback; returns an int id (ACT convention) for UnregisterNamedCallback. The
        // first registration for a name installs the host-side proxy for this satellite.
        public int RegisterNamedCallback(string name, Action<object> callback, object owner = null, string registrant = null)
        {
            if (string.IsNullOrEmpty(name) || callback == null) return -1;
            int id; bool firstForName;
            lock (_cbLock)
            {
                id = _nextCallbackId++;
                _callbacksById[id] = new KeyValuePair<string, Action<object>>(name, callback);
                if (!_callbackIdsByName.TryGetValue(name, out var list))
                { list = new System.Collections.Generic.List<int>(); _callbackIdsByName[name] = list; }
                firstForName = list.Count == 0;
                list.Add(id);
            }
            if (firstForName) ServiceRoute?.RegisterCallback(name, true);
            return id;
        }

        // Unregister by id; the last registration for a name releases this satellite's host proxy.
        public void UnregisterNamedCallback(int id)
        {
            string name = null; bool lastForName = false;
            lock (_cbLock)
            {
                if (_callbacksById.TryGetValue(id, out var entry))
                {
                    name = entry.Key;
                    _callbacksById.Remove(id);
                    if (_callbackIdsByName.TryGetValue(name, out var list))
                    {
                        list.Remove(id);
                        lastForName = list.Count == 0;
                        if (lastForName) _callbackIdsByName.Remove(name);
                    }
                }
            }
            if (lastForName && name != null) ServiceRoute?.UnregisterCallback(name);
        }

        // Invoke a named callback. The host is the single fan-out point: marshal up (do NOT invoke
        // locally); the host fans back down to every owner, including this origin, which dispatches via
        // InvokeNamedCallbackLocal. With no route, invoke the local registry directly (standalone fallback).
        public void InvokeNamedCallback(string name, object argument = null)
        {
            var route = ServiceRoute;
            if (route != null) route.InvokeCallback(name, argument);
            else InvokeNamedCallbackLocal(name, argument);
        }

        // Dispatch a host-fanned invoke to every locally-registered callback for the name (called from the
        // satellite's command reader with the decoded string argument).
        public void InvokeNamedCallbackLocal(string name, object argument)
        {
            Action<object>[] targets;
            lock (_cbLock)
            {
                if (!_callbackIdsByName.TryGetValue(name, out var list) || list.Count == 0) return;
                targets = list.Select(id => _callbacksById[id].Value).ToArray();
            }
            foreach (var cb in targets) { try { cb(argument); } catch (Exception ex) { Log($"[NamedCallback] '{name}' threw: {ex.Message}"); } }
        }

        // ACT exposes this as a public delegate FIELD (plugins assign their own parser).
        public DateTimeLogParser GetDateTimeFromLog;

        // Diagnostics sink (the satellite routes this to a log file).
        public static Action<string> Log = _ => { };

        // Live capture counters (verification).
        public int AddCombatActionCount;
        public int SetEncounterCount;
        public int ChangeZoneCount;

        // The shared, runtime-neutral encounter state machine (auto-start + idle-end). This facade is
        // the ACT event/WinForms surface over it; the modern net10 engine drives an identical copy so
        // both runtimes start and end an encounter at the same instant.
        private readonly EncounterLifecycle _lifecycle = new EncounterLifecycle();

        public FormActMain()
        {
            // Capture the no-op audio slots as sentinels so a plugin's takeover is detectable by reference
            // (see TtsHijacked/SoundHijacked). Kept as fields because precompiled plugins bind them via
            // ldfld/stfld.
            _ttsSentinel = tts => { };
            _soundSentinel = (wav, vol) => { };
            PlayTtsMethod = _ttsSentinel;
            PlaySoundMethod = _soundSentinel;

            // Off-screen + fully transparent + non-activating: invisible to the user, but a genuinely
            // shown window so Control.Visible is true. FFXIV_ACT_Plugin's ScanMemory and LogOutput
            // threads park on ACTWrapper.IsMainFormVisible() (which reads this form's Visible) before
            // producing any combat data or writing the network log — a hidden form freezes them.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
            AppDataFolder = new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Advanced Combat Tracker"));
            _lifecycle.CombatStarted = enc =>
                OnCombatStart?.Invoke(false, new CombatToggleEventArgs(0, _lifecycle.ActiveZone.Items.Count - 1, enc));
            _lifecycle.CombatEnded = enc =>
                OnCombatEnd?.Invoke(false, new CombatToggleEventArgs(0, 0, enc));
            ZoneList.Add(ActiveZone);
            CombatTables.Setup();
        }

        // ACT's number renderer for the "-*" / "-k|m|b" ExportVariables. Delegates to the shared
        // stateless DamageString.Create (linked from shared/Aggregation) so external net48 callers
        // that reach through oFormActMain keep working with one implementation.
        public string CreateDamageString(long damage, bool useSuffix, bool useDecimals)
            => DamageString.Create(damage, useSuffix, useDecimals);

        // Show without taking focus, so realizing this off-screen form never pulls the foreground
        // away from the game.
        protected override bool ShowWithoutActivation => true;

        // --- State the shims read/write ---
        public DirectoryInfo AppDataFolder { get; set; }
        public string LogFilePath { get; set; } = "";
        public string LogFileFilter { get; set; } = "";
        public int GlobalTimeSorter { get; set; }
        public bool InCombat { get => _lifecycle.InCombat; set => _lifecycle.InCombat = value; }
        public int TimeStampLen { get; set; } = 15;
        public bool LogPathHasCharName { get; set; }
        public Regex ZoneChangeRegex { get; set; } = new Regex(@"01:Changed Zone to (?<ZoneName>.*)\.");
        public bool ReadThreadLock { get; set; }
        public DateTime LastKnownTime { get => _lifecycle.LastKnownTime; set => _lifecycle.LastKnownTime = value; }
        public DateTime LastEstimatedTime { get => _lifecycle.LastEstimatedTime; set => _lifecycle.LastEstimatedTime = value; }
        public DateTime LastHostileTime { get => _lifecycle.LastHostileTime; set => _lifecycle.LastHostileTime = value; }

        // Idle-end of combat (ACT CheckIdleEndCombat). nudIdleLimit_Value defaults to 6 s.
        public bool IdleEndEnabled { get => _lifecycle.IdleEndEnabled; set => _lifecycle.IdleEndEnabled = value; }
        public double IdleLimitSeconds { get => _lifecycle.IdleLimitSeconds; set => _lifecycle.IdleLimitSeconds = value; }
        public string CurrentZone { get => _lifecycle.CurrentZone; set => _lifecycle.CurrentZone = value; }
        public AttackTypeGraphGenerator GenerateAttackTypeGraph { get; set; }
        public List<ActPluginData> ActPlugins { get; } = new List<ActPluginData>();
        public bool InitActDone { get; set; } = true;
        public bool IsActClosing { get; set; }
        public ZoneData ActiveZone { get => _lifecycle.ActiveZone; set => _lifecycle.ActiveZone = value; }

        // Zone/encounter history. We keep a single live zone, so ZoneList holds ActiveZone; this is the
        // surface Triggernometry walks for past-encounter export.
        public List<ZoneData> ZoneList { get; } = new List<ZoneData>();

        // ACT's user-defined custom triggers. We host no trigger engine; these stay empty so
        // Triggernometry's HasCustomTriggers()/GetCustomTriggers() import bind and find nothing to
        // import (its own triggers still run off the log stream).
        public SortedList<string, CustomTrigger> CustomTriggers { get; } = new SortedList<string, CustomTrigger>();
        public SortedList<string, CustomTrigger> ActiveCustomTriggers { get; } = new SortedList<string, CustomTrigger>();

        // Triggernometry's export hooks reflect this private field by name and pass it to GetTextExport.
        // Non-null so that reflected read + call never NRE. Default args match ACT.
        private readonly TextExportFormatOptions defaultTextFormat =
            new TextExportFormatOptions("{n}{NAME5} | {encdps-*}", "EncDPS", true, true,
                "({duration}) {title}: {encdps-*} {maxhit-*}");
        public TextExportFormatOptions DefaultTextFormat => defaultTextFormat;

        // --- Events plugins subscribe to ---
        public event NullDelegate UpdateCheckClicked;
        public event LogLineEventDelegate BeforeLogLineRead;
        public event LogLineEventDelegate OnLogLineRead;
        public event LogFileChangedDelegate LogFileChanged;
        public event CombatToggleEventDelegate OnCombatStart;
        public event CombatToggleEventDelegate OnCombatEnd;
        public event CombatActionDelegate BeforeCombatAction;
        public event CombatActionDelegate AfterCombatAction;

        // Bridge taps: the modern net10 engine mirrors the encounter lifecycle the plugin drives here,
        // so it aggregates the same swings into the same encounters. Not part of ACT's surface —
        // internal to the satellite; BridgeForwarder subscribes and forwards the typed requests.
        public event Action<DateTime, string, string> EncounterSetRaised;
        public event Action<string> ZoneChangeRaised;
        public event Action<bool> CombatEndRaised;

        // No-op publishers: ACT raises these from UI/clipboard/XML-share/URL paths the headless
        // host doesn't have. Declared so an unmodified plugin's `+=` binds (a `+=` to a missing
        // event is a MissingMethodException); never fired here.
        public event ActLifecycleEventDelegate ActLifecycleChanged;
        public event LogFileRenamedDelegate LogFileRenamed;
        public event XmlSnippetAddedDelegate XmlSnippetAdded;
        public event ClipboardEventDelegate BeforeClipboardSet;
        public event UrlRequestEventDelegate UrlRequest;

        // --- Version / plugin host ---
        public Version GetVersion() => new Version(3, 8, 5, 288);

        public ActPluginData PluginGetSelfData(IActPluginV1 plugin) =>
            ActPlugins.FirstOrDefault(p =>
                ReferenceEquals(p.pluginObj, plugin) ||
                ReferenceEquals((p.pluginObj as IActPluginAlias)?.Inner, plugin));

        public FileInfo PluginDownload(int myPluginId) => null;
        // Self-update is out of scope. Return a low version (not "") so the plugins' update check
        // (`new Version(...)` then `local < remote`) parses without throwing and evaluates false.
        public string PluginGetRemoteVersion(int myPluginId) => "0.0.0.0";
        public bool GetAutomaticUpdatesAllowed() => false;

        public void RestartACT(bool stopOnError, string message) =>
            Log($"[RestartACT] stopOnError={stopOnError} :: {message}");

        // ACT's signature is one method with optional trailing params; OverlayPlugin's CheckACTVersion
        // binds to the 4-arg form, older callers to the 2-arg form. We host no notification tray, so
        // the callback/sender are accepted and ignored (logged like the rest).
        public void NotificationAdd(string title, string text, EventHandler showCallback = null, object senderObject = null) =>
            Log($"[Notify] {title}: {text}");

        // Host-window DPI scale. ACT derives this from a design-96px control auto-scaled by WinForms
        // (pDpiSize.Width / 96); DeviceDpi/96 is the equivalent now that the satellite process is
        // System-DPI-aware (app.manifest <dpiAware>true</dpiAware> + EnableWindowsFormsHighDpiAutoResizing).
        // OverlayPlugin multiplies its config-tab layout by this; Hojoring/ACT-painted controls scale by
        // it too. The facade form is Show()n off-screen (see FacadeHost.CreateAct), so DeviceDpi is live.
        public float DpiScale => DeviceDpi / 96f;

        // ACT's corner-button tray. We host no chrome to place the control in, but Hojoring calls
        // these unguarded, so they must exist and survive add-then-remove. We track the controls in
        // a list (never displayed) so the round-trip is real rather than throwing.
        private readonly List<Control> cornerControls = new List<Control>();
        public void CornerControlAdd(Control c) { if (c != null && !cornerControls.Contains(c)) cornerControls.Add(c); }
        public void CornerControlRemove(Control c) { cornerControls.Remove(c); }

        // Encounter text export. Triggernometry/Discord call this for the active or a past encounter,
        // passing the reflected defaultTextFormat. We host no formatter engine, so this returns a
        // concise fixed summary — export-driven trigger actions get non-null text instead of throwing.
        public string GetTextExport(EncounterData Encounter, TextExportFormatOptions ExportFormatting) =>
            Encounter == null ? "" : $"({Encounter.DurationS}) {Encounter.Title}";

        public void WriteExceptionLog(Exception ex, string moreInfo) =>
            Log($"[Exception] {moreInfo}\n{ex}");

        public void WriteInfoLog(string message) => Log($"[Info] {message}");
        public void WriteDebugLog(string message) => Log($"[Debug] {message}");

        // Raise BeforeLogLineRead from outside the class (events can only be raised by their
        // declaring type). The FFXIV plugin subscribes to this to drive its log parser, so the
        // oracle harness uses it to replay real log lines through the plugin's decode.
        public void FireBeforeLogLineRead(bool isImport, LogLineEventArgs args)
            => BeforeLogLineRead?.Invoke(isImport, args);

        public void FireLogLineRead(bool isImport, LogLineEventArgs args)
            => OnLogLineRead?.Invoke(isImport, args);

        // Idle-end hook for the live log pump: the host calls AdvanceClock(lineTime) as lines are
        // read so combat ends after a quiet gap. Not driven from the Fire* events here, because the
        // oracle replays lines through them and must capture every swing regardless of InCombat.

        // --- Log file integration (plugin tails/writes Network_*.log) ---
        // The FFXIV plugin formats decoded packets into pipe-delimited lines and appends them to
        // Network_*.log, then calls OpenLog to have ACT tail that file. ACT's log reader fires
        // BeforeLogLineRead per line; the plugin's ParseStrategy pipeline (subscribed to it) turns
        // each line into AddCombatAction. That tail IS the live combat path — without it the file
        // grows but no combat reaches the engine. We start a background tail that does exactly this.
        public void OpenLog(bool getCurrentZone, bool getCharNameFromFile)
        {
            Log($"[OpenLog] path={LogFilePath} filter={LogFileFilter} getZone={getCurrentZone}");
            LogFileChanged?.Invoke(false, LogFilePath);
            StartLogTail(LogFilePath);
        }

        private readonly object _tailGate = new object();
        private Thread _tailThread;
        private CancellationTokenSource _tailCts;
        private string _tailingPath;

        private void StartLogTail(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_tailGate)
            {
                if (string.Equals(_tailingPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _tailThread != null && _tailThread.IsAlive)
                    return; // already tailing this file (OpenLog can be called repeatedly)
                _tailCts?.Cancel();
                _tailingPath = path;
                var cts = new CancellationTokenSource();
                _tailCts = cts;
                _tailThread = new Thread(() => TailLoop(path, cts.Token))
                { IsBackground = true, Name = "act-logtail" };
                _tailThread.Start();
            }
        }

        public void StopLogTail()
        {
            lock (_tailGate) { _tailCts?.Cancel(); }
        }

        // Tail the live log: start at the current end (so the day's existing history is not replayed
        // as a bogus encounter) and feed only newly-appended lines. Split on newline at the byte level
        // so a partially-written line is never fed until it is complete, and so multi-byte UTF-8 names
        // never split across reads.
        private void TailLoop(string path, CancellationToken ct)
        {
            try
            {
                for (int i = 0; i < 200 && !File.Exists(path); i++)
                    if (ct.WaitHandle.WaitOne(100)) return;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    long pos = fs.Length;             // live: only lines appended from now on
                    var line = new List<byte>(512);
                    var chunk = new byte[16384];
                    while (!ct.IsCancellationRequested)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        int read, any = 0;
                        while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            any += read;
                            pos += read;
                            for (int i = 0; i < read; i++)
                            {
                                byte b = chunk[i];
                                if (b == (byte)'\n')
                                {
                                    int len = line.Count;
                                    if (len > 0 && line[len - 1] == (byte)'\r') len--;
                                    if (len > 0) FeedLine(Encoding.UTF8.GetString(line.ToArray(), 0, len));
                                    line.Clear();
                                }
                                else line.Add(b);
                            }
                        }
                        if (any == 0 && ct.WaitHandle.WaitOne(50)) break;
                    }
                }
            }
            catch (Exception ex) { Log($"[LogTail] {Path.GetFileName(path)} error: {ex.Message}"); }
        }

        // Replay one tailed line through the plugin's parser, mirroring the proven oracle pump:
        // advance the idle clock by the line's timestamp, then raise BeforeLogLineRead (drives the
        // plugin's ParseStrategy → AddCombatAction) and OnLogLineRead (cactbot/triggers).
        private void FeedLine(string raw)
        {
            var ts = ParseLineTimestamp(raw);
            if (ts > DateTime.MinValue) AdvanceClock(ts);
            var args = new LogLineEventArgs(raw, 0, ts, CurrentZone ?? "", InCombat);
            try
            {
                FireBeforeLogLineRead(false, args);
                FireLogLineRead(false, args);
            }
            catch (Exception ex) { Log($"[LogTail] parse error: {ex.Message}"); }
        }

        // FFXIV log lines are "<type>|<ISO-8601 timestamp>|...": the timestamp is the second field.
        private static DateTime ParseLineTimestamp(string raw)
        {
            int p = raw.IndexOf('|');
            int p2 = p >= 0 ? raw.IndexOf('|', p + 1) : -1;
            if (p >= 0 && p2 > p &&
                DateTime.TryParse(raw.Substring(p + 1, p2 - p - 1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts))
                return ts;
            return DateTime.MinValue;
        }

        public void ActCommands(string commandText) => Log($"[ActCommands] {commandText}");

        // --- Combat pipeline: feed the shared aggregation engine through the lifecycle ---
        // The state machine (auto-start, idle-end, zone/encounter transitions) lives in
        // EncounterLifecycle; this facade keeps the ACT event surface (Before/AfterCombatAction,
        // OnCombatStart/End, wired to the lifecycle's CombatStarted/CombatEnded hooks) and the
        // verification counters around it.
        public void AddCombatAction(MasterSwing action)
        {
            AddCombatActionCount++;
            _lifecycle.LastKnownTime = action.Time;
            _lifecycle.LastEstimatedTime = action.Time;
            BeforeCombatAction?.Invoke(false, new CombatActionEventArgs(action));
            _lifecycle.ActiveZone.ActiveEncounter?.AddCombatAction(action);
            AfterCombatAction?.Invoke(false, new CombatActionEventArgs(action));
        }

        public bool SetEncounter(DateTime time, string attacker, string victim)
        {
            SetEncounterCount++;
            var result = _lifecycle.SetEncounter(time, attacker, victim);
            EncounterSetRaised?.Invoke(time, attacker, victim);
            return result;
        }

        // Advance the parse clock as log lines are read. Combat ends after an idle gap, matching
        // ACT's CheckIdleEndCombat (LastKnownTime - LastHostileTime > nudIdleLimit). The FFXIV
        // plugin reads InCombat back off this form to gate which heals it reports, so driving this
        // from the host's log pump is what makes in-/out-of-combat heal attribution match ACT.
        public bool AdvanceClock(DateTime time) => _lifecycle.AdvanceClock(time);

        public bool CheckIdleEndCombat() => _lifecycle.CheckIdleEndCombat();

        public void ChangeZone(string zoneName)
        {
            ChangeZoneCount++;
            _lifecycle.ChangeZone(zoneName);
            ZoneChangeRaised?.Invoke(zoneName);
            Log($"[ChangeZone] {zoneName}");
        }

        // When set (consumer satellites, P9a), EndCombat routes UP the bridge instead of ending the local
        // replica: the host ends the authoritative encounter and fans EndCombatRequested back down so every
        // replica ends in one bus order. The producer + dev-standalone leave this false — the producer keeps
        // its in-band CombatEndRaised path so parser-driven EndCombat stays ordered within the swing stream.
        public static bool RouteEndCombatUp;

        public void EndCombat(bool actExport)
        {
            var route = ServiceRoute;
            if (RouteEndCombatUp && route != null)
            {
                route.EndCombat(actExport);   // consumer: route up; the fan-back applies it (no local end)
                return;
            }
            EndCombatLocal(actExport);
        }

        // The non-routing apply. Used by the producer/dev-standalone path above and by the consumer's
        // downstream fan-back (Program.FoldConsume) so the fanned EndCombatRequested does not re-route up.
        // Public (not an ACT member) so the satellite can call it across the assembly boundary.
        public void EndCombatLocal(bool actExport)
        {
            _lifecycle.EndCombat(actExport);
            CombatEndRaised?.Invoke(actExport);
        }

        // EncounterData.AddCombatAction tracks every combatant in this slice.
        public bool SelectiveListGetSelected(string name) => true;

        // ACT table-UI refresh hooks (no UI table here).
        public void ValidateLists() { }
        public void ValidateTableSetup() { }

        // Suppress unused-event warnings for events wired only via the public API.
        private void TouchEvents()
        {
            _ = UpdateCheckClicked; _ = BeforeLogLineRead; _ = OnLogLineRead;
            _ = ActLifecycleChanged; _ = LogFileRenamed; _ = XmlSnippetAdded;
            _ = BeforeClipboardSet; _ = UrlRequest;
        }
    }
}
