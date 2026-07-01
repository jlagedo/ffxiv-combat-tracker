using System.Windows.Forms;
using Advanced_Combat_Tracker;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Compat.Shim;

/// <summary>
/// Stands in for the real FFXIV_ACT_Plugin object in <c>ActGlobals.oFormActMain.ActPlugins</c>.
/// OverlayPlugin and Hojoring discover the FFXIV plugin by scanning that roster (matching its title /
/// file name) and then reflecting the public <c>DataRepository</c>/<c>DataSubscription</c> properties
/// off its <c>pluginObj</c>. This exposes exactly those two so a recompiled consumer binds to the
/// shim's projected SDK surface. Its own lifecycle is a no-op — it hosts no plugin, it is the seam.
/// </summary>
public sealed class SyntheticFfxivPlugin : IActPluginV1
{
    public SyntheticFfxivPlugin(IDataRepository dataRepository, IDataSubscription dataSubscription)
    {
        DataRepository = dataRepository;
        DataSubscription = dataSubscription;
    }

    /// <summary>The projected SDK pull-state surface (reflected by OverlayPlugin/Hojoring).</summary>
    public IDataRepository DataRepository { get; }

    /// <summary>The projected SDK typed-event surface (reflected by OverlayPlugin/Hojoring).</summary>
    public IDataSubscription DataSubscription { get; }

    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText) { }

    public void DeInitPlugin() { }
}
