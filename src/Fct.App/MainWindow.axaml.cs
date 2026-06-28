using Avalonia.Controls;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Fct.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        _ = StartSatelliteAsync();
    }

    private async Task StartSatelliteAsync()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "s0-handshake.log");
        try
        {
            var result = await new SatelliteHost().StartAsync();
            StatusText.Text =
                $"Satellite: {result.Handshake}   |   child HWND: 0x{result.WindowHandle.ToInt64():X}";
            File.WriteAllText(logPath, StatusText.Text);

            if (result.WindowHandle != IntPtr.Zero)
                EmbedHost.Child = new EmbeddedSatelliteView(result.WindowHandle);
            else
                StatusText.Text += "   (no HWND received — nothing to embed)";
        }
        catch (Exception ex)
        {
            var msg = "Satellite launch FAILED:\n" + ex;
            StatusText.Text = msg;
            File.WriteAllText(logPath, msg);
        }
    }
}