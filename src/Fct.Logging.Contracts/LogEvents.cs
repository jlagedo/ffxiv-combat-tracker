using Microsoft.Extensions.Logging;

namespace Fct.Logging
{
    // Stable EventId taxonomy for the whole stack. Ranges group the important junctions by
    // subsystem so logs filter and correlate across the two-process bridge. This assembly
    // multi-targets net48;net10 so the net10 host (Fct.App) and the net48 satellite
    // (Fct.LegacyHost) share one identity, so a junction carries the same numeric id on both
    // sides of the pipe.
    //
    //   1xxx  host process            (launch, lifetime, bridge, embedding)
    //   2xxx  satellite process       (boot, plugin load, dispatch, diagnostics)
    //   3xxx  native parser
    public static class LogEvents
    {
        // -- 10xx host lifecycle --
        public static readonly EventId HostStarting = new EventId(1000, nameof(HostStarting));
        public static readonly EventId HostStarted = new EventId(1001, nameof(HostStarted));
        public static readonly EventId HostStopping = new EventId(1002, nameof(HostStopping));
        public static readonly EventId HostUnhandledException = new EventId(1003, nameof(HostUnhandledException));
        public static readonly EventId UiUnhandledException = new EventId(1004, nameof(UiUnhandledException));
        public static readonly EventId UiShellShown = new EventId(1005, nameof(UiShellShown));

        // -- 11xx satellite process control (host side) --
        public static readonly EventId SatelliteLaunching = new EventId(1100, nameof(SatelliteLaunching));
        public static readonly EventId SatelliteStarted = new EventId(1101, nameof(SatelliteStarted));
        public static readonly EventId SatelliteLaunchFailed = new EventId(1102, nameof(SatelliteLaunchFailed));
        public static readonly EventId SatelliteHandshakeTimeout = new EventId(1103, nameof(SatelliteHandshakeTimeout));
        public static readonly EventId SatelliteExited = new EventId(1104, nameof(SatelliteExited));
        public static readonly EventId SatelliteNotStaged = new EventId(1105, nameof(SatelliteNotStaged));
        public static readonly EventId JobObjectUnavailable = new EventId(1106, nameof(JobObjectUnavailable));
        public static readonly EventId SatelliteShutdownRequested = new EventId(1107, nameof(SatelliteShutdownRequested));
        public static readonly EventId SatelliteShutdownTimeout = new EventId(1108, nameof(SatelliteShutdownTimeout));

        // -- 12xx bridge / IPC (host side) --
        public static readonly EventId BridgeConnected = new EventId(1200, nameof(BridgeConnected));
        public static readonly EventId BridgeHandshake = new EventId(1201, nameof(BridgeHandshake));
        public static readonly EventId BridgePluginAnnounced = new EventId(1202, nameof(BridgePluginAnnounced));
        public static readonly EventId BridgeReaderStopped = new EventId(1203, nameof(BridgeReaderStopped));
        public static readonly EventId BridgeFrameMalformed = new EventId(1204, nameof(BridgeFrameMalformed));
        public static readonly EventId BridgeEventDecoded = new EventId(1205, nameof(BridgeEventDecoded));
        public static readonly EventId BridgeEventDecodeFailed = new EventId(1206, nameof(BridgeEventDecodeFailed));
        public static readonly EventId BridgeEgressFailed = new EventId(1207, nameof(BridgeEgressFailed));
        public static readonly EventId BridgeNotWired = new EventId(1209, nameof(BridgeNotWired));
        public static readonly EventId BridgeSubscribed = new EventId(1211, nameof(BridgeSubscribed));

        // -- 13xx window embedding (host side) --
        public static readonly EventId WindowReparented = new EventId(1300, nameof(WindowReparented));
        public static readonly EventId WindowReparentFailed = new EventId(1301, nameof(WindowReparentFailed));

        // -- 14xx native plugin host (net10 ALC loader + lifecycle) --
        public static readonly EventId NativePluginsScanning = new EventId(1400, nameof(NativePluginsScanning));
        public static readonly EventId NativePluginManifestRejected = new EventId(1401, nameof(NativePluginManifestRejected));
        public static readonly EventId NativePluginLoaded = new EventId(1402, nameof(NativePluginLoaded));
        public static readonly EventId NativePluginInitialized = new EventId(1403, nameof(NativePluginInitialized));
        public static readonly EventId NativePluginFaulted = new EventId(1404, nameof(NativePluginFaulted));
        public static readonly EventId NativePluginUnloaded = new EventId(1405, nameof(NativePluginUnloaded));
        public static readonly EventId NativePluginsReady = new EventId(1406, nameof(NativePluginsReady));
        public static readonly EventId NativePluginInstallFailed = new EventId(1407, nameof(NativePluginInstallFailed));
        public static readonly EventId CompatRuntimeStaging = new EventId(1408, nameof(CompatRuntimeStaging));

