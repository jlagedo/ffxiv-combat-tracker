using System;
using System.IO;
using System.Windows.Forms;
using Advanced_Combat_Tracker;   // the compat-shim facade, NOT the real ACT — same type names, in-process

namespace Fct.SampleLegacyPlugin;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// A *recompile-shim* plugin — the migration on-ramp for an existing ACT plugin (stop 2 of 1→2→3).
//
// Your ACT code is UNCHANGED: you still implement IActPluginV1, still build a WinForms UI into the
// TabPage, still read ActGlobals. The only differences are the project (recompile against the shim
// facade, target net10.0-windows) and the manifest (name your type in `legacyEntry`, not `entry`).
// The shim re-projects the ACT programming model onto the modern host, so it runs in-process with no
// real ACT present. When you're ready for the typed API + Avalonia UI, port to Fct.SamplePlugin's
// model (stop 3) at your own pace. Full walkthrough: docs/PORTING.md.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SampleLegacyPlugin : IActPluginV1
{
    // Static counters let the load test observe that InitPlugin/DeInitPlugin ran across the ALC boundary.
    public static int InitCount;
    public static int DeInitCount;
    public static string LastStatus = string.Empty;

    private Label? _status;                 // the status label ACT hands us (shown in the plugin list row)
    private CheckBox? _announce;            // a config control, built into the tab
    private bool _settings;                 // our one persisted setting (announce combat start via TTS)

    // ACT calls this once when the plugin loads. pluginScreenSpace is your tab; pluginStatusText is
    // the one-line status shown next to the plugin's name.
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        InitCount++;
        _status = pluginStatusText;
        pluginStatusText.Text = "Sample legacy plugin online";
        LastStatus = pluginStatusText.Text;

        // Logging goes through the shared ActGlobals hub — the shim maps it onto IPluginHost.Logger.
        ActGlobals.oFormActMain.WriteInfoLog("SampleLegacyPlugin initialized");

        // CONFIGURATION — the ACT-era pattern: persist under the host-provided AppDataFolder. The shim
        // routes AppDataFolder to your private per-plugin storage dir. Guarded so a bad read never
        // breaks load.
        _settings = LoadSetting();

        // GETTING DATA — subscribe to combat lifecycle. The shim raises these off the host's engine,
        // so your existing handlers fire exactly as under ACT. Unsubscribe in DeInitPlugin.
        ActGlobals.oFormActMain.OnCombatStart += OnCombatStart;

        // UI — build your WinForms controls into the tab exactly as before. The shim hosts the TabPage
        // in the Avalonia shell via NativeControlHost, so it renders as-is.
        BuildUi(pluginScreenSpace);
    }

    // ACT calls this on unload — undo everything InitPlugin did.
    public void DeInitPlugin()
    {
        DeInitCount++;
        ActGlobals.oFormActMain.OnCombatStart -= OnCombatStart;
        SaveSetting(_settings);
        LastStatus = "offline";
        if (_status != null) _status.Text = LastStatus;
    }

    private void OnCombatStart(bool isImport, CombatToggleEventArgs args)
    {
        if (_status != null) _status.Text = "In combat";
        if (_settings) ActGlobals.oFormActMain.TTS("Combat start");   // shim → IAudioOutput
    }

    private void BuildUi(TabPage tab)
    {
        _announce = new CheckBox { Text = "Announce combat start (TTS)", Checked = _settings, AutoSize = true, Location = new System.Drawing.Point(12, 12) };
        _announce.CheckedChanged += (_, _) => { _settings = _announce.Checked; SaveSetting(_settings); };
        tab.Controls.Add(_announce);
    }

    // ── config helpers: one bool in a file under AppDataFolder ────────────────────────────────────
    private static string SettingsPath =>
        Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "sample-legacy.cfg");

    private static bool LoadSetting()
    {
        try { return File.Exists(SettingsPath) && bool.TryParse(File.ReadAllText(SettingsPath), out var v) && v; }
        catch { return false; }
    }

    private static void SaveSetting(bool value)
    {
        try { File.WriteAllText(SettingsPath, value.ToString()); } catch { /* best-effort */ }
    }
}
