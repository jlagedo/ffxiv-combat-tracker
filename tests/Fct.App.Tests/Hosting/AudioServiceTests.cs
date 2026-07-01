using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.App.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Hosting;

public class AudioServiceTests
{
    private static AudioService NewService() => new(NullLogger<AudioService>.Instance);

    [Fact]
    public void Speak_fans_out_to_all_sinks_in_priority_order()
    {
        var audio = NewService();
        var low = new RecordingAudioSink("low");
        var high = new RecordingAudioSink("high");
        audio.RegisterSink(low, priority: 0);
        audio.RegisterSink(high, priority: 10);

        audio.Speak("hi");

        Assert.Single(low.Speaks);
        Assert.Single(high.Speaks);
        Assert.Equal("hi", high.Speaks[0].Text);
    }

    [Fact]
    public void Terminal_sink_stops_the_chain()
    {
        var audio = NewService();
        var below = new RecordingAudioSink("below");
        var terminal = new RecordingAudioSink("terminal");
        audio.RegisterSink(below, priority: 0);
        audio.RegisterSink(terminal, priority: 10, terminal: true);

        audio.Speak("routed");

        Assert.Single(terminal.Speaks);
        Assert.Empty(below.Speaks); // suppressed by the higher-priority terminal sink
    }

    [Fact]
    public void Play_carries_volume()
    {
        var audio = NewService();
        var sink = new RecordingAudioSink();
        audio.RegisterSink(sink);

        audio.Play("beep.wav", 55);

        Assert.Single(sink.Plays);
        Assert.Equal(("beep.wav", 55), sink.Plays[0]);
    }
}
