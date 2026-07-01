using System;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D4: <see cref="Advanced_Combat_Tracker.FormActMain"/> re-fires the modern <c>RawLogLine</c>
/// firehose as ACT's <c>Before/OnLogLineRead</c> (the Trig/cactbot regex lifeline); typed events do
/// not trip the line hook.
/// </summary>
public class RawLogLineTests
{
    [Fact]
    public void OnLogLineRead_fires_for_each_raw_line()
    {
        var host = new FakePluginHost();
        var form = new Advanced_Combat_Tracker.FormActMain(host);

        Advanced_Combat_Tracker.LogLineEventArgs? captured = null;
        int beforeCount = 0;
        form.BeforeLogLineRead += (isImport, args) => beforeCount++;
        form.OnLogLineRead += (isImport, args) => captured = args;

        host.Bus!.Emit(new RawLogLine(1, DateTimeOffset.Now, LogMessageType.ChatLog, "00|chat|hello", "00|chat|hello"));

        Assert.Equal(1, beforeCount);
        Assert.NotNull(captured);
        Assert.Equal("00|chat|hello", captured!.logLine);
        Assert.Equal((int)LogMessageType.ChatLog, captured.detectedType);
    }

    [Fact]
    public void Typed_events_do_not_fire_the_line_hook()
    {
        var host = new FakePluginHost();
        var form = new Advanced_Combat_Tracker.FormActMain(host);

        int fired = 0;
        form.OnLogLineRead += (_, __) => fired++;

        host.Bus!.Emit(new ZoneChanged(1, DateTimeOffset.Now, 132, "New Gridania"));

        Assert.Equal(0, fired);
    }
}
