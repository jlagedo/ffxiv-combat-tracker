using System;
using System.Collections.Generic;

namespace Advanced_Combat_Tracker
{
    // ACT's damage-number wrapper. Sentinels match ACT: NoDamage=0, Miss=-1, Unknown=-9,
    // Death=-10, ThreatPosition=-11. Implicitly converts to long.
    public class Dnum : IComparable
    {
        private string damageString;

        public static Dnum NoDamage => 0L;
        public static Dnum Miss => -1L;
        public static Dnum Unknown => -9L;
        public static Dnum Death => -10L;
        public static Dnum ThreatPosition => -11L;

        public long Number { get; }
        public string DamageString
        {
            get => string.IsNullOrEmpty(damageString) ? ToString() : damageString;
            set => damageString = value;
        }
        public string DamageString2 { get; set; }

        public Dnum(long number) { Number = number; damageString = string.Empty; }
        public Dnum(long number, string customDamageString) { Number = number; damageString = customDamageString; }

        public static implicit operator long(Dnum val) => val.Number;
        public static implicit operator Dnum(long val) => val >= -10 ? new Dnum(val) : new Dnum(-9L);

        public static bool operator ==(Dnum a, Dnum b) => a.Equals(b);
        public static bool operator !=(Dnum a, Dnum b) => !a.Equals(b);

        public override string ToString() => Number > 0 ? Number.ToString() : (damageString ?? Number.ToString());
        public int CompareTo(object obj) => Number.CompareTo(((Dnum)obj).Number);
        public override bool Equals(object obj) => obj is Dnum d && d.Number == Number;
        public override int GetHashCode() => Number.GetHashCode();
    }

    // A single combat action. Special defaults to the "none" term.
    public class MasterSwing : IComparable, IComparable<MasterSwing>
    {
        public delegate string StringDataCallback(MasterSwing Data);
        public delegate System.Drawing.Color ColorDataCallback(MasterSwing Data);

        public class ColumnDef
        {
            public StringDataCallback GetCellData;
            public StringDataCallback GetSqlData;
            public Comparison<MasterSwing> SortComparer;
            public ColorDataCallback GetCellForeColor = _ => System.Drawing.Color.Transparent;
            public ColorDataCallback GetCellBackColor = _ => System.Drawing.Color.Transparent;
            public string SqlDataType { get; }
            public string SqlDataName { get; }
            public bool DefaultVisible { get; }
            public string Label { get; }
            public ColumnDef(string Label, bool DefaultVisible, string SqlDataType, string SqlDataName,
                StringDataCallback CellDataCallback, StringDataCallback SqlDataCallback, Comparison<MasterSwing> SortComparer)
            { this.Label = Label; this.DefaultVisible = DefaultVisible; this.SqlDataType = SqlDataType;
              this.SqlDataName = SqlDataName; GetCellData = CellDataCallback; GetSqlData = SqlDataCallback; this.SortComparer = SortComparer; }
        }

        public static Dictionary<string, ColumnDef> ColumnDefs = new Dictionary<string, ColumnDef>();

        public string GetColumnByName(string name) => ColumnDefs.ContainsKey(name) ? ColumnDefs[name].GetCellData(this) : string.Empty;

        public int SwingType { get; }
        public bool Critical { get; set; }
        public string Special { get; }
        public Dnum Damage { get; }
        public DateTime Time { get; }
        public int TimeSorter { get; }
        public string AttackType { get; }
        public string Attacker { get; }
        public string DamageType { get; }
        public string Victim { get; }
        public EncounterData ParentEncounter { get; set; }
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();

        public MasterSwing(int swingType, bool critical, string special, Dnum damage, DateTime time,
            int timeSorter, string theAttackType, string attacker, string theDamageType, string victim)
        {
            SwingType = swingType; Critical = critical; Special = special ?? "none";
            Damage = damage; Time = time; TimeSorter = timeSorter;
            AttackType = theAttackType; Attacker = attacker; DamageType = theDamageType; Victim = victim;
        }

        public MasterSwing(int swingType, bool critical, Dnum damage, DateTime time, int timeSorter,
            string theAttackType, string attacker, string theDamageType, string victim)
            : this(swingType, critical, "none", damage, time, timeSorter, theAttackType, attacker, theDamageType, victim) { }

        public int CompareTo(object obj) => CompareTo(obj as MasterSwing);
        public int CompareTo(MasterSwing other) => other == null ? 1 : TimeSorter.CompareTo(other.TimeSorter);
        internal static int CompareTime(MasterSwing l, MasterSwing r)
        {
            int n = l.TimeSorter.CompareTo(r.TimeSorter);
            return n != 0 ? n : l.Time.CompareTo(r.Time);
        }
        public override string ToString() => $"{Time:s}|{Damage}|{Attacker}|{AttackType}|{Victim}";
    }
}
