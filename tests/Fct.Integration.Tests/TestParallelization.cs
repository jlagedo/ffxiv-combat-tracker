using Xunit;

// These are heavyweight end-to-end tests: most spawn one or more real net48 satellite OS processes
// (Fct.LegacyHost.exe), and several bring up the real OverlayPlugin with its CEF + Fleck WebSocket stack.
// Run concurrently they oversubscribe the machine — CEF startup and the MiniParse push timer get CPU-starved
// past even generous adaptive waits, which surfaced as intermittent overlay/priming flakes under load. The
// `satellite` / `satellite-p6` collections only serialize their own members; ~20 other satellite-spawning
// classes are ungrouped and still ran in parallel with them. Serializing the whole assembly removes the
// oversubscription at the root; the per-test adaptive waits (WaitForTerminalEncounter, the sink-artifact
// poll) remain as defense-in-depth. The fast in-proc tests here are cheap, so the wall-clock cost is small.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
