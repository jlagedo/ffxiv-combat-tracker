using System;
using System.IO;
using System.IO.Pipes;
using System.Windows.Forms;

namespace Fct.LegacyHost
{
    // The net48 satellite entry point. For S0 this is a placeholder WinForms host;
    // it grows into the ACT facade host (TabControl + plugin loading) in later steps.
    internal static class Program
    {
        // Kept alive for the process lifetime so the handshake pipe stays open.
        private static NamedPipeClientStream _bridge;

        [STAThread]
        private static void Main(string[] args)
        {
            var pipeName = ParseBridgeArg(args);
            if (pipeName != null)
                TrySendHandshake(pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var form = new Form
            {
                Text = "Fct.LegacyHost (net48 satellite)",
                Width = 480,
                Height = 320
            };
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text = $"Fct.LegacyHost running\n64-bit: {Environment.Is64BitProcess}\nCLR: {Environment.Version}"
            });

            Application.Run(form);
        }

        private static string ParseBridgeArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--bridge")
                    return args[i + 1];
            return null;
        }

        private static void TrySendHandshake(string pipeName)
        {
            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                var writer = new StreamWriter(_bridge) { AutoFlush = true };
                writer.WriteLine(
                    $"READY pid={System.Diagnostics.Process.GetCurrentProcess().Id} " +
                    $"x64={Environment.Is64BitProcess} clr={Environment.Version}");
                // Leave _bridge open for the process lifetime (handshake channel).
            }
            catch
            {
                // S0: a missing/closed pipe is non-fatal; the satellite still runs standalone.
            }
        }
    }
}
