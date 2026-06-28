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
            var handshake = await new SatelliteHost().StartAsync();
            var msg = "Satellite handshake OK:\n" + handshake;
            StatusText.Text = msg;
            File.WriteAllText(logPath, msg);
        }
        catch (Exception ex)
        {
            var msg = "Satellite launch FAILED:\n" + ex;
            StatusText.Text = msg;
            File.WriteAllText(logPath, msg);
        }
    }
}