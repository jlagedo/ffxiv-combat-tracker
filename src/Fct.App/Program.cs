using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Avalonia;
using Fct.Abstractions;
using Fct.App.Logging;
using Fct.App.ViewModels;
using Fct.Host;
using Fct.Host.Plugins;
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
        // Resolve the UI culture before anything else touches Resources or formats a string.
        Fct.App.Lang.LocalizationStartup.Initialize();

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
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // Serilog is the sole logging backend; drop the default console/debug/eventsource providers.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: false);

        // The headless host runtime: IPluginHost services, the ALC plugin loader, and the satellite
        // bridge client. Everything below is the Avalonia shell + composition-root wiring that binds
        // it to the UI. Live game data reaches the bus via the net48→net10 bridge forwarder (piece C):
        // SatelliteHost decodes EVT frames from the satellite and publishes them through IGameEventSink.
        builder.Services.AddFctHostServices();

        // Shell view + view model.
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // Localized text for the satellite's notifications (host runtime binds ISatelliteNotificationText).
        builder.Services.AddSingleton<ISatelliteNotificationText, SatelliteNotificationText>();

        // The compat shim's legacy-plugin host factory (injected into PluginManager for `legacyEntry`
        // manifests). The shim + its two impersonation facades are a staged compat\ package resolved
        // into the default ALC by CompatRuntime — NOT a static reference of this exe — so the shim's
        // LegacyPluginHost is materialized reflectively here (no compile-time shim dependency, no
        // impersonation identity in Fct.App's deps.json). The (IPlugin) cast is safe because
        // Fct.Abstractions is single-identity (shared up to the default context).
        builder.Services.AddSingleton<LegacyPluginHostFactory>(_ => (assembly, legacyEntry) =>
        {
            var shim = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Fct.Compat.Shim"));
            var hostType = shim.GetType("Fct.Compat.Shim.LegacyPluginHost", throwOnError: true)!;
            return (IPlugin)Activator.CreateInstance(hostType, assembly, legacyEntry)!;
        });

        // The plugin UI dispatcher: Avalonia's UI thread, owned by the shell (kept out of Fct.Host so
        // the runtime takes no Avalonia.Threading dependency). The PluginUiCoordinator that consumes it
        // is registered by AddFctHostServices.
        builder.Services.AddSingleton<Fct.Abstractions.UI.IUiDispatcher, Fct.App.Plugins.Ui.AvaloniaUiDispatcher>();

        var host = builder.Build();

        // Opt in to the staged legacy compat runtime: teach the default ALC to resolve the shim + its
        // two impersonation facades from compat\ (they are staged there, not baked into this exe's
        // deps.json). Subscribed here — before host.Start() — so it is live before any plugin loads.
        CompatRuntime.Enable(
            Path.Combine(AppData.InstallDirectory, "compat"),
            host.Services.GetRequiredService<ILoggerFactory>());

        return host;
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
