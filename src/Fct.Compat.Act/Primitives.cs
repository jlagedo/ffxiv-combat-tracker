using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    // The plugin host contract. Real plugins implement this.
    public interface IActPluginV1
    {
        void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText);
        void DeInitPlugin();
    }

    // Per-plugin record ACT keeps. OverlayPlugin reflects over cbEnabled/lblPluginTitle/
    // pluginObj for discovery; FFXIV_ACT_Plugin reads pluginFile.DirectoryName.
    public class ActPluginData
    {
        public TabPage tpPluginSpace;
        public Label lblPluginStatus;
        public Label lblPluginTitle;
        public CheckBox cbEnabled;
        public Panel pPluginInfo;
        public System.IO.FileInfo pluginFile;
        public IActPluginV1 pluginObj;
        public string pluginVersion;
    }

    // --- Log-line / combat event surface (events plugins subscribe to) ---

    public delegate void LogLineEventDelegate(bool isImport, LogLineEventArgs logInfo);
    public delegate void LogFileChangedDelegate(bool IsImport, string newLogFileName);
    public delegate void CombatToggleEventDelegate(bool isImport, CombatToggleEventArgs encounterInfo);
    public delegate void CombatActionDelegate(bool isImport, CombatActionEventArgs actionInfo);

    public class LogLineEventArgs : EventArgs
    {
        public string logLine;
        public int detectedType;
        public readonly DateTime detectedTime;
        public readonly string detectedZone;
        public readonly bool inCombat;
        public readonly string originalLogLine;

        public LogLineEventArgs(string logLine, int detectedType, DateTime detectedTime,
            string detectedZone, bool inCombat)
        {
            this.logLine = logLine;
            this.detectedType = detectedType;
            this.detectedTime = detectedTime;
            this.detectedZone = detectedZone;
            this.inCombat = inCombat;
            originalLogLine = logLine;
        }
    }

    public class CombatToggleEventArgs : EventArgs
    {
        public readonly int zoneDataIndex;
        public readonly int encounterDataIndex;
        public readonly EncounterData encounter;

        public CombatToggleEventArgs(int zoneDataIndex, int encounterDataIndex, EncounterData encounter)
        {
            this.zoneDataIndex = zoneDataIndex;
            this.encounterDataIndex = encounterDataIndex;
            this.encounter = encounter;
        }
    }

    public class CombatActionEventArgs : EventArgs
    {
        public int swingType;
        public bool critical;
        public string attacker;
        public string theAttackType;
        public Dnum damage;
        public DateTime time;
        public int timeSorter;
        public string victim;
        public string theDamageType;
        public string special;
        public Dictionary<string, object> tags;
        public readonly MasterSwing combatAction;
        public bool cancelAction;

        public CombatActionEventArgs(MasterSwing action)
        {
            combatAction = action;
            swingType = action.SwingType;
            critical = action.Critical;
            attacker = action.Attacker;
            theAttackType = action.AttackType;
            damage = action.Damage;
            time = action.Time;
            timeSorter = action.TimeSorter;
            victim = action.Victim;
            theDamageType = action.DamageType;
            special = action.Special;
            tags = action.Tags;
        }
    }
}
