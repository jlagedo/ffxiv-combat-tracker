using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using FFXIV_ACT_Plugin.Common;
using Timer = System.Windows.Forms.Timer;   // UI-thread timer (driven by the satellite message loop)

namespace Fct.StreamProbe
{
    // A real IActPluginV1 that wires up the whole data path and logs it, so a live run can be
    // captured and verified end-to-end before any forwarder/bridge work is committed. It is the
    // legacy-side prototype of the BridgeForwarder: same discovery, same subscriptions, same
    // projection points — only the sink differs (a log file instead of the bridge).
    //
    // Two contracts are tapped:
    //   * the FFXIV_ACT_Plugin SDK stream (IDataSubscription, 11 events) — discovered by reflecting
    //     ActGlobals.oFormActMain.ActPlugins exactly as OverlayPlugin's FFXIVRepository does;
    //   * ACT's aggregate (OnLogLineRead / combat events + the live ExportVariables rollup cactbot
    //     reads) — tapped on the facade hub.
    //
    // Strictly read-only: it never calls AddCombatAction/SetEncounter/ChangeZone or mutates any
    // model, so it cannot move the engine off its ACT-parity baseline.
    public sealed class StreamProbePlugin : IActPluginV1
    {
        private ProbeLog _log;
        private Label _status;
        private IDataSubscription _sub;
        private IDataRepository _repo;
        private Timer _discover;   // polls until the FFXIV plugin SDK is reachable
        private Timer _snapshot;   // periodic counters + ExportVariables readout

        // Counters incremented from the dispatch thread (Interlocked) — totals printed by Snapshot.
        private long _logLine, _parsedLogLine, _networkReceived, _networkSent;
        private long _combatantAdded, _combatantRemoved, _partyListChanged, _primaryPlayerChanged;
        private long _playerStatsChanged, _zoneChanged, _processChanged;
        private long _actLogLine, _afterCombatAction, _combatStart, _combatEnd;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _status = pluginStatusText;
            _log = new ProbeLog();
            _log.Write("INIT", $"Fct.StreamProbe starting. pid={Process.GetCurrentProcess().Id} " +
                               $"act={SafeType(ActGlobals.oFormActMain)} base={AppDomain.CurrentDomain.BaseDirectory}");

            // ACT-side taps are live immediately — the facade hub exists before any plugin loads.
            TapActEvents();

            // The FFXIV plugin SDK may not be wired yet (load order), so poll until it is.
            _discover = new Timer { Interval = 500 };
            _discover.Tick += (s, e) => TryBindFfxiv();
            _discover.Start();

            // Periodic heartbeat: counters + the aggregate OverlayPlugin/cactbot read.
            _snapshot = new Timer { Interval = 5000 };
            _snapshot.Tick += (s, e) => Snapshot();
            _snapshot.Start();

            if (_status != null) _status.Text = "Fct.StreamProbe wired";
        }

        // ---- Discovery (the same seam OverlayPlugin's FFXIVRepository uses) -------------------

