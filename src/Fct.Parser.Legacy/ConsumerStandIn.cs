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

        public ConsumerStandIn(Action<string> log)
        {
            _log = log ?? (_ => { });
            _plugin = new SyntheticFfxivPlugin(_repo, _sub);
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

        // The _iocContainer NAME is load-bearing (OverlayPlugin's GetFFXIVACTPluginIOCService reflects it
        // to resolve an ILogOutput). Non-null placeholder for P5; the ILogOutput write-back lands in P6.
#pragma warning disable CS0414
        private readonly object _iocContainer = new object();
#pragma warning restore CS0414

        public SyntheticFfxivPlugin(IDataRepository repo, IDataSubscription sub)
        {
            DataRepository = repo;
            DataSubscription = sub;
        }

        // No wrapped inner; PluginGetSelfData resolves `this`.
        public IActPluginV1 Inner => this;
        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText) { }
        public void DeInitPlugin() { }
    }
}
