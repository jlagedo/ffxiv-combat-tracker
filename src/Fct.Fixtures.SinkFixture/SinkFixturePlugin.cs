using System;
using System.IO;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace Fct.Fixtures.SinkFixture
{
    // A minimal real IActPluginV1 that stands in for ACT-Discord-Triggers as a terminal audio sink. On
    // init it replaces the ACT audio slots (PlayTtsMethod/PlaySoundMethod) with a recording delegate —
    // the same ldfld/stfld takeover Discord-Triggers performs — so the satellite's audio-sink poll
    // announces REGISTERSINK and the host relays produced audio down to this satellite. Each relayed
    // call is appended to a record file so a test can assert the produced TTS/sound arrived here.
    public sealed class SinkFixturePlugin : IActPluginV1
    {
        // Test seam: where relayed calls are recorded. Falls back to a file next to the plugin.
        public const string RecordPathEnvVar = "FCT_SINK_RECORD";

        private Label _status;
        private string _recordPath;
        private readonly object _gate = new object();

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _status = pluginStatusText;
            _recordPath = Environment.GetEnvironmentVariable(RecordPathEnvVar);
            if (string.IsNullOrEmpty(_recordPath))
                _recordPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sinkfixture.log");

            var act = ActGlobals.oFormActMain;
            if (act != null)
            {
                // Take over both slots (reference differs from the sentinel → TtsHijacked/SoundHijacked).
                act.PlayTtsMethod = OnTts;
                act.PlaySoundMethod = OnSound;
            }
            if (pluginScreenSpace != null) pluginScreenSpace.Text = "SinkFixture";
            if (_status != null) _status.Text = "SinkFixture Started.";
        }

        private void OnTts(string text) => Record("TTS|" + (text ?? ""));
        private void OnSound(string wavFilePath, int volume) => Record("SND|" + volume + "|" + (wavFilePath ?? ""));

        private void Record(string line)
        {
            try { lock (_gate) File.AppendAllText(_recordPath, line + Environment.NewLine); }
            catch { /* fixture: never throw on the relay path */ }
        }

        public void DeInitPlugin()
        {
            if (_status != null) try { _status.Text = "SinkFixture stopped."; } catch { }
        }
    }
}
