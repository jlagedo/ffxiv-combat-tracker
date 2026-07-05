using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Fct.Host.Plugins.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fct.Host;

// Registers the .NET 10 host runtime — the IPluginHost services, the ALC plugin loader, and the
// satellite bridge client — into the composition root. The Avalonia shell, the IUiDispatcher
// implementation, the localized ISatelliteNotificationText, and the LegacyPluginHostFactory (which
// binds Fct.Compat.Shim) stay in Fct.App and are registered there.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFctHostServices(this IServiceCollection services)
    {
        // User-facing notifications: one hub the whole app publishes to and the shell subscribes to.
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationHub>(sp => sp.GetRequiredService<NotificationService>());

        // Shell preferences (persisted JSON).
        services.AddSingleton<UiSettingsStore>();

        // Plugin install/uninstall: metadata classification, the persisted installed-set registry,
        // the install-dir convention, and the single install/uninstall entry point.
        services.AddSingleton<PluginClassifier>();
        services.AddSingleton<PluginRegistryStore>();
        services.AddSingleton<PluginInstallPaths>();
        services.AddSingleton<ISatellitePluginChannel>(sp => sp.GetRequiredService<SatelliteHost>());
        services.AddSingleton<PluginInstaller>();

        // The satellite decodes live game-event frames onto the bus (piece C) and surfaces notable
        // records as notifications.
        services.AddSingleton<SatelliteHost>(sp => new SatelliteHost(
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<IGameEventSink>(),
            sp.GetRequiredService<INotificationHub>(), sp.GetService<ISatelliteNotificationText>()));

        // Gracefully de-init the satellite's plugins on host shutdown (persists their state).
        services.AddHostedService<SatelliteLifetime>();

        // Native plugin host (slice A+B): the real IPluginHost services + the ALC-per-plugin loader.
        services.AddSingleton<GameEventBus>();
        services.AddSingleton<IGameEventSink>(sp => sp.GetRequiredService<GameEventBus>());
        services.AddSingleton<GameSnapshotProvider>();
        services.AddSingleton<IGameSession, GameSession>();
        // Folds the live event stream into the pull snapshot IDataRepository/consumers read.
        services.AddHostedService<GameSnapshotAggregator>();
        services.AddSingleton<RegistryService>();
        services.AddSingleton<IPluginRegistry>(sp => sp.GetRequiredService<RegistryService>());
        services.AddSingleton<IAudioOutput, AudioService>();
        services.AddSingleton<IClock, SystemClock>();

        // The modern encounter engine: the single source of truth for encounter calculations. It runs
        // the shared Fct.Aggregation engine fed by the typed CombatSwing/lifecycle feed off the bus, and
        // its EngineEncounterService projects the live encounter into the IEncounterService the UI reads.
        services.AddSingleton<Fct.Engine.ModernEncounterEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<Fct.Engine.ModernEncounterEngine>());
        services.AddSingleton<IEncounterService>(sp => new Fct.Engine.EngineEncounterService(
            sp.GetRequiredService<Fct.Engine.ModernEncounterEngine>(), sp.GetRequiredService<IClock>()));
        // Diagnostic: log the engine's live encounter so net10 aggregation is observable + comparable to
        // the satellite's [Capture] line in the unified host log.
        services.AddHostedService<EncounterHeartbeat>();
        services.AddSingleton<PluginManager>();
        services.AddHostedService<PluginLifetime>();

        // The modern plugin UI face (work item 9): IUiHost + the RegisterUi coordinator. Plugin init
        // runs before Avalonia starts, so RegisterUi is deferred to MainWindow.OnOpened. The
        // IUiDispatcher it depends on is registered by the shell (AvaloniaUiDispatcher).
        services.AddSingleton<PluginUiCoordinator>();

        return services;
    }
}
