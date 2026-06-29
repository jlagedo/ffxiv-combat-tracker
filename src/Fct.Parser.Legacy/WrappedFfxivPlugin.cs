using System;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Parser.Legacy
{
    // The object placed in ActPluginData.pluginObj in place of the real FFXIV_ACT_Plugin
    // instance. It forwards the IActPluginV1 lifecycle to the real plugin and re-exposes the
    // SDK surface OverlayPlugin reflects against (docs/DATA-FLOW.md §4.1), with one change:
    // DataSubscription is our ring-buffer dispatcher rather than the plugin's BeginInvoke one.
    //
    //   - DataSubscription : our RingBufferDataSubscription (funnels the real plugin's events).
    //   - DataRepository   : the real plugin's repository, unchanged (resource tables,
    //                        combatant list, region — nothing reimplemented).
    //   - _iocContainer    : the real plugin's IOC container, so OverlayPlugin's
    //                        GetFFXIVACTPluginIOCService(...ILogOutput) resolves unchanged.
    //
    // The property names "DataSubscription"/"DataRepository" and the field name "_iocContainer"
    // are load-bearing: OverlayPlugin looks them up by literal string via reflection.
    public sealed class WrappedFfxivPlugin : IActPluginV1, IActPluginAlias
    {
        private readonly IActPluginV1 _real;
        private readonly RingBufferDataSubscription _subscription;
        private readonly Action<string> _log;

        // Reflected off the real plugin after InitPlugin. _iocContainer's NAME is the contract.
        private object _iocContainer;
        private IDataRepository _dataRepository;

        public WrappedFfxivPlugin(IActPluginV1 real, int ringCapacity = 4096, Action<string> log = null)
        {
            _real = real ?? throw new ArgumentNullException(nameof(real));
            _log = log ?? (_ => { });
            _subscription = new RingBufferDataSubscription(ringCapacity, _log);
        }

        // Reflected by OverlayPlugin's FFXIVRepository — names must match exactly.
        public IDataSubscription DataSubscription => _subscription;
        public IDataRepository DataRepository => _dataRepository;

        // Lets the facade's PluginGetSelfData resolve `real -> our ActPluginData` (the real
        // plugin looks itself up by `this` during InitPlugin and at runtime, e.g. to find its
        // plugin folder for the deucalion distribution and log file).
        public IActPluginV1 Inner => _real;

        // The raw-packet inject seam (tests + future bridge). Not part of the reflected surface.
        public IRawPacketSource RawPackets => _subscription;

        // Diagnostics: handler counts on the ring. ProcessChanged>0 once OverlayPlugin binds;
        // NetworkReceived stays 0 until a live game appears (OverlayPlugin gates packet capture).
        public int NetworkReceivedSubscriberCount => _subscription.NetworkReceivedSubscriberCount;
        public int ProcessChangedSubscriberCount => _subscription.ProcessChangedSubscriberCount;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            // Real plugin populates the tab/status and sets DataSubscription/DataRepository/_iocContainer.
            _real.InitPlugin(pluginScreenSpace, pluginStatusText);
            BindReal();
        }

        public void DeInitPlugin()
        {
            try { _real.DeInitPlugin(); }
            finally { _subscription.Dispose(); }
        }

        private void BindReal()
        {
            var t = _real.GetType();

            // Cast to IDataSubscription/IDataRepository here is the type-identity canary: if our
            // FFXIV_ACT_Plugin.Common did not unify onto the real loaded copy, these throw — and
            // OverlayPlugin's own casts would fail the same way. See FacadeHost's Common resolver.
            var realSub = (IDataSubscription)t.GetProperty("DataSubscription")?.GetValue(_real);
            _dataRepository = (IDataRepository)t.GetProperty("DataRepository")?.GetValue(_real);
            _iocContainer = t.GetField("_iocContainer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(_real);

            if (realSub == null)
            {
                _log("[Wrap] real DataSubscription was null — OverlayPlugin will receive no events.");
                return;
            }

            _subscription.AttachUpstream(realSub);
            _log($"[Wrap] bound real plugin: repo={_dataRepository?.GetType().FullName ?? "(null)"} " +
                 $"ioc={(_iocContainer != null ? "ok" : "MISSING")} " +
                 $"common={typeof(IDataSubscription).Assembly.FullName}");
        }
    }
}
