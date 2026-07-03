using Fct.Bridge;
using Xunit;

namespace Fct.App.Tests.Plugins;

public class SatelliteProtocolTests
{
    [Fact]
    public void LoadPlugin_frame_round_trips()
    {
        var line = SatelliteProtocol.FormatLoadPlugin("com.x", @"C:\p\x.dll", "X Plugin");
        Assert.True(SatelliteProtocol.TryParseLoadPlugin(line, out var key, out var dll, out var title));
        Assert.Equal("com.x", key);
        Assert.Equal(@"C:\p\x.dll", dll);
        Assert.Equal("X Plugin", title);
    }

    [Fact]
    public void UnloadPlugin_frame_round_trips()
    {
        var line = SatelliteProtocol.FormatUnloadPlugin("com.x");
        Assert.True(SatelliteProtocol.TryParseUnloadPlugin(line, out var key));
        Assert.Equal("com.x", key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unloaded_ack_round_trips(bool ok)
    {
        var line = SatelliteProtocol.FormatUnloaded("com.x", ok);
        Assert.True(SatelliteProtocol.TryParseUnloaded(line, out var key, out var parsed));
        Assert.Equal("com.x", key);
        Assert.Equal(ok, parsed);
    }

    [Theory]
    [InlineData("PLUGIN a|b|c|d")]
    [InlineData("READY x64=True")]
    [InlineData("random")]
    [InlineData(null)]
    public void Command_parsers_reject_unrelated_lines(string? line)
    {
        Assert.False(SatelliteProtocol.TryParseLoadPlugin(line, out _, out _, out _));
        Assert.False(SatelliteProtocol.TryParseUnloadPlugin(line, out _));
        Assert.False(SatelliteProtocol.TryParseUnloaded(line, out _, out _));
    }
}
