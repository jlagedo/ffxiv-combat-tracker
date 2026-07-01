using System;
using System.Threading.Tasks;
using Avalonia;
using Fct.Abstractions;
using Fct.App.Hosting;
using Fct.App.Logging;
using Fct.App.Plugins;
using Fct.App.ViewModels;
using Fct.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Fct.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Stand the logger up first so even a failure building the host is recorded.
        Log.Logger = LoggingBootstrap.CreateLogger();

        IHost? host = null;
        try
        {
            host = BuildHost(args);
            App.Services = host.Services;

            var log = host.Services.GetRequiredService<ILogger<Program>>();
            log.LogInformation(LogEvents.HostStarting, "FFXIV Combat Tracker host starting (pid {Pid})",
                Environment.ProcessId);
            HookProcessWideExceptions(log);

            host.Start();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.LogInformation(LogEvents.HostStopping, "Host shutting down");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            if (host is not null)
            {
                host.StopAsync().GetAwaiter().GetResult();
                host.Dispose();   // disposes the Serilog provider → flushes sinks
            }
            Log.CloseAndFlush();
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Serilog is the sole logging backend; drop the default console/debug/eventsource providers.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: false);

        builder.Services.AddSingleton<SatelliteHost>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // Gracefully de-init the satellite's plugins on host shutdown (persists their state).
        builder.Services.AddHostedService<SatelliteLifetime>();

        // Native plugin host (slice A+B): the real IPluginHost services + the ALC-per-plugin loader.
        builder.Services.AddSingleton<GameEventBus>();
        builder.Services.AddSingleton<IGameEventSink>(sp => sp.GetRequiredService<GameEventBus>());
        builder.Services.AddSingleton<GameSnapshotProvider>();
        builder.Services.AddSingleton<IGameSession, GameSession>();
        builder.Services.AddSingleton<RegistryService>();
        builder.Services.AddSingleton<IPluginRegistry>(sp => sp.GetRequiredService<RegistryService>());
        builder.Services.AddSingleton<IAudioOutput, AudioService>();
        builder.Services.AddSingleton<IEncounterService, EncounterService>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<PluginManager>();
        builder.Services.AddHostedService<PluginLifetime>();

#if DEBUG
        // TEMPORARY: synthetic game data until the net48→net10 bridge forwarder (piece C) exists.
        // Registered last so plugins are loaded + subscribed before it emits.
        builder.Services.AddSingleton<DevGameEventSource>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DevGameEventSource>());
#endif

        return builder.Build();
    }

    // Nothing should reach these, but if it does we want it in the log rather than a silent crash.
    private static void HookProcessWideExceptions(Microsoft.Extensions.Logging.ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(LogEvents.HostUnhandledException, e.ExceptionObject as Exception,
                "Unhandled AppDomain exception (terminating={Terminating})", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(LogEvents.HostUnhandledException, e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
