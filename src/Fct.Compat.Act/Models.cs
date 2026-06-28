using System;
using System.Collections.Generic;

namespace Advanced_Combat_Tracker
{
    // ACT's damage number wrapper. Carries either a real number or a sentinel
    // (miss/death/no-damage). Implicitly converts to long.
    public class Dnum : IComparable, IComparable<Dnum>
    {
        public static readonly Dnum NoDamage = new Dnum(long.MinValue + 1, "NoDamage");
        public static readonly Dnum Miss = new Dnum(long.MinValue + 2, "Miss");
        public static readonly Dnum Death = new Dnum(long.MinValue + 3, "Death");
        public static readonly Dnum Unknown = new Dnum(long.MinValue + 4, "Unknown");

        public long Number;
        public string DamageString;
        public string DamageString2;

        public Dnum(long number) { Number = number; DamageString = number.ToString(); }
        public Dnum(long number, string damageString) { Number = number; DamageString = damageString; }

        public static implicit operator long(Dnum d) => d?.Number ?? 0L;

        public int CompareTo(object obj) => CompareTo(obj as Dnum);
        public int CompareTo(Dnum other) => other == null ? 1 : Number.CompareTo(other.Number);
        public override string ToString() => DamageString ?? Number.ToString();
    }

    // A single combat action (swing). Fed into the aggregation pipeline via
    // FormActMain.AddCombatAction.
    public class MasterSwing : IComparable, IComparable<MasterSwing>
    {
        public int SwingType { get; set; }
        public bool Critical { get; set; }
        public string Special { get; set; }
        public Dnum Damage { get; set; }
        public DateTime Time { get; set; }
        public int TimeSorter { get; set; }
        public string AttackType { get; set; }
        public string Attacker { get; set; }
        public string DamageType { get; set; }
        public string Victim { get; set; }
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();

        public MasterSwing(int SwingType, bool Critical, string Special, Dnum damage, DateTime Time,
            int TimeSorter, string theAttackType, string Attacker, string theDamageType, string Victim)
        {
            this.SwingType = SwingType;
            this.Critical = Critical;
            this.Special = Special ?? "";
            Damage = damage;
            this.Time = Time;
            this.TimeSorter = TimeSorter;
            AttackType = theAttackType;
            this.Attacker = Attacker;
            DamageType = theDamageType;
            this.Victim = Victim;
        }

        public MasterSwing(int SwingType, bool Critical, Dnum damage, DateTime Time, int TimeSorter,
            string theAttackType, string Attacker, string theDamageType, string Victim)
            : this(SwingType, Critical, "", damage, Time, TimeSorter, theAttackType, Attacker, theDamageType, Victim)
        {
        }

        public int CompareTo(object obj) => CompareTo(obj as MasterSwing);
        public int CompareTo(MasterSwing other) => other == null ? 1 : TimeSorter.CompareTo(other.TimeSorter);
    }

    // Minimal aggregation shells for S2/S3. The real DPS aggregation (CombatantData
    // ExportVariables, encounter accumulation) lands in S5.
    public class ZoneData
    {
        public string ZoneName = "";
        public EncounterData ActiveEncounter = new EncounterData();
        public List<EncounterData> Items = new List<EncounterData>();
    }

    public class EncounterData
    {
        public bool Active;
        public string ZoneName = "";
        public string Title = "";
        public DateTime StartTime = DateTime.MaxValue;
        public DateTime EndTime = DateTime.MinValue;
        public readonly Dictionary<string, CombatantData> Items =
            new Dictionary<string, CombatantData>(StringComparer.OrdinalIgnoreCase);

        public List<CombatantData> GetAllies() => new List<CombatantData>(Items.Values);
    }

    public class CombatantData
    {
        public string Name = "";
        public EncounterData Parent;
        public CombatantData(string name, EncounterData parent) { Name = name; Parent = parent; }
    }
}
