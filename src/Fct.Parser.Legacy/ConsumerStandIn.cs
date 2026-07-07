using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Parser.Legacy
{
    // ISOLATION-PLAN P5 — the synthetic parser stand-in (net48). Exposes the SDK surface a consumer
    // plugin discovers + binds, all backed by host-routed frames with NO parser. The net48 mirror of the
    // in-process net10 Fct.Compat.Shim (SyntheticFfxivPlugin/DataSubscriptionAdapter/DataRepository),
    // against the real embedded FFXIV_ACT_Plugin.Common. All SDK-typed members are confined here, behind
    // IConsumerStandIn, so the plugin-free consumer path never JITs them unless --stand-in activates it.
    internal sealed class ConsumerStandIn : IConsumerStandIn
    {
        private readonly Action<string> _log;
        private readonly ConsumerDataSubscription _sub = new ConsumerDataSubscription();
        private readonly ConsumerDataRepository _repo = new ConsumerDataRepository();
        private readonly SyntheticFfxivPlugin _plugin;

        public ConsumerStandIn(Action<string> log, Action<int, string> writeBack)
        {
            _log = log ?? (_ => { });
            // The _iocContainer Hojoring casts to Microsoft.MinIoC.Container must be a REAL instance of that
            // type (compiled into FFXIV_ACT_Plugin.dll). Build it reflectively with our write-back ILogOutput
            // + a non-null ILogFormat registered; fall back to the synthetic container when the parser (hence
            // the MinIoC/Logfile types) isn't loaded — OverlayPlugin's GetService path still works there and
            // Hojoring (which needs the real cast) is plugin-gated anyway.
            object ioc = MinIocStandIn.TryBuildRealContainer(writeBack, m => _log(m));
            if (ioc == null)
            {
                ioc = new StandInIocContainer(new ConsumerLogOutput(writeBack));
                _log("[StandIn] real Microsoft.MinIoC.Container unavailable — using the synthetic container");
            }
            else
            {
                _log("[StandIn] built the real Microsoft.MinIoC.Container from the parser assembly");
            }
            _plugin = new SyntheticFfxivPlugin(_repo, _sub, ioc);
        }

        public void Register()
        {
            const string title = "FFXIV_ACT_Plugin";
            var act = ActGlobals.oFormActMain;
            var data = new ActPluginData
            {
                pluginFile = new FileInfo(InstalledParserPath()),
                cbEnabled = new CheckBox { Checked = true },
                lblPluginTitle = new Label { Text = title },
                lblPluginStatus = new Label { Text = "FFXIV_ACT_Plugin Started." },
                tpPluginSpace = new TabPage(title),
                pPluginInfo = new Panel(),
                pluginObj = _plugin,
                pluginVersion = "3.0.0.0",
            };
            act.ActPlugins.Add(data);
            _log("[StandIn] registered synthetic FFXIV_ACT_Plugin in ActPlugins " +
                 $"(common={typeof(IDataSubscription).Assembly.FullName})");
        }

        // Repository mirror first (so a consumer re-poll after the event sees fresh state), then re-raise.
        public void Fold(GameEvent evt)
        {
            _repo.Apply(evt);
            _sub.Raise(evt);
            // PIPELINE-COMPLETENESS-PLAN P3.6/G6: the forwarded region also reaches Machina's own
            // OpcodeManager (best-effort, non-blocking — KR/CN only, gates nothing). Re-attempted on
            // every SessionStateChanged (bind-time + relog/patch re-emits, P3.3) since Machina.FFXIV may
            // not be loaded in this satellite's AppDomain yet the first time this fires.
            if (evt is SessionStateChanged state)
                MachinaRegionBridge.TrySetRegion(state.Region, _log);
        }

        public StandInVerification SelfVerify()
        {
            var v = new StandInVerification();
            var act = ActGlobals.oFormActMain;
            var data = act?.ActPlugins?.FirstOrDefault(p =>
                p.cbEnabled != null && p.cbEnabled.Checked && p.pluginObj != null &&
                p.lblPluginTitle != null &&
                p.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
            if (data == null) return v;

            v.Found = true;
            v.Title = data.lblPluginTitle.Text;
            v.Status = data.lblPluginStatus?.Text ?? "";

            // The exact reflection OverlayPlugin's FFXIVRepository performs: the `as` casts to the real
            // SDK interfaces are the Common type-identity canary — they succeed only if the stand-in's
            // members and the discovering code bound the single loaded FFXIV_ACT_Plugin.Common copy.
            var obj = data.pluginObj;
            var sub = obj.GetType().GetProperty("DataSubscription")?.GetValue(obj) as IDataSubscription;
            var repo = obj.GetType().GetProperty("DataRepository")?.GetValue(obj) as IDataRepository;
            v.SdkTypesBound = sub != null && repo != null;
            if (repo != null)
            {
                v.Combatants = repo.GetCombatantList().Count;
                v.GameVersion = repo.GetGameVersion();
                // PIPELINE-COMPLETENESS-PLAN P1.5/G4: read (never write) the remaining four env scalars —
                // same "in-satellite discovery gate" observability precedent as GameVersion above.
                v.Language = (int)repo.GetSelectedLanguageID();
                v.Region = repo.GetGameRegion();
                v.ServerTimestampTicks = repo.GetServerTimestamp().Ticks;
                v.IsChatLogAvailable = repo.IsChatLogAvailable();
            }
            v.LogLines = _sub.LogLinesRaised;
            v.Packets = _sub.NetworkRaised;
            // Reproduce Hojoring's XIVPluginHelper.Attach() container gate: _iocContainer must be the real
            // Microsoft.MinIoC.Container (asm FFXIV_ACT_Plugin) and resolve ILogFormat + ILogOutput non-null.
            v.RealIocContainer = MinIocStandIn.VerifyHojoringGate(
                obj.GetType().GetField("_iocContainer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj));
            return v;
        }

        public void Dispose() => _sub.Dispose();

        private static string InstalledParserPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Advanced Combat Tracker", "Plugins", "FFXIV_ACT_Plugin.dll");
    }

    // Stands in for the real FFXIV_ACT_Plugin object in ActPlugins. Discovery-by-reflection matches its
    // title then reflects the public DataRepository/DataSubscription properties and the non-public
    // _iocContainer field off it (the names are the contract). Its lifecycle is a no-op — it is the seam.
    internal sealed class SyntheticFfxivPlugin : IActPluginV1, IActPluginAlias
    {
        public IDataRepository DataRepository { get; }
        public IDataSubscription DataSubscription { get; }

        // The _iocContainer NAME is load-bearing: OverlayPlugin's GetFFXIVACTPluginIOCService reflects this
        // field, then GetService(Type) off it to resolve an ILogOutput by type name, then invokes its
        // WriteLine (P6 write-back). Hojoring additionally casts it to Microsoft.MinIoC.Container. Its runtime
        // type is therefore the parser's real Container (or the synthetic fallback); typed object so this
        // assembly takes no compile-time dependency on the parser-embedded MinIoC type. Reflection-only, so
        // unused-field is expected.
#pragma warning disable CS0414
        private readonly object _iocContainer;
#pragma warning restore CS0414

        public SyntheticFfxivPlugin(IDataRepository repo, IDataSubscription sub, object iocContainer)
        {
            DataRepository = repo;
            DataSubscription = sub;
            _iocContainer = iocContainer;
        }

        // No wrapped inner; PluginGetSelfData resolves `this`.
        public IActPluginV1 Inner => this;
        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText) { }
        public void DeInitPlugin() { }
    }

    // The synthetic IOC container behind SyntheticFfxivPlugin._iocContainer. OverlayPlugin reflects
    // GetService(Type) off it and resolves an ILogOutput by type NAME (FFXIV_ACT_Plugin.Logfile.ILogOutput).
    // Signatures name no SDK type, so the plugin-free path never JITs SDK metadata through here.
    internal sealed class StandInIocContainer
    {
        private readonly ConsumerLogOutput _logOutput;
        public StandInIocContainer(ConsumerLogOutput logOutput) => _logOutput = logOutput;

        // OverlayPlugin: getServiceMethod.Invoke(iocContainer, new object[] { parentAssembly.GetType(type) }).
        public object GetService(Type serviceType)
            => serviceType?.FullName == "FFXIV_ACT_Plugin.Logfile.ILogOutput" ? (object)_logOutput : null;

        // Autofac-style alias some consumers (Hojoring) resolve through instead of GetService.
        public object Resolve(Type serviceType) => GetService(serviceType);
    }

    // The ILogOutput write-back target OverlayPlugin reflects: logOutput.GetType().GetMethod("WriteLine")
    // invoked with ((int)ID, timestamp, line). Matches ACT's ILogOutput.WriteLine(messageType, ServerDate,
    // line) by reflection shape — no SDK type referenced. Marshals the custom line up the bridge; the host
    // re-emits it as a bus RawLogLine fanned back to every rawlog subscriber (including this origin). The
    // server date is dropped: the host stamps its own clock on re-emit.
    internal sealed class ConsumerLogOutput
    {
        private readonly Action<int, string> _writeBack;
        public ConsumerLogOutput(Action<int, string> writeBack) => _writeBack = writeBack ?? ((_, __) => { });
        public void WriteLine(int messageType, DateTime serverDate, string line) => _writeBack(messageType, line ?? "");
    }

    // Builds the parser's REAL Microsoft.MinIoC.Container — a type compiled directly into
    // FFXIV_ACT_Plugin.dll (not a separate assembly), which is why Hojoring's `_iocContainer as
    // Microsoft.MinIoC.Container` cast demands an instance of exactly that type. Constructed and populated
    // entirely by reflection so this project takes NO compile-time dependency on the parser-embedded
    // MinIoC / FFXIV_ACT_Plugin.Logfile types. The registered ILogOutput/ILogFormat are RealProxy instances
    // over the runtime interfaces (Hojoring only null-checks them; OverlayPlugin reflects WriteLine off the
    // ILogOutput — the proxy answers GetType() with the interface type so that reflection resolves).
    internal static class MinIocStandIn
    {
        public static object TryBuildRealContainer(Action<int, string> writeBack, Action<string> log)
        {
            try
            {
                var parserAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "FFXIV_ACT_Plugin");
                var containerT = parserAsm?.GetType("Microsoft.MinIoC.Container");
                var extT = parserAsm?.GetType("Microsoft.MinIoC.ContainerExtensions");
                if (containerT == null || extT == null) return null;

                var logfileAsm = FindOrLoad("FFXIV_ACT_Plugin.Logfile");
                var ilogOut = logfileAsm?.GetType("FFXIV_ACT_Plugin.Logfile.ILogOutput");
                var ilogFmt = logfileAsm?.GetType("FFXIV_ACT_Plugin.Logfile.ILogFormat");
                if (ilogOut == null || ilogFmt == null) return null;

                // Register<T>(this Container, Func<T> factory) — the only factory overload, generic on the
                // interface. Build a Func<T> (T = the runtime interface) via a generic helper, then invoke it.
                var register = extT.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                    m.Name == "Register" && m.IsGenericMethod && m.GetGenericArguments().Length == 1 &&
                    m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.Name.StartsWith("Func"));
                if (register == null) return null;
                var mkFactory = typeof(MinIocStandIn).GetMethod(nameof(MakeFactory), BindingFlags.NonPublic | BindingFlags.Static);

                var container = Activator.CreateInstance(containerT);
                Register(register, mkFactory, container, ilogOut, new RealProxyLogService(ilogOut, writeBack).GetTransparentProxy());
                Register(register, mkFactory, container, ilogFmt, new RealProxyLogService(ilogFmt, null).GetTransparentProxy());
                return container;
            }
            catch (Exception ex)
            {
                log?.Invoke("[StandIn] real MinIoC container build failed: " + ex.Message);
                return null;
            }
        }

        private static void Register(MethodInfo register, MethodInfo mkFactory, object container, Type iface, object instance)
        {
            var func = mkFactory.MakeGenericMethod(iface).Invoke(null, new[] { instance });
            register.MakeGenericMethod(iface).Invoke(null, new[] { container, func });
        }

        // Reproduce Hojoring's attach gate against a candidate _iocContainer: the runtime type is exactly
        // Microsoft.MinIoC.Container (asm FFXIV_ACT_Plugin) AND Resolve<ILogFormat>()/Resolve<ILogOutput>()
        // are both non-null. Used by the stand-in self-verify (the P9a plugin-gated container gate).
        public static bool VerifyHojoringGate(object ioc)
        {
            try
            {
                if (ioc == null) return false;
                var containerT = ioc.GetType();
                if (containerT.FullName != "Microsoft.MinIoC.Container" ||
                    containerT.Assembly.GetName().Name != "FFXIV_ACT_Plugin") return false;
                var extT = containerT.Assembly.GetType("Microsoft.MinIoC.ContainerExtensions");
                var logfileAsm = FindOrLoad("FFXIV_ACT_Plugin.Logfile");
                var ilogOut = logfileAsm?.GetType("FFXIV_ACT_Plugin.Logfile.ILogOutput");
                var ilogFmt = logfileAsm?.GetType("FFXIV_ACT_Plugin.Logfile.ILogFormat");
                if (extT == null || ilogOut == null || ilogFmt == null) return false;
                var resolve = extT.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "Resolve" && m.IsGenericMethod && m.GetParameters().Length == 1);
                return resolve.MakeGenericMethod(ilogOut).Invoke(null, new[] { ioc }) != null
                    && resolve.MakeGenericMethod(ilogFmt).Invoke(null, new[] { ioc }) != null;
            }
            catch { return false; }
        }

        // Func<T> returning the given instance (T = a runtime interface type the proxy implements).
        private static Func<T> MakeFactory<T>(object instance) => () => (T)instance;

        private static Assembly FindOrLoad(string name)
        {
            var a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == name);
            if (a != null) return a;
            try { return Assembly.Load(name); } catch { return null; }
        }
    }

    // A RealProxy over a runtime interface (net48-native — no compile-time reference to the interface, no
    // DispatchProxy package). WriteLine routes to the write-back (OverlayPlugin's custom-line seam); GetType
    // returns the interface type so `logOutput.GetType().GetMethod("WriteLine")` resolves; everything else
    // no-ops (Hojoring only null-checks ILogFormat/ILogOutput — it invokes no members on them).
    internal sealed class RealProxyLogService : System.Runtime.Remoting.Proxies.RealProxy
    {
        private readonly Type _iface;
        private readonly Action<int, string> _writeBack;   // ILogOutput.WriteLine target; null for ILogFormat
        public RealProxyLogService(Type interfaceType, Action<int, string> writeBack) : base(interfaceType)
        { _iface = interfaceType; _writeBack = writeBack; }

        public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage msg)
        {
            var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)msg;
            object ret = null;
            if (call.MethodName == "GetType") ret = _iface;
            else if (call.MethodName == "WriteLine" && _writeBack != null && call.ArgCount >= 3)
                _writeBack(Convert.ToInt32(call.Args[0]), call.Args[2] as string ?? "");
            return new System.Runtime.Remoting.Messaging.ReturnMessage(ret, null, 0, call.LogicalCallContext, call);
        }
    }
}