        // -- 15xx native plugin UI face (RegisterUi coordinator, host side) --
        public static readonly EventId NativePluginUiRegistered = new EventId(1500, nameof(NativePluginUiRegistered));
        public static readonly EventId NativePluginUiNotContributor = new EventId(1501, nameof(NativePluginUiNotContributor));
        public static readonly EventId NativePluginUiFaulted = new EventId(1502, nameof(NativePluginUiFaulted));
        public static readonly EventId NativePluginUiDuplicateSurface = new EventId(1503, nameof(NativePluginUiDuplicateSurface));

        // -- 16xx modern encounter engine (host side) --
        public static readonly EventId EncounterHeartbeat = new EventId(1600, nameof(EncounterHeartbeat));
        public static readonly EventId EncounterProjectionFailed = new EventId(1601, nameof(EncounterProjectionFailed));

        // -- 165x host bus dispatch (host side) --
        public static readonly EventId BusSubscriberFaulted = new EventId(1650, nameof(BusSubscriberFaulted));
        public static readonly EventId RawPacketConsumerFaulted = new EventId(1651, nameof(RawPacketConsumerFaulted));

        // -- 17xx host persisted stores (settings / registry) --
        public static readonly EventId SettingsLoadFailed = new EventId(1700, nameof(SettingsLoadFailed));
        public static readonly EventId SettingsSaveFailed = new EventId(1701, nameof(SettingsSaveFailed));

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
        public static readonly EventId PluginDeInit = new EventId(2106, nameof(PluginDeInit));

        // -- 22xx dispatch / ring buffer (satellite side) --
        public static readonly EventId RealPluginBound = new EventId(2200, nameof(RealPluginBound));
        // Reserved: emitted from the compat parser wrapper's null-subscription / ring-drop paths, which
        // log only through the Action<string> seam today (those libs take no logging dependency). Kept
        // allocated so the ids are stable if that seam is ever given a direct ILogger.
        public static readonly EventId RealSubscriptionNull = new EventId(2201, nameof(RealSubscriptionNull));
        public static readonly EventId SubscriberThrew = new EventId(2202, nameof(SubscriberThrew));
        public static readonly EventId PacketsDropped = new EventId(2203, nameof(PacketsDropped));

        // -- 221x bridge event forwarder (satellite side) --
        public static readonly EventId ForwarderBound = new EventId(2210, nameof(ForwarderBound));
        public static readonly EventId ForwarderDropped = new EventId(2211, nameof(ForwarderDropped));

        // -- 23xx diagnostics / self-test (satellite side) --
        public static readonly EventId SelfTest = new EventId(2300, nameof(SelfTest));
        public static readonly EventId DispatcherDiagnostics = new EventId(2301, nameof(DispatcherDiagnostics));
        public static readonly EventId Summary = new EventId(2302, nameof(Summary));
        public static readonly EventId CaptureHeartbeat = new EventId(2303, nameof(CaptureHeartbeat));

        // -- 24xx ACT facade surfaces (satellite side; routed from the legacy plugins) --
        public static readonly EventId ActInfo = new EventId(2400, nameof(ActInfo));
        public static readonly EventId ActDebug = new EventId(2401, nameof(ActDebug));
        public static readonly EventId ActException = new EventId(2402, nameof(ActException));
        public static readonly EventId ActNotification = new EventId(2403, nameof(ActNotification));
        public static readonly EventId ActCommand = new EventId(2404, nameof(ActCommand));
        public static readonly EventId ActRestartRequested = new EventId(2405, nameof(ActRestartRequested));

        // -- 30xx native parser (reserved — the native parser is not yet implemented; these ids are
        //    allocated ahead of it so the taxonomy is stable when it lands) --
        public static readonly EventId LineDropped = new EventId(3000, nameof(LineDropped));
        public static readonly EventId UnknownLineType = new EventId(3001, nameof(UnknownLineType));
        public static readonly EventId ParseAnomaly = new EventId(3002, nameof(ParseAnomaly));
    }

    // Stable category names (the ILogger<T> category is the type's full name by default; these are
    // for the hand-built loggers where a type isn't the natural category — e.g. forwarded satellite
    // records re-emitted on the host).
    public static class LogCategories
    {
        public const string Satellite = "Fct.Satellite";
        public const string Bridge = "Fct.Bridge";
    }
}
