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
            CombatTables.Setup();
        }

        public string CreateDamageString(Dnum damage, bool useSuffix, bool useDecimals)
            => ((long)damage).ToString();

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
        public string CurrentZone { get; set; } = "";
        public AttackTypeGraphGenerator GenerateAttackTypeGraph { get; set; }
        public List<ActPluginData> ActPlugins { get; } = new List<ActPluginData>();
        public bool InitActDone { get; set; } = true;
        public bool IsActClosing { get; set; }
        public ZoneData ActiveZone { get; set; } = new ZoneData();

        // --- Events plugins subscribe to ---
        public event NullDelegate UpdateCheckClicked;
        public event LogLineEventDelegate BeforeLogLineRead;
        public event LogLineEventDelegate OnLogLineRead;
        public event LogFileChangedDelegate LogFileChanged;
        public event CombatToggleEventDelegate OnCombatStart;
        public event CombatToggleEventDelegate OnCombatEnd;
        public event CombatActionDelegate BeforeCombatAction;
        public event CombatActionDelegate AfterCombatAction;

        // --- Version / plugin host ---
        public Version GetVersion() => new Version(3, 8, 5, 288);

        public ActPluginData PluginGetSelfData(IActPluginV1 plugin) =>
            ActPlugins.FirstOrDefault(p => ReferenceEquals(p.pluginObj, plugin));

        public FileInfo PluginDownload(int myPluginId) => null;
        public string PluginGetRemoteVersion(int myPluginId) => "";
        public bool GetAutomaticUpdatesAllowed() => false;

        public void RestartACT(bool stopOnError, string message) =>
            Log($"[RestartACT] stopOnError={stopOnError} :: {message}");

        public void NotificationAdd(string title, string text) => Log($"[Notify] {title}: {text}");

        public void WriteExceptionLog(Exception ex, string moreInfo) =>
            Log($"[Exception] {moreInfo}\n{ex}");

        public void WriteInfoLog(string message) => Log($"[Info] {message}");
        public void WriteDebugLog(string message) => Log($"[Debug] {message}");

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
            return true;
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
        }
    }
}
