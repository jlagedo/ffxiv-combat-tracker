using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace Fct.Fixtures.TriggerFixture
{
    // A minimal real IActPluginV1 that behaves like a Triggernometry trigger for the P7 gate: it taps
    // ACT's OnLogLineRead, and when a line matches the marker regex it speaks the captured payload via
    // ActGlobals.oFormActMain.TTS(...). Nothing else — it reads only the log-line re-raise surface, so a
    // green run proves a consumer satellite fans a fanned RawLogLine through the facade to a trigger and
    // routes the produced TTS up to the host. Read-only w.r.t. the engine.
    public sealed class TriggerFixturePlugin : IActPluginV1
    {
        // The consumer satellite re-raises fanned RawLogLines as "<payload>". The fixture matches this
        // marker and speaks capture group 1, so a test can assert the exact string on the audio pipe.
        public const string MarkerPrefix = "FCT_TRIGGER:";
        private static readonly Regex Marker = new Regex(Regex.Escape(MarkerPrefix) + "([^|]+)", RegexOptions.Compiled);

        private Label _status;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _status = pluginStatusText;
            var act = ActGlobals.oFormActMain;
            if (act != null) act.OnLogLineRead += OnLogLineRead;
            if (pluginScreenSpace != null) pluginScreenSpace.Text = "TriggerFixture";
            if (_status != null) _status.Text = "TriggerFixture Started.";
        }

        private void OnLogLineRead(bool isImport, LogLineEventArgs e)
        {
            if (e?.logLine == null) return;
            var m = Marker.Match(e.logLine);
            if (!m.Success) return;
            try { ActGlobals.oFormActMain?.TTS(m.Groups[1].Value); } catch { /* fixture: never throw into the fold */ }
        }

        public void DeInitPlugin()
        {
            var act = ActGlobals.oFormActMain;
            if (act != null) try { act.OnLogLineRead -= OnLogLineRead; } catch { }
            if (_status != null) try { _status.Text = "TriggerFixture stopped."; } catch { }
        }
    }
}
