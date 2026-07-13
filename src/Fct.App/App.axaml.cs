using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Fct.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.App;

public partial class App : Application
{
    // Set by Program before the Avalonia lifetime starts; null only under the XAML previewer.
    internal static IServiceProvider? Services { get; set; }

    // The Generic Host, set by Program but NOT started there — the shell starts it from
    // MainWindow.OnOpened so hosted-service work never blocks the window from painting. Null under
    // the XAML previewer.
    internal static IHost? Host { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The last-resort net for the Avalonia UI thread: the satellite guards its WinForms pump and
        // Program.cs guards AppDomain/TaskScheduler, but a throw in a UI event handler or binding would
        // otherwise escalate straight to a fatal AppDomain crash. Log it (Critical) and keep the app
        // alive — a single UI glitch must not take down the whole tracker.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Services?.GetService<ILogger<App>>()?.LogCritical(
                LogEvents.UiUnhandledException, e.Exception, "Unhandled Avalonia UI-thread exception");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services?.GetRequiredService<MainWindow>() ?? new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        // The Avalonia lifetime is up and the shell window is shown. The host runtime is not started
        // yet — MainWindow.OnOpened brings it online after first paint (and logs HostStarted then).
        Services?.GetService<ILogger<App>>()?.LogInformation(LogEvents.UiShellShown, "UI shell shown");
    }
}
