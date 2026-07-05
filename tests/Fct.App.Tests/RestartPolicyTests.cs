using System;
using Fct.Host;
using Xunit;

namespace Fct.App.Tests;

// ISOLATION-PLAN P3: the pure supervision policy — exponential backoff between restarts and quarantine
// on a crash loop — held to its schedule deterministically without processes or a clock.
public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RestartPolicy Policy() => new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = TimeSpan.FromMilliseconds(1000),
        CrashLoopThreshold = 4,
        CrashLoopWindow = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void First_failure_backs_off_by_the_base_delay()
    {
        var d = Policy().Decide(new[] { T0 }, T0);
        Assert.False(d.Quarantine);
        Assert.Equal(TimeSpan.FromMilliseconds(100), d.Delay);
    }

    [Fact]
    public void Consecutive_failures_double_the_backoff()
    {
        var p = Policy();
        Assert.Equal(TimeSpan.FromMilliseconds(200),
            p.Decide(new[] { T0, T0.AddSeconds(1) }, T0.AddSeconds(1)).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(400),
            p.Decide(new[] { T0, T0.AddSeconds(1), T0.AddSeconds(2) }, T0.AddSeconds(2)).Delay);
    }

    [Fact]
    public void Backoff_is_capped_at_the_max_delay()
    {
        // A high threshold so many failures back off rather than quarantine; the delay saturates at Max.
        var p = new RestartPolicy
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(1000),
            CrashLoopThreshold = 100,
            CrashLoopWindow = TimeSpan.FromSeconds(60),
        };
        var times = new System.Collections.Generic.List<DateTimeOffset>();
        for (int i = 0; i < 20; i++) times.Add(T0.AddSeconds(i));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), p.Decide(times, T0.AddSeconds(19)).Delay);
    }

    [Fact]
    public void Crash_loop_within_the_window_quarantines()
    {
        var p = Policy();
        var times = new[] { T0, T0.AddSeconds(1), T0.AddSeconds(2), T0.AddSeconds(3) };
        var d = p.Decide(times, T0.AddSeconds(3));
        Assert.True(d.Quarantine);
    }

    [Fact]
    public void A_failure_outside_the_window_does_not_count_toward_the_loop()
    {
        var p = Policy();
        // One ancient failure + one now: only the recent one is in-window, so it backs off, not quarantines.
        var d = p.Decide(new[] { T0, T0.AddSeconds(60) }, T0.AddSeconds(60));
        Assert.False(d.Quarantine);
        Assert.Equal(TimeSpan.FromMilliseconds(100), d.Delay);
    }
}
