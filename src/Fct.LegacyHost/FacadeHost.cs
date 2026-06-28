using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

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

        // Resolve the plugins' strong-named reference to "Advanced Combat Tracker" to our
        // facade. The strong name/version are not re-checked on the AssemblyResolve path.
        public static void InstallAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                if (name == "Advanced Combat Tracker")
                    return typeof(ActGlobals).Assembly;
                return null;
            };
        }

        public static FormActMain CreateAct()
        {
            FormActMain.Log = Log;
            var act = new FormActMain();
            ActGlobals.oFormActMain = act;
            _ = act.Handle; // realize the handle on this (WinForms) thread
            return act;
        }

        public static ActPluginData LoadPlugin(TabControl tabs, string title, string dllPath, string knownTypeName)
        {
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

            try
            {
                if (!File.Exists(dllPath))
                {
                    Log($"[{title}] DLL not found: {dllPath}");
                    status.Text = "DLL not found";
                    return data;
                }

                var asm = Assembly.LoadFrom(dllPath);
                var type = asm.GetType(knownTypeName) ?? FindPluginType(asm);
                if (type == null) { Log($"[{title}] no IActPluginV1 type found"); return data; }

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
            return data;
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
}
