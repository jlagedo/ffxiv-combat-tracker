using System;
using System.IO;

namespace Fct.Logging
{
    // The single application-data root shared by both processes. Everything the app persists — logs,
    // UI settings, the installed-plugin registry, per-plugin storage — lives under this folder.
    //
    // In DEBUG the root is a "FFXIVCombatTracker" folder next to the executable, so the whole
    // configuration can be inspected, edited, and deleted in one place while debugging. In RELEASE it
    // is the per-user %LOCALAPPDATA%\FFXIVCombatTracker location. The FCT_DATA_ROOT environment
    // variable overrides both; the host sets it on the satellite process so both runtimes resolve to
    // one root (unified logs + a single folder to clear).
    public static class AppData
    {
        // Set by the host on the satellite's environment to hand it the host's resolved root.
        public const string RootEnvVar = "FCT_DATA_ROOT";

        public static string Root
        {
            get
            {
                var overrideRoot = Environment.GetEnvironmentVariable(RootEnvVar);
                if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;
#if DEBUG
                return Path.Combine(AppContext.BaseDirectory, "FFXIVCombatTracker");
#else
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FFXIVCombatTracker");
#endif
            }
        }
    }
}
