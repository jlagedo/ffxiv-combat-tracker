using System;
using System.IO;
using System.IO.Pipes;
using System.Windows.Forms;

namespace Fct.LegacyHost
{
    // The net48 satellite. Hosts the real FFXIV_ACT_Plugin + OverlayPlugin and the ACT
    // facade (later steps). For S1 it creates a borderless WinForms window and hands its
    // HWND to the host, which reparents it into the Avalonia window.
    internal static class Program
    {
        private static NamedPipeClientStream _bridge;
        private static StreamWriter _writer;

        [STAThread]
        private static void Main(string[] args)
        {
            var pipeName = ParseBridgeArg(args);
            if (pipeName != null)
                ConnectBridge(pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new Form
            {
                Text = "Fct.LegacyHost",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                Width = 600,
                Height = 360,
                BackColor = System.Drawing.Color.FromArgb(248, 250, 252),
            };
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new System.Drawing.Font("Segoe UI", 11f),
                Text = $"Embedded net48 satellite (WinForms, x64={Environment.Is64BitProcess})\n" +
                       "This content lives in Fct.LegacyHost and is reparented into the Avalonia host."
            });

            // Force native handle creation, then hand the HWND to the host for reparenting.
            var handle = form.Handle;
            SendLine($"HWND {handle.ToInt64():X}");

            if (pipeName != null)
                // Host owns visibility (reparent + ShowWindow); run a bare message loop so
                // the form's HWND keeps pumping on this thread.
                Application.Run(new ApplicationContext());
            else
                // Standalone (no host): show the form normally.
                Application.Run(form);

            GC.KeepAlive(form);
        }

        private static string ParseBridgeArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--bridge")
                    return args[i + 1];
            return null;
        }

        private static void ConnectBridge(string pipeName)
        {
            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine($"READY pid={System.Diagnostics.Process.GetCurrentProcess().Id} " +
                         $"x64={Environment.Is64BitProcess} clr={Environment.Version}");
            }
            catch
            {
                _bridge = null;
                _writer = null;
            }
        }

        private static void SendLine(string s)
        {
            try { _writer?.WriteLine(s); } catch { }
        }
    }
}
