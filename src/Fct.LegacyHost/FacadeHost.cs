using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.Parser.Legacy;

namespace Fct.LegacyHost
{
    // Stands up the ACT facade and loads the real (net48) plugins into WinForms tabs.
    internal static class FacadeHost
    {
        public static Action<string> Log = _ => { };

        public static string AppData => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Advanced Combat Tracker");

        public static string FfxivPluginPath => Path.Combine(AppData, "Plugins", "FFXIV_ACT_Plugin.dll");

        public static string OverlayPluginPath =>
            @"E:\dev\Advanced Combat Tracker\OverlayPlugin-0.16.5\OverlayPlugin.dll";

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
                    Log($"[{title}] DLL not found: {dllPath}");
                    status.Text = "DLL not found";
                    return result;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = asm.GetType("FFXIV_ACT_Plugin.FFXIV_ACT_Plugin") ?? FindPluginType(asm);
                if (type == null) { Log($"[{title}] no IActPluginV1 type found"); return result; }

                var real = (IActPluginV1)Activator.CreateInstance(type);
                var wrapper = new WrappedFfxivPlugin(real, ringCapacity: 4096, log: Log);
                data.pluginObj = wrapper;          // OverlayPlugin reflects this for the SDK surface
                data.pluginVersion = asm.GetName().Version?.ToString();
                Log($"[{title}] instantiated {type.FullName} v{data.pluginVersion}; wrapping + InitPlugin…");
                wrapper.InitPlugin(tab, status);   // forwards to real, then binds the ring dispatcher
                Log($"[{title}] InitPlugin returned. status='{status.Text}'");
            }
            catch (Exception ex)
            {
                status.Text = "load failed";
                Log($"[{title}] WRAPPED LOAD FAILED: {ex}");
            }
            return result;
        }

        public static FormActMain CreateAct()
        {
            FormActMain.Log = Log;
            var act = new FormActMain();
            ActGlobals.oFormActMain = act;
            _ = act.Handle; // realize the handle on this (WinForms) thread
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
                    Log($"[{title}] DLL not found: {dllPath}");
                    status.Text = "DLL not found";
                    return result;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = (knownTypeName != null ? asm.GetType(knownTypeName) : null) ?? FindPluginType(asm);
                if (type == null) { Log($"[{title}] no IActPluginV1 type found"); return result; }

                var plugin = (IActPluginV1)Activator.CreateInstance(type);
                data.pluginObj = plugin;
                data.pluginVersion = asm.GetName().Version?.ToString();
                Log($"[{title}] instantiated {type.FullName} v{data.pluginVersion}; calling InitPlugin…");
                plugin.InitPlugin(tab, status);
                Log($"[{title}] InitPlugin returned. status='{status.Text}'");
            }
            catch (Exception ex)
            {
                status.Text = "load failed";
                Log($"[{title}] LOAD FAILED: {ex}");
            }
            return result;
        }

        // A borderless, off-screen WinForms window with a docked TabControl, ready to host one
        // plugin's configuration tabs. Each plugin gets its own so the Avalonia host can embed
        // them independently. The handle is realized here (off-screen) so it never flashes before
        // it's reparented into the host; NativeControlHost shows + positions it on embed.
        private static Form CreateHostWindow(out TabControl tabs)
        {
            var form = new Form
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
            return form;
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
