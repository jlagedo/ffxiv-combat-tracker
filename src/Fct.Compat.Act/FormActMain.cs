using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        // Audio/TTS playback hooks. Audio triggers are out of scope, so these are no-op sinks —
        // but they exist with ACT's exact delegate types so trigger plugins (e.g. Discord-Triggers)
        // that read and override them load and route their combat events normally.
        public PlaySoundDelegate PlaySoundMethod = (wav, vol) => { };
        public PlayTtsDelegate PlayTtsMethod = tts => { };

        // ACT's audio entry points. Callers (cactbot say/play_sound, Hojoring) invoke these
        // methods, which route to the swappable delegate fields above. Discord-Triggers (and
        // TTSYukkuri) install the real sink by swapping those fields, so anything calling these
        // lands in the installed audio stack. We skip ACT's speech-correction/volume/caching
        // bookkeeping; the no-op delegate defaults degrade to silence without a sink. Volume is
        // fixed at 100 (ACT's PlaySound(string) default).
        public void TTS(string SpeechText) => PlayTtsMethod?.Invoke(SpeechText);
        public void PlaySound(string WavFilePath) => PlaySoundMethod?.Invoke(WavFilePath, 100);

        // ACT exposes this as a public delegate FIELD (plugins assign their own parser).
        public DateTimeLogParser GetDateTimeFromLog;

        // Diagnostics sink (the satellite routes this to a log file).
        public static Action<string> Log = _ => { };

        // Live capture counters (verification).
        public int AddCombatActionCount;
        public int SetEncounterCount;
        public int ChangeZoneCount;

        public FormActMain()
        {
            // Never shown; exists for its handle + message pump + Invoke marshaling.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Visible = false;
            AppDataFolder = new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Advanced Combat Tracker"));
            ZoneList.Add(ActiveZone);
            CombatTables.Setup();
        }

        // ACT's number renderer for the "-*" / "-k|m|b" ExportVariables: optional K/M/B/T/Q suffix
        // (two decimals when UseDecimals, integer division otherwise); plain "{Damage}" with no
        // suffix or below the smallest threshold. NaN/Infinity sentinels surface verbatim.
        public string CreateDamageString(long damage, bool useSuffix, bool useDecimals)
        {
            if (damage == long.MinValue) return float.NaN.ToString();
            if (damage == long.MaxValue) return float.PositiveInfinity.ToString();
            if (damage < 0) return new Dnum(damage).ToString();
            if (useSuffix)
            {
                if (useDecimals)
                {
                    if (damage >= 1000000000000000L) return $"{(double)damage / 1000000000000000.0:0.00}Q";
                    if (damage >= 1000000000000L) return $"{(double)damage / 1000000000000.0:0.00}T";
                    if (damage >= 1000000000L) return $"{(double)damage / 1000000000.0:0.00}B";
                    if (damage >= 1000000L) return $"{(double)damage / 1000000.0:0.00}M";
                    if (damage >= 1000L) return $"{(double)damage / 1000.0:0.00}K";
                }
                else
                {
                    if (damage >= 10000000000000000L) return $"{damage / 1000000000000000L}Q";
                    if (damage >= 10000000000000L) return $"{damage / 1000000000000L}T";
                    if (damage >= 10000000000L) return $"{damage / 1000000000L}B";
                    if (damage >= 10000000L) return $"{damage / 1000000L}M";
                    if (damage >= 10000L) return $"{damage / 1000L}K";
                }
            }
            return $"{damage}";
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        // --- State the shims read/write ---
        public DirectoryInfo AppDataFolder { get; set; }
        public string LogFilePath { get; set; } = "";
        public string LogFileFilter { get; set; } = "";
        public int GlobalTimeSorter { get; set; }
        public bool InCombat { get; set; }
        public int TimeStampLen { get; set; } = 15;
        public bool LogPathHasCharName { get; set; }
        public Regex ZoneChangeRegex { get; set; } = new Regex(@"01:Changed Zone to (?<ZoneName>.*)\.");
        public bool ReadThreadLock { get; set; }
        public DateTime LastKnownTime { get; set; } = DateTime.Now;
        public DateTime LastEstimatedTime { get; set; } = DateTime.Now;
        public DateTime LastHostileTime { get; set; }

        // Idle-end of combat (ACT CheckIdleEndCombat). nudIdleLimit_Value defaults to 6 s.
        public bool IdleEndEnabled { get; set; } = true;
        public double IdleLimitSeconds { get; set; } = 6;
        public string CurrentZone { get; set; } = "";
        public AttackTypeGraphGenerator GenerateAttackTypeGraph { get; set; }
        public List<ActPluginData> ActPlugins { get; } = new List<ActPluginData>();
        public bool InitActDone { get; set; } = true;
        public bool IsActClosing { get; set; }
        public ZoneData ActiveZone { get; set; } = new ZoneData();

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

        // Host-window DPI scale. OP reads this to scale its config tab (in a try/catch → default 1);
        // we render no ACT chrome, so a constant 1f is correct and removes OP's silent catch.
        public float DpiScale => 1f;

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
        public void OpenLog(bool getCurrentZone, bool getCharNameFromFile)
        {
            Log($"[OpenLog] path={LogFilePath} filter={LogFileFilter}");
            LogFileChanged?.Invoke(false, LogFilePath);
        }

        public void ActCommands(string commandText) => Log($"[ActCommands] {commandText}");

        // --- Combat pipeline: feed the clean-room aggregation engine ---
        public void AddCombatAction(MasterSwing action)
        {
            AddCombatActionCount++;
            LastKnownTime = action.Time;
            LastEstimatedTime = action.Time;
            BeforeCombatAction?.Invoke(false, new CombatActionEventArgs(action));
            ActiveZone.ActiveEncounter?.AddCombatAction(action);
            AfterCombatAction?.Invoke(false, new CombatActionEventArgs(action));
        }

        public bool SetEncounter(DateTime time, string attacker, string victim)
        {
            SetEncounterCount++;
            LastKnownTime = time;
            if (!InCombat || ActiveZone.ActiveEncounter == null || !ActiveZone.ActiveEncounter.Active)
            {
                var enc = new EncounterData(ActGlobals.charName, CurrentZone, ActiveZone) { Active = true };
                enc.StartTimes.Add(time);
                ActiveZone.ActiveEncounter = enc;
                ActiveZone.Items.Add(enc);
                InCombat = true;
                OnCombatStart?.Invoke(false, new CombatToggleEventArgs(0, ActiveZone.Items.Count - 1, enc));
            }
            // Every hostile action refreshes the idle clock (ACT SetEncounter tail).
            LastHostileTime = time;
            return true;
        }

        // Advance the parse clock as log lines are read. Combat ends after an idle gap, matching
        // ACT's CheckIdleEndCombat (LastKnownTime - LastHostileTime > nudIdleLimit). The FFXIV
        // plugin reads InCombat back off this form to gate which heals it reports, so driving this
        // from the host's log pump is what makes in-/out-of-combat heal attribution match ACT.
        public bool AdvanceClock(DateTime time)
        {
            if (time > LastKnownTime) LastKnownTime = time;
            return CheckIdleEndCombat();
        }

        public bool CheckIdleEndCombat()
        {
            if (InCombat && IdleEndEnabled &&
                LastKnownTime - LastHostileTime > TimeSpan.FromSeconds(IdleLimitSeconds))
            {
                EndCombat(true);
                return true;
            }
            return false;
        }

        public void ChangeZone(string zoneName)
        {
            ChangeZoneCount++;
            CurrentZone = zoneName;
            ActiveZone.ZoneName = zoneName;
            Log($"[ChangeZone] {zoneName}");
        }

        public void EndCombat(bool actExport)
        {
            if (InCombat)
            {
                InCombat = false;
                var enc = ActiveZone.ActiveEncounter;
                if (enc != null)
                {
                    enc.EndCombat(actExport);
                    OnCombatEnd?.Invoke(false, new CombatToggleEventArgs(0, 0, enc));
                }
            }
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
