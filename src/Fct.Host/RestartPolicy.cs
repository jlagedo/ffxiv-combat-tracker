using System;
using System.Collections.Generic;

namespace Fct.Host
{
    /// <summary>The supervisor's decision after a satellite exits: quarantine it, or restart after a delay.</summary>
    public readonly record struct RestartDecision(bool Quarantine, TimeSpan Delay);

    /// <summary>
    /// Pure restart policy for a supervised satellite (ISOLATION-PLAN P3): exponential backoff between
    /// restarts, and quarantine on a crash loop (too many failures inside a rolling window). Stateless —
    /// the caller keeps the failure history and passes it in — so it is deterministically unit-testable
    /// without a clock or real processes.
    /// </summary>
    public sealed class RestartPolicy
    {
        /// <summary>Failures within <see cref="CrashLoopWindow"/> that trip quarantine (stop restarting).</summary>
        public int CrashLoopThreshold { get; init; } = 4;

        /// <summary>The rolling window over which failures are counted for the crash-loop check.</summary>
        public TimeSpan CrashLoopWindow { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Backoff before the first restart; doubled per consecutive recent failure.</summary>
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

        /// <summary>Ceiling for the exponential backoff.</summary>
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Decide what to do after a failure. <paramref name="failureTimes"/> is the ascending history of
        /// failure instants (including the one just observed); <paramref name="now"/> is the current time.
        /// Quarantines once the count of failures within the window reaches the threshold; otherwise
        /// backs off exponentially by that count (1 recent failure → <see cref="BaseDelay"/>).
        /// </summary>
        public RestartDecision Decide(IReadOnlyList<DateTimeOffset> failureTimes, DateTimeOffset now)
        {
            if (failureTimes is null) throw new ArgumentNullException(nameof(failureTimes));

            // Count failures inside the window. The history is ascending, so the first one that falls
            // outside the window guarantees every earlier one does too.
            int recent = 0;
            for (int i = failureTimes.Count - 1; i >= 0; i--)
            {
                if (now - failureTimes[i] <= CrashLoopWindow) recent++;
                else break;
            }

            if (recent >= CrashLoopThreshold)
                return new RestartDecision(Quarantine: true, Delay: TimeSpan.Zero);

            double factor = Math.Pow(2, Math.Max(0, recent - 1));
            long ticks = (long)Math.Min((double)MaxDelay.Ticks, BaseDelay.Ticks * factor);
            return new RestartDecision(Quarantine: false, Delay: TimeSpan.FromTicks(Math.Max(0, ticks)));
        }
    }
}