        private void TryBindFfxiv()
        {
            try
            {
                var act = ActGlobals.oFormActMain;
                if (act == null) return;

                var ffxiv = act.ActPlugins.FirstOrDefault(p =>
                    p.cbEnabled.Checked && p.pluginObj != null &&
                    p.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
                if (ffxiv == null) return;

                var obj = ffxiv.pluginObj;
                var sub = obj.GetType().GetProperty("DataSubscription")?.GetValue(obj) as IDataSubscription;
                var repo = obj.GetType().GetProperty("DataRepository")?.GetValue(obj) as IDataRepository;
                if (sub == null)
                {
                    _log.Write("DISCOVER", $"found FFXIV plugin ({SafeType(obj)}) but DataSubscription is null/uncastable; retrying");
                    return;
                }

                _discover.Stop(); _discover.Dispose(); _discover = null;
                _sub = sub; _repo = repo;
                _log.Write("DISCOVER", $"bound. pluginObj={SafeType(obj)} sub={SafeType(sub)} repo={SafeType(repo)}");

                SubscribeSdk(sub);
                DumpRepository(repo);
            }
            catch (Exception ex) { _log.Write("DISCOVER", "ERROR " + ex.Message); }
        }

        private void SubscribeSdk(IDataSubscription sub)
        {
            sub.LogLine += OnLogLine;
            sub.ParsedLogLine += OnParsedLogLine;
            sub.NetworkReceived += OnNetworkReceived;
            sub.NetworkSent += OnNetworkSent;
            sub.CombatantAdded += OnCombatantAdded;
            sub.CombatantRemoved += OnCombatantRemoved;
            sub.PartyListChanged += OnPartyListChanged;
            sub.PrimaryPlayerChanged += OnPrimaryPlayerChanged;
            sub.PlayerStatsChanged += OnPlayerStatsChanged;
            sub.ZoneChanged += OnZoneChanged;
            sub.ProcessChanged += OnProcessChanged;
            _log.Write("SDK", "subscribed to all 11 IDataSubscription events");
        }

        private void DumpRepository(IDataRepository repo)
        {
            if (repo == null) { _log.Write("REPO", "DataRepository null — skipping state dump"); return; }
            try
            {
                string lang = Safe(() => repo.GetSelectedLanguageID().ToString());
                string ver = Safe(() => repo.GetGameVersion());
                string terr = Safe(() => repo.GetCurrentTerritoryID().ToString());
                string pid = Safe(() => repo.GetCurrentPlayerID().ToString());
                string proc = Safe(() => repo.GetCurrentFFXIVProcess()?.Id.ToString() ?? "(no process)");
                string cbs = Safe(() => repo.GetCombatantList()?.Count.ToString() ?? "0");
                _log.Write("REPO", $"lang={lang} gameVer={ver} territory={terr} playerId={pid} ffxivPid={proc} combatants={cbs}");
            }
            catch (Exception ex) { _log.Write("REPO", "ERROR " + ex.Message); }
        }

        // ---- SDK event handlers (signatures match FFXIV_ACT_Plugin.Common delegates) ----------

        private void OnLogLine(uint eventType, uint seconds, string line)
        {
            long n = Interlocked.Increment(ref _logLine);
            if (Sample(n)) _log.Write("SDK.LOG", $"#{n} type={eventType} {Trunc(line, 200)}");
        }

        private void OnParsedLogLine(uint sequence, int messagetype, string message)
        {
            long n = Interlocked.Increment(ref _parsedLogLine);
            if (Sample(n)) _log.Write("SDK.PLOG", $"#{n} mt={messagetype} {Trunc(message, 200)}");
        }

        private void OnNetworkReceived(string connection, long epoch, byte[] message)
        {
            long n = Interlocked.Increment(ref _networkReceived);
            if (Sample(n)) _log.Write("SDK.NETRX", $"#{n} conn={connection} epoch={epoch} len={message?.Length ?? 0}");
        }

        private void OnNetworkSent(string connection, long epoch, byte[] message)
        {
            long n = Interlocked.Increment(ref _networkSent);
            if (Sample(n)) _log.Write("SDK.NETTX", $"#{n} conn={connection} epoch={epoch} len={message?.Length ?? 0}");
        }

        // CombatantAdded/Removed/PlayerStatsChanged carry model types; take them as object (delegate
        // parameter contravariance) so the probe needn't bind the concrete model shapes.
        private void OnCombatantAdded(object combatant)
        {
            long n = Interlocked.Increment(ref _combatantAdded);
            if (Sample(n)) _log.Write("SDK.CBADD", $"#{n} {Trunc(combatant?.ToString(), 160)}");
        }

        private void OnCombatantRemoved(object combatant)
        {
            long n = Interlocked.Increment(ref _combatantRemoved);
            if (Sample(n)) _log.Write("SDK.CBDEL", $"#{n} {Trunc(combatant?.ToString(), 160)}");
        }

        private void OnPartyListChanged(ReadOnlyCollection<uint> partyList, int partySize)
        {
            Interlocked.Increment(ref _partyListChanged);
            _log.Write("SDK.PARTY", $"size={partySize} ids=[{string.Join(",", partyList ?? new ReadOnlyCollection<uint>(new uint[0]))}]");
        }

        private void OnPrimaryPlayerChanged()
        {
            Interlocked.Increment(ref _primaryPlayerChanged);
            string who = _repo != null ? Safe(() => _repo.GetCurrentPlayerID().ToString()) : "?";
            _log.Write("SDK.PRIMARY", $"primary player changed; playerId={who}");
        }

        private void OnPlayerStatsChanged(object playerStats)
        {
            Interlocked.Increment(ref _playerStatsChanged);
            _log.Write("SDK.STATS", Trunc(playerStats?.ToString(), 160));
        }

        private void OnZoneChanged(uint zoneId, string zoneName)
        {
            Interlocked.Increment(ref _zoneChanged);
            _log.Write("SDK.ZONE", $"id={zoneId} name='{zoneName}'");
        }

        private void OnProcessChanged(Process process)
        {
            Interlocked.Increment(ref _processChanged);
            _log.Write("SDK.PROC", process == null ? "process changed: (none)" : $"process changed: pid={Safe(() => process.Id.ToString())}");
        }

        // ---- ACT-side taps (the facade hub) ---------------------------------------------------

        private void TapActEvents()
        {
            var act = ActGlobals.oFormActMain;
            if (act == null) { _log.Write("ACT", "oFormActMain null — cannot tap ACT events"); return; }
            act.OnLogLineRead += OnActLogLineRead;
            act.OnCombatStart += OnActCombatStart;
            act.OnCombatEnd += OnActCombatEnd;
            act.AfterCombatAction += OnActAfterCombatAction;
            _log.Write("ACT", "tapped OnLogLineRead / OnCombatStart / OnCombatEnd / AfterCombatAction");
        }

        private void OnActLogLineRead(bool isImport, LogLineEventArgs e)
        {
            long n = Interlocked.Increment(ref _actLogLine);
            if (Sample(n)) _log.Write("ACT.LINE", $"#{n} type={e.detectedType} {Trunc(e.logLine, 200)}");
        }

        private void OnActCombatStart(bool isImport, CombatToggleEventArgs e)
        {
            Interlocked.Increment(ref _combatStart);
            _log.Write("ACT.START", $"combat start; zone='{Safe(() => ActGlobals.oFormActMain.CurrentZone)}'");
        }

        private void OnActCombatEnd(bool isImport, CombatToggleEventArgs e)
        {
            Interlocked.Increment(ref _combatEnd);
            string dmg = Safe(() => e.encounter?.Damage.ToString()) ?? "?";
            _log.Write("ACT.END", $"combat end; encounterDamage={dmg}");
        }

        private void OnActAfterCombatAction(bool isImport, CombatActionEventArgs e)
        {
            long n = Interlocked.Increment(ref _afterCombatAction);
            if (Sample(n))
                _log.Write("ACT.SWING", $"#{n} {e.attacker} -> {e.victim} {e.theAttackType} dmg={e.damage} crit={e.critical}");
        }

        // ---- Periodic snapshot: counters + the aggregate OverlayPlugin/cactbot read ----------

        private void Snapshot()
        {
            try
            {
                _log.Write("SNAP", $"counters sdk[log={_logLine} plog={_parsedLogLine} netRx={_networkReceived} " +
                    $"netTx={_networkSent} cbAdd={_combatantAdded} cbDel={_combatantRemoved} party={_partyListChanged} " +
                    $"primary={_primaryPlayerChanged} stats={_playerStatsChanged} zone={_zoneChanged} proc={_processChanged}] " +
                    $"act[line={_actLogLine} swing={_afterCombatAction} start={_combatStart} end={_combatEnd}] " +
                    $"logDropped={_log.DroppedCount}");

                var enc = ActGlobals.oFormActMain?.ActiveZone?.ActiveEncounter;
                if (enc == null || enc.Items.Count == 0) { _log.Write("SNAP", "no active encounter"); return; }

                string Enc(string k) => EncounterData.ExportVariables.TryGetValue(k, out var f)
                    ? Safe(() => f.GetExportString(enc, enc.GetAllies(), "")) : "(missing)";
                _log.Write("SNAP", $"ENCOUNTER title='{Enc("title")}' duration={Enc("duration")} " +
                    $"encdps={Enc("encdps")} damage={Enc("damage")}");

                string Cmb(CombatantData cd, string k) => CombatantData.ExportVariables.TryGetValue(k, out var f)
                    ? Safe(() => f.GetExportString(cd, "")) : "(missing)";
                foreach (var cd in enc.Items.Values.OrderByDescending(c => c.Damage).Take(8))
                    _log.Write("SNAP", $"  {Cmb(cd, "name")}: encdps={Cmb(cd, "encdps")} damage={Cmb(cd, "damage")} " +
                        $"dps%={Cmb(cd, "damage%")} crit%={Cmb(cd, "crithit%")} hits={Cmb(cd, "hits")}");
            }
            catch (Exception ex) { _log.Write("SNAP", "ERROR " + ex.Message); }
        }

        // ---- Teardown -------------------------------------------------------------------------

        public void DeInitPlugin()
        {
            try { _discover?.Stop(); _discover?.Dispose(); } catch { }
            try { _snapshot?.Stop(); _snapshot?.Dispose(); } catch { }

            var act = ActGlobals.oFormActMain;
            if (act != null)
            {
                try { act.OnLogLineRead -= OnActLogLineRead; } catch { }
                try { act.OnCombatStart -= OnActCombatStart; } catch { }
                try { act.OnCombatEnd -= OnActCombatEnd; } catch { }
                try { act.AfterCombatAction -= OnActAfterCombatAction; } catch { }
            }

            if (_sub != null)
            {
                try { _sub.LogLine -= OnLogLine; } catch { }
                try { _sub.ParsedLogLine -= OnParsedLogLine; } catch { }
                try { _sub.NetworkReceived -= OnNetworkReceived; } catch { }
                try { _sub.NetworkSent -= OnNetworkSent; } catch { }
                try { _sub.CombatantAdded -= OnCombatantAdded; } catch { }
                try { _sub.CombatantRemoved -= OnCombatantRemoved; } catch { }
                try { _sub.PartyListChanged -= OnPartyListChanged; } catch { }
                try { _sub.PrimaryPlayerChanged -= OnPrimaryPlayerChanged; } catch { }
                try { _sub.PlayerStatsChanged -= OnPlayerStatsChanged; } catch { }
                try { _sub.ZoneChanged -= OnZoneChanged; } catch { }
                try { _sub.ProcessChanged -= OnProcessChanged; } catch { }
            }

            try { Snapshot(); } catch { }
            _log?.Write("DEINIT", "Fct.StreamProbe stopped");
            _log?.Dispose();
            if (_status != null) try { _status.Text = "Fct.StreamProbe stopped"; } catch { }
        }

        // ---- Helpers --------------------------------------------------------------------------

        // Log the first 32 of each event in full, then 1-in-256, so a live game's high-rate streams
        // (packets, log lines) stay readable and the writer thread is never overrun.
        private static bool Sample(long n) => n <= 32 || (n & 0xFF) == 0;

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        private static string SafeType(object o) => o == null ? "null" : o.GetType().FullName;

        private static string Safe(Func<string> f) { try { return f(); } catch (Exception ex) { return "<err:" + ex.GetType().Name + ">"; } }
    }
}
