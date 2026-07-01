using System;
using System.Text.RegularExpressions;

namespace Advanced_Combat_Tracker
{
    // Minimal binding surface for Triggernometry. The host runs no trigger engine; these types exist
    // only so Triggernometry's compiled ProxyPlugin resolves its member tokens against our facade and
    // degrades to a no-op. None of them are populated or driven by the host.

    // ACT's user-defined custom trigger (regex -> sound/timer/tab). Triggernometry reads these off
    // oFormActMain.CustomTriggers to import them into its own engine. CustomTriggers stays empty here,
    // so no instance is ever created; only the member shape needs to exist for the import projection to
    // bind. Mirrors ACT Models/CustomTrigger.cs minus the WinForms results-tab members (no consumer in
    // the supported plugins reads them). SoundType: 1 Beep, 2 Sound, 3 TTS.
    public class CustomTrigger
    {
        private string category = "General";
        private readonly Regex rE;

        public string Category
        {
            get => string.IsNullOrEmpty(category) ? "General" : category;
            set => category = value;
        }
        public bool RestrictToCategoryZone { get; set; }
        public string Key => category + "|" + ShortRegexString;
        public DateTime LastAudioAlert { get; set; } = DateTime.MinValue;
        public string TimerName { get; set; } = string.Empty;
        public string SoundData { get; set; } = string.Empty;
        public string ShortRegexString { get; set; }
        public bool Active { get; set; } = true;
        public bool Timer { get; set; }
        public int SoundType { get; set; }
        public bool Tabbed { get; set; }
        public Regex RegEx => rE;

        public CustomTrigger(string cRegex, string cCategory)
        {
            ShortRegexString = cRegex;
            rE = new Regex(ShortRegexString, RegexOptions.Compiled);
            category = cCategory;
        }

        public CustomTrigger(string cRegex, int cSoundType, string cSoundData, bool cTimer, string cTimerName, bool cTabbed)
        {
            ShortRegexString = cRegex;
            SoundData = cSoundData;
            TimerName = cTimerName;
            SoundType = cSoundType;
            Timer = cTimer;
            Active = true;
            Tabbed = cTabbed;
            rE = new Regex(ShortRegexString, RegexOptions.Compiled);
        }
    }

    // One stored log line on an encounter (ACT Models/LogLineEntry.cs). Triggernometry appends to
    // ActiveEncounter.LogLines through this ctor when mirroring ACT's encounter log; EncounterData
    // .LogLines is typed List<LogLineEntry> so that append binds.
    public class LogLineEntry
    {
        public int GlobalTimeSorter { get; }
        public DateTime Time { get; set; }
        public string LogLine { get; }
        public int Type { get; }
        public bool SearchSelected { get; set; }

        public LogLineEntry(DateTime Time, string LogLine, int ParsedType, int GlobalTimeSorter)
        {
            this.LogLine = LogLine;
            Type = ParsedType;
            SearchSelected = false;
            this.Time = Time;
            this.GlobalTimeSorter = GlobalTimeSorter;
        }
    }

    // Formatting options for ACT's encounter text export. Triggernometry reflects the private
    // FormActMain.defaultTextFormat field and passes it to GetTextExport; the type exists so that call
    // binds. Shape matches ACT Models/TextExportFormatOptions.cs.
    public class TextExportFormatOptions : IEquatable<TextExportFormatOptions>
    {
        public string PlayerFormat { get; }
        public string AlliesFormat { get; }
        public string Sorting { get; }
        public bool ShowOnlyAllies { get; }
        public bool ShowAlliedInfo { get; }

        public TextExportFormatOptions(string PlayerFormat, string Sorting, bool ShowOnlyAllies,
            bool ShowAlliedInfo, string AlliesFormat)
        {
            this.PlayerFormat = PlayerFormat;
            this.Sorting = Sorting;
            this.ShowOnlyAllies = ShowOnlyAllies;
            this.ShowAlliedInfo = ShowAlliedInfo;
            this.AlliesFormat = AlliesFormat;
        }

        public override string ToString() => "(" + Sorting + ") \"" + PlayerFormat + "\"";
        public override int GetHashCode() =>
            $"{PlayerFormat}{AlliesFormat}{Sorting}{ShowOnlyAllies}{ShowAlliedInfo}".GetHashCode();
        public override bool Equals(object obj) =>
            obj != DBNull.Value && obj is TextExportFormatOptions other && Equals(other);
        public bool Equals(TextExportFormatOptions other) =>
            other != null && GetHashCode().Equals(other.GetHashCode());
    }
}
