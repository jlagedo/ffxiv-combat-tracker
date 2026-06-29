using Microsoft.Extensions.Logging;

namespace Fct.Logging
{
    // Stable EventId taxonomy for the whole stack. Ranges group the important junctions by
    // subsystem so logs filter and correlate across the two-process bridge. This file is shared
    // source: it is compiled into both the net10 host (Fct.App) and the net48 satellite
    // (Fct.LegacyHost), so a given junction carries the same numeric id on both sides of the pipe.
    //
    //   1xxx  host process            (launch, lifetime, bridge, embedding)
    //   2xxx  satellite process       (boot, plugin load, dispatch, diagnostics)
    //   3xxx  native parser
    internal static class LogEvents
    {
        // -- 10xx host lifecycle --
        public static readonly EventId HostStarting = new EventId(1000, nameof(HostStarting));
        public static readonly EventId HostStarted = new EventId(1001, nameof(HostStarted));
        public static readonly EventId HostStopping = new EventId(1002, nameof(HostStopping));
        public static readonly EventId HostUnhandledException = new EventId(1003, nameof(HostUnhandledException));

        // -- 11xx satellite process control (host side) --
        public static readonly EventId SatelliteLaunching = new EventId(1100, nameof(SatelliteLaunching));
        public static readonly EventId SatelliteStarted = new EventId(1101, nameof(SatelliteStarted));
        public static readonly EventId SatelliteLaunchFailed = new EventId(1102, nameof(SatelliteLaunchFailed));
        public static readonly EventId SatelliteHandshakeTimeout = new EventId(1103, nameof(SatelliteHandshakeTimeout));
        public static readonly EventId SatelliteExited = new EventId(1104, nameof(SatelliteExited));
        public static readonly EventId SatelliteNotStaged = new EventId(1105, nameof(SatelliteNotStaged));
        public static readonly EventId JobObjectUnavailable = new EventId(1106, nameof(JobObjectUnavailable));

        // -- 12xx bridge / IPC (host side) --
        public static readonly EventId BridgeConnected = new EventId(1200, nameof(BridgeConnected));
        public static readonly EventId BridgeHandshake = new EventId(1201, nameof(BridgeHandshake));
        public static readonly EventId BridgePluginAnnounced = new EventId(1202, nameof(BridgePluginAnnounced));
        public static readonly EventId BridgeReaderStopped = new EventId(1203, nameof(BridgeReaderStopped));
        public static readonly EventId BridgeFrameMalformed = new EventId(1204, nameof(BridgeFrameMalformed));

        // -- 13xx window embedding (host side) --
        public static readonly EventId WindowReparented = new EventId(1300, nameof(WindowReparented));
        public static readonly EventId WindowReparentFailed = new EventId(1301, nameof(WindowReparentFailed));

        // -- 20xx satellite lifecycle (satellite side) --
        public static readonly EventId SatelliteBooting = new EventId(2000, nameof(SatelliteBooting));
        public static readonly EventId SatelliteBridgeConnected = new EventId(2001, nameof(SatelliteBridgeConnected));
        public static readonly EventId SatelliteBridgeConnectFailed = new EventId(2002, nameof(SatelliteBridgeConnectFailed));
        public static readonly EventId FacadeCreated = new EventId(2003, nameof(FacadeCreated));

        // -- 21xx plugin load (satellite side) --
        public static readonly EventId PluginLoading = new EventId(2100, nameof(PluginLoading));
        public static readonly EventId PluginInstantiated = new EventId(2101, nameof(PluginInstantiated));
        public static readonly EventId PluginInitialized = new EventId(2102, nameof(PluginInitialized));
        public static readonly EventId PluginLoadFailed = new EventId(2103, nameof(PluginLoadFailed));
        public static readonly EventId PluginNotFound = new EventId(2104, nameof(PluginNotFound));
        public static readonly EventId PluginsReady = new EventId(2105, nameof(PluginsReady));

        // -- 22xx dispatch / ring buffer (satellite side) --
        public static readonly EventId RealPluginBound = new EventId(2200, nameof(RealPluginBound));
        public static readonly EventId RealSubscriptionNull = new EventId(2201, nameof(RealSubscriptionNull));
        public static readonly EventId SubscriberThrew = new EventId(2202, nameof(SubscriberThrew));
        public static readonly EventId PacketsDropped = new EventId(2203, nameof(PacketsDropped));

        // -- 23xx diagnostics / self-test (satellite side) --
        public static readonly EventId SelfTest = new EventId(2300, nameof(SelfTest));
        public static readonly EventId DispatcherDiagnostics = new EventId(2301, nameof(DispatcherDiagnostics));
        public static readonly EventId Summary = new EventId(2302, nameof(Summary));

        // -- 24xx ACT facade surfaces (satellite side; routed from the legacy plugins) --
        public static readonly EventId ActInfo = new EventId(2400, nameof(ActInfo));
        public static readonly EventId ActDebug = new EventId(2401, nameof(ActDebug));
        public static readonly EventId ActException = new EventId(2402, nameof(ActException));
        public static readonly EventId ActNotification = new EventId(2403, nameof(ActNotification));
        public static readonly EventId ActCommand = new EventId(2404, nameof(ActCommand));
        public static readonly EventId ActRestartRequested = new EventId(2405, nameof(ActRestartRequested));

        // -- 30xx native parser --
        public static readonly EventId LineDropped = new EventId(3000, nameof(LineDropped));
        public static readonly EventId UnknownLineType = new EventId(3001, nameof(UnknownLineType));
        public static readonly EventId ParseAnomaly = new EventId(3002, nameof(ParseAnomaly));
    }

    // Stable category names (the ILogger<T> category is the type's full name by default; these are
    // for the hand-built loggers where a type isn't the natural category — e.g. forwarded satellite
    // records re-emitted on the host).
    internal static class LogCategories
    {
        public const string Satellite = "Fct.Satellite";
        public const string Bridge = "Fct.Bridge";
    }
}
