using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Audio routing across the seam (B1, A2). The shortest legacy→native path.</summary>
    public sealed class AudioFlowTests
    {
        // B1 — Triggernometry TTS()/PlaySound() → native IAudioSink (RealPlugin.cs:1650-1683).
        [Fact]
        public void B1_LegacyTts_ReachesNativeSink()
        {
            var audio = new RecordingAudioOutput();
            var sink = new RecordingAudioSink("native");
            audio.RegisterSink(sink);
            var host = new FakePluginHost(audio: audio);
            using var shim = new ShimStub(host);

            // Trig calls the legacy TTS()/PlaySound() entry points.
            shim.TTS("pull in 5");
            shim.PlaySound("alarm.wav", 80);

            Assert.Single(sink.Speaks);
            Assert.Equal("pull in 5", sink.Speaks[0].Text);
            var (file, volume) = Assert.Single(sink.Plays);
            Assert.Equal("alarm.wav", file);
            Assert.Equal(80, volume); // volume 0–100 preserved
        }

        // A2 — native Audio.Speak → every registered sink (fan-out), exact text + AudioOptions.
        [Fact]
        public void A2_NativeSpeak_FansOutToAllSinks()
        {
            var audio = new RecordingAudioOutput();
            var discord = new RecordingAudioSink("discord");
            var yukkuri = new RecordingAudioSink("ttsyukkuri");
            audio.RegisterSink(discord);
            audio.RegisterSink(yukkuri);
            var host = new FakePluginHost(audio: audio);

            var opts = new AudioOptions(Volume: 60, Voice: "Alex", Rate: 1.2f);
            host.Audio.Speak("engage", opts);

            foreach (var sink in new[] { discord, yukkuri })
            {
                var (text, options) = Assert.Single(sink.Speaks);
                Assert.Equal("engage", text);
                Assert.Equal(opts, options); // record equality: exact AudioOptions carried through
            }
        }

        // A2 (terminal variant) — Discord-Triggers / TTSYukkuri route audio *instead of* the speakers.
        // The additive fan-out contract can't express that: a terminal sink would stop the chain so a
        // lower-priority sink never fires. RED until G3 (terminal-sink capability) exists.
        [Fact(Skip = "G3: terminal/route-instead-of sink capability not yet in the contract")]
        public void A2_TerminalSink_StopsTheChain()
        {
            var audio = new RecordingAudioOutput();
            var terminal = new RecordingAudioSink("discord-terminal");
            var speakers = new RecordingAudioSink("builtin-speakers");
            audio.RegisterSink(terminal, priority: 10 /*, terminal: true — G3 */);
            audio.RegisterSink(speakers, priority: 0);

            host_Speak(audio, "engage");

            Assert.Single(terminal.Speaks);
            Assert.Empty(speakers.Speaks); // would fail today: fan-out delivers to both

            static void host_Speak(RecordingAudioOutput a, string t) => a.Speak(t);
        }
    }
}
