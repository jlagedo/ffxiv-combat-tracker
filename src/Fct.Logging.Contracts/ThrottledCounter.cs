using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Fct.Logging
{
    // A rate-limited failure counter for hot paths where a fault must never be swallowed silently
    // yet must never turn into an unbounded log storm. The first occurrence logs immediately (with
    // the full exception); further occurrences inside the interval are counted and folded into the
    // next periodic "N since last (M total)" line. Each throwing site owns its own instance, so
    // "first-throw-then-throttle" is per-site with no handler-identity bookkeeping.
    //
    // Lives in Fct.Logging.Contracts (net48;net10) so the same type compiles into both the net10
    // host and the net48 satellite. Uses Stopwatch.GetTimestamp() for the clock — Environment
    // .TickCount64 does not exist on net48. Lock-free: the hot path pays two interlocked increments
    // plus a read and a compare.
    public sealed class ThrottledCounter
    {
        private readonly ILogger _log;
        private readonly LogLevel _level;
        private readonly EventId _event;
        private readonly string _template;
        private readonly long _minIntervalTicks;

        private long _total;
        private long _sinceLast;
        private long _nextEmit; // Stopwatch timestamp; 0 => the next Record() emits immediately.

        public ThrottledCounter(ILogger log, LogLevel level, EventId ev, string template, TimeSpan? interval = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _level = level;
            _event = ev;
            _template = template ?? throw new ArgumentNullException(nameof(template));
            var window = interval ?? TimeSpan.FromSeconds(5);
            _minIntervalTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        }

        /// <summary>Total occurrences recorded since construction.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <summary>Record one occurrence. Logs the first one immediately, then at most once per
        /// interval with the count folded in. Safe to call from any thread.</summary>
        public void Record(Exception? ex = null)
        {
            Interlocked.Increment(ref _total);
            Interlocked.Increment(ref _sinceLast);

            long now = Stopwatch.GetTimestamp();
            long next = Interlocked.Read(ref _nextEmit);
            if (now < next)
                return;

            // Claim the emit slot; only the thread that swaps in the new deadline logs.
            if (Interlocked.CompareExchange(ref _nextEmit, now + _minIntervalTicks, next) != next)
                return;

            long count = Interlocked.Exchange(ref _sinceLast, 0);
            _log.Log(_level, _event, ex, _template, count, Interlocked.Read(ref _total));
        }
    }
}
