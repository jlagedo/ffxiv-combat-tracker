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

        // Overrides InstallDirectory (where the staged satellite\/compat\/plugins\ siblings live).
        // Used by tests that drive the host runtime out-of-process from the staged app tree, and by any
        // portable layout that separates the launcher from the staged runtime.
        public const string InstallDirEnvVar = "FCT_INSTALL_DIR";

        // The directory the application was launched from — where its staged sibling folders
        // (satellite\, compat\, plugins\) and, in DEBUG, its data root live. Distinct from
        // AppContext.BaseDirectory: a single-file self-extracting host runs its managed assemblies out
        // of a temp extraction directory (which BaseDirectory points at), but the staged siblings stay
        // next to the launched .exe. On net48 (never single-file) the two are identical.
        public static string InstallDirectory
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable(InstallDirEnvVar);
                if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
#if NET
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
#endif
                return AppContext.BaseDirectory;
            }
        }

        public static string Root
        {
            get
            {
                var overrideRoot = Environment.GetEnvironmentVariable(RootEnvVar);
                if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;
#if DEBUG
                return Path.Combine(InstallDirectory, "FFXIVCombatTracker");
#else
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FFXIVCombatTracker");
#endif
            }
        }
    }
}
