using System;
using System.IO;
using System.Linq;
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
            _plugin = new SyntheticFfxivPlugin(_repo, _sub, writeBack);
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
            if (repo != null) v.Combatants = repo.GetCombatantList().Count;
            v.LogLines = _sub.LogLinesRaised;
            v.Packets = _sub.NetworkRaised;
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
        // WriteLine (P6 write-back). Assigned + reached only by reflection, so unused-field is expected.
#pragma warning disable CS0414
        private readonly StandInIocContainer _iocContainer;
#pragma warning restore CS0414

        public SyntheticFfxivPlugin(IDataRepository repo, IDataSubscription sub, Action<int, string> writeBack)
        {
            DataRepository = repo;
            DataSubscription = sub;
            _iocContainer = new StandInIocContainer(new ConsumerLogOutput(writeBack));
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
}
