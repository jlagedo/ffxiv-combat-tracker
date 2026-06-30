using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.LegacyHost.Logging;
using Fct.Logging;
using Fct.Parser.Legacy;
using Microsoft.Extensions.Logging;

namespace Fct.LegacyHost
{
    // Stands up the ACT facade and loads the real (net48) plugins into WinForms tabs.
    internal static class FacadeHost
    {
        // Legacy delegate seam handed to the ACT facade (FormActMain.Log) and the plugin wrapper; the
        // satellite points it at SatelliteLogging.WriteLegacy. FacadeHost's own diagnostics go through
        // the ILogger directly (SatelliteLogging.Log).
        public static Action<string> Log = _ => { };

        public static string AppData => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Advanced Combat Tracker");

        public static string FfxivPluginPath => Path.Combine(AppData, "Plugins", "FFXIV_ACT_Plugin.dll");

        public static string OverlayPluginPath =>
            Path.Combine(AppData, "Plugins", "OverlayPlugin", "OverlayPlugin.dll");

        // The diagnostic stream-probe plugin, staged next to the satellite under plugins\.
        public static string StreamProbePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "Fct.StreamProbe.dll");

        // Resolve the plugins' strong-named reference to "Advanced Combat Tracker" to our
        // facade. The strong name/version are not re-checked on the AssemblyResolve path.
        //
        // Also unify FFXIV_ACT_Plugin.Common onto the single copy the real plugin (Costura)
        // already loaded. Our wrapper (Fct.Parser.Legacy) references the SDK Common by identity
        // 3.0.0.0/9f740ac505d6bc50; this hands it the live instance so IDataSubscription is one
        // type across the wrapper, the real plugin, and OverlayPlugin. Without this the casts in
        // WrappedFfxivPlugin.BindReal — and OverlayPlugin's own — would see two distinct types.
        public static void InstallAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                if (name == "Advanced Combat Tracker")
                    return typeof(ActGlobals).Assembly;
                if (name == "FFXIV_ACT_Plugin.Common")
                    return AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "FFXIV_ACT_Plugin.Common");
                return null;
            };
        }

        // Load the real FFXIV_ACT_Plugin, then place a WrappedFfxivPlugin in pluginObj so the
        // unmodified OverlayPlugin binds to our ring-buffer IDataSubscription instead of the
        // plugin's per-subscriber BeginInvoke fan-out. DataRepository/_iocContainer/raw packets
        // still come from the real instance. Title stays "FFXIV_ACT_Plugin" for discovery.
        public static LoadedPlugin LoadWrappedFfxivPlugin(string dllPath)
        {
            const string title = "FFXIV_ACT_Plugin";
            var window = CreateHostWindow(out var tabs);
            var status = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "(loading)" };
            var tab = new TabPage(title);
            tabs.TabPages.Add(tab);

            var data = new ActPluginData
            {
                pluginFile = new FileInfo(dllPath),
                cbEnabled = new CheckBox { Checked = true },
                lblPluginTitle = new Label { Text = title },
                lblPluginStatus = status,
                tpPluginSpace = tab,
                pPluginInfo = new Panel(),
            };
            ActGlobals.oFormActMain.ActPlugins.Add(data);
            var result = new LoadedPlugin { Key = "ffxiv", Title = title, Data = data, Window = window };

            try
            {
                if (!File.Exists(dllPath))
                {
                    SatelliteLogging.Log.LogError(LogEvents.PluginNotFound, "[{Title}] DLL not found: {Path}", title, dllPath);
                    status.Text = "DLL not found";
                    return result;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = asm.GetType("FFXIV_ACT_Plugin.FFXIV_ACT_Plugin") ?? FindPluginType(asm);
                if (type == null) { SatelliteLogging.Log.LogError(LogEvents.PluginLoadFailed, "[{Title}] no IActPluginV1 type found", title); return result; }

                var real = (IActPluginV1)Activator.CreateInstance(type);
                var wrapper = new WrappedFfxivPlugin(real, ringCapacity: 4096, log: Log);
                data.pluginObj = wrapper;          // OverlayPlugin reflects this for the SDK surface
                data.pluginVersion = asm.GetName().Version?.ToString();
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInstantiated,
                    "[{Title}] instantiated {Type} v{Version}; wrapping + InitPlugin…", title, type.FullName, data.pluginVersion);
                wrapper.InitPlugin(tab, status);   // forwards to real, then binds the ring dispatcher
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInitialized, "[{Title}] InitPlugin returned. status='{Status}'", title, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = "load failed";
                SatelliteLogging.Log.LogError(LogEvents.PluginLoadFailed, ex, "[{Title}] wrapped load failed", title);
            }
            return result;
        }

        // Load a headless diagnostic plugin (no embeddable window): the stream probe consumes data
        // and logs to a file, so it needs no UI. It still goes into ActPlugins (faithful: it appears
        // as a loaded plugin and gets DeInitPlugin on shutdown to flush its log), but it does not
        // match OverlayPlugin's "FFXIV_ACT_Plugin"-titled discovery scan, so it is inert to others.
        // Loaded via Assembly.LoadFrom — its compile-time references to the real "Advanced Combat
        // Tracker" / "FFXIV_ACT_Plugin.Common" resolve through the satellite's AssemblyResolve, so
        // this also proves the facade is API-compatible with the real ACT surface it was built on.
        public static LoadedPlugin LoadProbe(string dllPath)
        {
            const string title = "Fct.StreamProbe";
            var status = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "(loading)" };
            var tab = new TabPage(title);   // detached: the probe ignores it

            var data = new ActPluginData
            {
                pluginFile = new FileInfo(dllPath),
                cbEnabled = new CheckBox { Checked = true },
                lblPluginTitle = new Label { Text = title },
                lblPluginStatus = status,
                tpPluginSpace = tab,
                pPluginInfo = new Panel(),
            };
            ActGlobals.oFormActMain.ActPlugins.Add(data);
            var result = new LoadedPlugin { Key = "probe", Title = title, Data = data, Window = null };

            try
            {
                if (!File.Exists(dllPath))
                {
                    SatelliteLogging.Log.LogWarning(LogEvents.PluginNotFound, "[{Title}] DLL not found: {Path}", title, dllPath);
                    status.Text = "DLL not found";
                    return result;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = asm.GetType("Fct.StreamProbe.StreamProbePlugin") ?? FindPluginType(asm);
                if (type == null) { SatelliteLogging.Log.LogWarning(LogEvents.PluginLoadFailed, "[{Title}] no IActPluginV1 type found", title); return result; }

                var plugin = (IActPluginV1)Activator.CreateInstance(type);
                data.pluginObj = plugin;
                data.pluginVersion = asm.GetName().Version?.ToString();
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInstantiated,
                    "[{Title}] instantiated {Type} v{Version}; calling InitPlugin…", title, type.FullName, data.pluginVersion);
                plugin.InitPlugin(tab, status);
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInitialized, "[{Title}] InitPlugin returned. status='{Status}'", title, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = "load failed";
                SatelliteLogging.Log.LogError(LogEvents.PluginLoadFailed, ex, "[{Title}] load failed", title);
            }
            return result;
        }

        public static FormActMain CreateAct()
        {
            FormActMain.Log = Log;
            var act = new FormActMain();
            ActGlobals.oFormActMain = act;
            // Show (off-screen, transparent, non-activating) so Control.Visible is true: the plugin's
            // ScanMemory/LogOutput threads gate on IsMainFormVisible() before emitting combat data.
            act.Show();
            return act;
        }

        public static LoadedPlugin LoadPlugin(string key, string title, string dllPath, string knownTypeName)
        {
            var window = CreateHostWindow(out var tabs);
            var status = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "(loading)" };
            var tab = new TabPage(title);
            tabs.TabPages.Add(tab);

            var data = new ActPluginData
            {
                pluginFile = new FileInfo(dllPath),
                cbEnabled = new CheckBox { Checked = true },
                lblPluginTitle = new Label { Text = title },
                lblPluginStatus = status,
                tpPluginSpace = tab,
                pPluginInfo = new Panel(),
            };
            ActGlobals.oFormActMain.ActPlugins.Add(data);
            var result = new LoadedPlugin { Key = key, Title = title, Data = data, Window = window };

            try
            {
                if (!File.Exists(dllPath))
                {
                    SatelliteLogging.Log.LogError(LogEvents.PluginNotFound, "[{Title}] DLL not found: {Path}", title, dllPath);
                    status.Text = "DLL not found";
                    return result;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = (knownTypeName != null ? asm.GetType(knownTypeName) : null) ?? FindPluginType(asm);
                if (type == null) { SatelliteLogging.Log.LogError(LogEvents.PluginLoadFailed, "[{Title}] no IActPluginV1 type found", title); return result; }

                var plugin = (IActPluginV1)Activator.CreateInstance(type);
                data.pluginObj = plugin;
                data.pluginVersion = asm.GetName().Version?.ToString();
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInstantiated,
                    "[{Title}] instantiated {Type} v{Version}; calling InitPlugin…", title, type.FullName, data.pluginVersion);
                plugin.InitPlugin(tab, status);
                SatelliteLogging.Log.LogInformation(LogEvents.PluginInitialized, "[{Title}] InitPlugin returned. status='{Status}'", title, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = "load failed";
                SatelliteLogging.Log.LogError(LogEvents.PluginLoadFailed, ex, "[{Title}] load failed", title);
            }
            return result;
        }

        // A borderless, off-screen WinForms window with a docked TabControl, ready to host one
        // plugin's configuration tabs. Each plugin gets its own so the Avalonia host can embed
        // them independently. The window is shown off-screen so WinForms runs a real layout pass
        // before it's reparented into the host; NativeControlHost re-positions it on embed.
        private static Form CreateHostWindow(out TabControl tabs)
        {
            var form = new HostForm
            {
                Text = "Fct.PluginHost",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                ShowInTaskbar = false,
                Width = 900,
                Height = 560,
                BackColor = System.Drawing.Color.FromArgb(248, 250, 252),
            };
            tabs = new TabControl { Dock = DockStyle.Fill };
            form.Controls.Add(tabs);
            _ = form.Handle; // realize the HWND on this WinForms thread

            // Show it (off-screen, without stealing foreground) so WinForms performs a genuine
            // layout pass against a real client size. Plugin panels built on SplitContainer /
            // percentage-row TableLayoutPanel (e.g. OverlayPlugin's ControlPanel) collapse to
            // zero height if they're only ever laid out while the host window is hidden. Once the
            // window is visible, controls the plugin adds afterward lay out live.
            form.Show();
            return form;
        }

        // The off-screen plugin host window: shown before embedding to force a layout pass, but
        // kept out of the foreground (ShowWithoutActivation) and the Alt-Tab list (WS_EX_TOOLWINDOW)
        // so the user never sees a stray window in the moment before it's reparented.
        private sealed class HostForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
        }

        // De-initialise every loaded plugin so it persists its state, mirroring ACT's shutdown
        // (FormActMain.SaveXmlActPlugins): set IsActClosing, then walk ActPlugins in load order
        // calling DeInitPlugin on each, swallowing per-plugin failures so one bad plugin can't
        // block the rest. For the FFXIV plugin, pluginObj is the WrappedFfxivPlugin, whose
        // DeInitPlugin de-inits the real plugin then disposes the ring; for OverlayPlugin it is the
        // real loader, whose DeInitPlugin force-saves its config (overlay positions, toggles).
        public static void DeInitPlugins()
        {
            var act = ActGlobals.oFormActMain;
            if (act == null) return;

            act.IsActClosing = true;
            foreach (var data in act.ActPlugins)
            {
                if (data?.pluginObj == null) continue;
                var name = data.lblPluginTitle?.Text ?? "(plugin)";
                try
                {
                    Log($"[Info] Calling deinit for {name}...");
                    data.pluginObj.DeInitPlugin();
                }
                catch (Exception ex)
                {
                    Log($"[Exception] DeInit {name}: {ex}");
                }
            }
            Log("[Info] Plugin deinit complete.");
        }

        private static Type FindPluginType(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            return types.FirstOrDefault(t =>
                typeof(IActPluginV1).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
        }
    }

    // A plugin loaded into its own embeddable host window. The host embeds Window's HWND on
    // demand so each plugin shows only its own configuration tabs.
    internal sealed class LoadedPlugin
    {
        public string Key;
        public string Title;
        public ActPluginData Data;
        public System.Windows.Forms.Form Window;
        public IntPtr Hwnd => Window?.Handle ?? IntPtr.Zero;
        public string Status => Data?.lblPluginStatus?.Text ?? "";
    }
}
