using System;
using System.Collections.Generic;

namespace Advanced_Combat_Tracker
{
    // The runtime-neutral ACT encounter state machine: owns the active zone/encounter, the in-combat
    // flag, and the idle-timeout clock, and reproduces ACT's auto-start (on SetEncounter) and
    // idle-end (CheckIdleEndCombat) behavior. Both sides of the bridge drive one identical copy of
    // this — the net48 ACT facade (Fct.Compat.Act.FormActMain) and the modern net10 engine — so an
    // encounter starts and ends at the same instant regardless of which runtime aggregates. Depends
    // only on Fct.Aggregation types: no WinForms, no ACT event surface, no Fct.Abstractions.
    public sealed class EncounterLifecycle
    {
        public ZoneData ActiveZone { get; set; } = new ZoneData();

        public bool InCombat { get; set; }
        public string CurrentZone { get; set; } = "";

        public DateTime LastKnownTime { get; set; } = DateTime.Now;
        public DateTime LastEstimatedTime { get; set; } = DateTime.Now;
        public DateTime LastHostileTime { get; set; }

        // Idle-end of combat (ACT CheckIdleEndCombat). nudIdleLimit_Value defaults to 6 s.
        public bool IdleEndEnabled { get; set; } = true;
        public double IdleLimitSeconds { get; set; } = 6;

        // Raised when an encounter opens / closes. Each host wires these to its own event surface:
        // the net48 facade to OnCombatStart/OnCombatEnd, the modern engine to IEncounterService.
        public Action<EncounterData> CombatStarted;
        public Action<EncounterData> CombatEnded;

        // Fold a swing into the active encounter and advance the "known time" clock. Encounter start
        // is driven by SetEncounter (ACT calls it per hostile action before the swing), so a swing
        // with no open encounter is silently dropped, exactly as ACT's null-conditional fold does.
        public void AddCombatAction(MasterSwing action)
        {
            LastKnownTime = action.Time;
            LastEstimatedTime = action.Time;
            ActiveZone.ActiveEncounter?.AddCombatAction(action);
        }

        // Open a new encounter on the !InCombat transition; every hostile action refreshes the idle
        // clock. Mirrors ACT FormActMain.SetEncounter.
        public bool SetEncounter(DateTime time, string attacker, string victim)
        {
            LastKnownTime = time;
            if (!InCombat || ActiveZone.ActiveEncounter == null || !ActiveZone.ActiveEncounter.Active)
            {
                var enc = new EncounterData(AggregationGlobals.charName, CurrentZone, ActiveZone) { Active = true };
                enc.StartTimes.Add(time);
                ActiveZone.ActiveEncounter = enc;
                ActiveZone.Items.Add(enc);
                InCombat = true;
                CombatStarted?.Invoke(enc);
            }
            LastHostileTime = time;
            return true;
        }

        // Advance the parse clock as time passes (log lines / swing timestamps); combat ends after an
        // idle gap, matching ACT's CheckIdleEndCombat (LastKnownTime - LastHostileTime > nudIdleLimit).
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
            CurrentZone = zoneName;
            ActiveZone.ZoneName = zoneName;
        }

        public void EndCombat(bool actExport)
        {
            if (!InCombat) return;
            InCombat = false;
            var enc = ActiveZone.ActiveEncounter;
            if (enc != null)
            {
                enc.EndCombat(actExport);
                CombatEnded?.Invoke(enc);
            }
        }
    }
}
