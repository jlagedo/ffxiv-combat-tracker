using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Fct.App.ViewModels;

namespace Fct.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // Open the logs directory in the OS file browser (creating it if the app hasn't written yet).
    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            Directory.CreateDirectory(vm.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = vm.LogsDirectory, UseShellExecute = true });
        }
        catch { /* best-effort convenience action */ }
    }
}
