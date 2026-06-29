using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Fct.App;

public partial class App : Application
{
    // Set by Program before the Avalonia lifetime starts; null only under the XAML previewer.
    internal static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services?.GetRequiredService<MainWindow>() ?? new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
