using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D2: the ACT audio surface on <see cref="FormActMain"/> forwards to the modern host — producers
/// (<c>TTS</c>/<c>PlaySound</c>) fan out through <c>IAudioOutput</c>, and setting a
/// <c>PlayTtsMethod</c>/<c>PlaySoundMethod</c> slot registers a terminal sink (G3 route-instead-of).
/// </summary>
public class AudioTests
{
    [Fact]
    public void TTS_and_PlaySound_reach_the_host_audio()
    {
        var audio = new RecordingAudioOutput();
        var form = new FormActMain(new FakePluginHost(audio: audio));

        form.TTS("pull in 5");
        form.PlaySound("beep.wav", 42);

        Assert.Contains(audio.Speaks, s => s.Text == "pull in 5");
        Assert.Contains(audio.Plays, p => p.FilePath == "beep.wav" && p.Volume == 42);
    }

    [Fact]
    public void PlayTtsMethod_slot_takes_over_playback_as_a_terminal_sink()
    {
        var audio = new RecordingAudioOutput();
        var form = new FormActMain(new FakePluginHost(audio: audio));

        // ACT's built-in speaker stand-in at the default (lowest) priority.
        var builtin = new RecordingAudioSink("builtin");
        audio.RegisterSink(builtin, priority: 0);

        // A hijacker (Discord-Triggers / TTSYukkuri) replaces the slot to route audio to itself.
        string? captured = null;
        form.PlayTtsMethod = t => captured = t;   // registers a terminal sink at priority 10

        form.TTS("hello");

        Assert.Equal("hello", captured);
        Assert.Empty(builtin.Speaks);   // suppressed by the terminal hijack sink
    }

    [Fact]
    public void PlaySoundMethod_slot_takes_over_sound_playback()
    {
        var audio = new RecordingAudioOutput();
        var form = new FormActMain(new FakePluginHost(audio: audio));

        var builtin = new RecordingAudioSink("builtin");
        audio.RegisterSink(builtin, priority: 0);

        (string File, int Volume) captured = default;
        form.PlaySoundMethod = (file, volume) => captured = (file, volume);

        form.PlaySound("alarm.wav", 77);

        Assert.Equal(("alarm.wav", 77), captured);
        Assert.Empty(builtin.Plays);
    }
}
