using System;
using System.Collections.Generic;
using System.Drawing;

namespace Advanced_Combat_Tracker
{
    // ACT's built-in spell-timer notification delegate. OP's SpellTimerOverlay binds to the
    // FormSpellTimers events typed with this; the payload is a TimerFrame.
    public delegate void SpellTimerEventDelegate(TimerFrame spellTimer);

    // A spell-timer definition. Hojoring's SpecialSpellTimer authors these and pushes them into
    // FormSpellTimers.TimerDefs; OP's SpellTimerOverlay reads them (only inside its never-fired
    // event handler). Ported from ACT's TimerData with literal defaults — ACT's Trans[...] lookups
    // are replaced by a fixed category since the facade hosts no localization table.
    public class TimerData : IEquatable<TimerData>
    {
        private string category = "General";

        public bool OnlyMasterTicks { get; set; }

        public string Category
        {
            get => string.IsNullOrEmpty(category) ? "General" : category;
            set => category = value;
        }

        public bool RestrictToCategory { get; set; }
        public int RemoveValue { get; set; } = -15;
        public bool Panel1Display { get; set; } = true;
        public bool Panel2Display { get; set; }
        public Color FillColor { get; set; } = Color.Blue;
        public string Tooltip { get; set; } = string.Empty;
        public bool Modable { get; set; } = true;
        public bool ActiveInList { get; set; } = true;
        public bool RadialDisplay { get; set; } = true;
        public string Name { get; set; } = string.Empty;
        public int TimerValue { get; set; } = 30;
        public int WarningValue { get; set; } = 10;
        public bool AbsoluteTiming { get; set; }
        public bool RestrictToMe { get; set; }
        public string StartSoundData { get; set; } = string.Empty;
        public string WarningSoundData { get; set; } = string.Empty;

        public string Key => Category.ToLower() + "|" + Name.ToLower();

        public TimerData(string Name, string Category)
        {
            this.Name = Name;
            category = Category;
        }

        public override string ToString() => "[" + TimerValue + "] " + Name;
        public override bool Equals(object obj) => obj is TimerData td && Key.Equals(td.Key);
        public bool Equals(TimerData other) => other != null && Key.Equals(other.Key);
        public override int GetHashCode() => Key.GetHashCode();
    }

    // The OnSpellTimerNotify payload. Minimal: only the members reached inside OP's never-fired
    // handler. The type must exist (correct name/namespace) so OP's prebuilt metadata resolves;
    // the reads never execute because the facade never raises the events.
    public class TimerFrame
    {
        public TimerData TimerData { get; set; }
        public string Combatant { get; set; }
        public List<SpellTimer> SpellTimers { get; set; } = new List<SpellTimer>();
        public string Name => TimerData.Name;

        public TimerFrame(string Combatant, TimerData SpellTimerData)
        {
            this.Combatant = Combatant;
            TimerData = SpellTimerData;
        }
    }

    // A TimerFrame.SpellTimers element. Never constructed by the facade and only referenced inside
    // OP's never-fired handler, so it exists purely to satisfy type resolution.
    public class SpellTimer
    {
    }

    // ACT's built-in spell-timer engine. Functional storage, inert engine: TimerDefs is a real
    // store so Hojoring's add/enumerate/remove round-trip works, but the matching/tick engine and
    // the notify events are not reproduced. OnSpellTimerNotify/OnSpellTimerRemoved are declared so
    // OP's SpellTimerOverlay `+=` binds, but are never raised (the OP overlay shows nothing instead
    // of NRE-ing); NotifySpell and RebuildSpellTreeView are inert. A plain class, not a Form —
    // nothing casts oFormSpellTimers to Control.
    public class FormSpellTimers
    {
        public event SpellTimerEventDelegate OnSpellTimerNotify;
        public event SpellTimerEventDelegate OnSpellTimerRemoved;

        private readonly SortedDictionary<string, TimerData> timerDefs =
            new SortedDictionary<string, TimerData>();

        public SortedDictionary<string, TimerData> TimerDefs => timerDefs;

        public void AddEditTimerDef(TimerData newTd)
        {
            if (newTd == null) return;
            timerDefs[newTd.Key] = newTd;
        }

        public void RemoveTimerDef(TimerData oldTd)
        {
            if (oldTd != null) timerDefs.Remove(oldTd.Key);
        }

        // ACT rebuilds its spell tree-view UI here; we host no UI.
        public void RebuildSpellTreeView() { }

        public void NotifySpell(string Attacker, string SpellName, bool Self, string Victim, bool Success)
            => NotifySpell(Attacker, SpellName, Self, Victim, Success, new Dictionary<string, string>());

        public void NotifySpell(string Attacker, string SpellName, bool Self, string Victim,
            bool Success, Dictionary<string, string> ExtraInfo)
        {
            // Inert: ACT's timer engine would match SpellName against TimerDefs and raise
            // OnSpellTimerNotify. We reproduce neither — Hojoring still drives its own overlay.
            _ = OnSpellTimerNotify;
            _ = OnSpellTimerRemoved;
        }
    }
}
