using System;
using System.Diagnostics;
using System.Threading;

namespace Fct.App.Tests;

/// <summary>Spin-waits for a condition — the event bus dispatches on a background pump, so callback
/// delivery is asynchronous and tests poll for it.</summary>
internal static class TestWait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
