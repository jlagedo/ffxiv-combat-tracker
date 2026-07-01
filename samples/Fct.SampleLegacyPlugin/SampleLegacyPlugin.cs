using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace Fct.SampleLegacyPlugin;

/// <summary>
/// A minimal ACT-era plugin recompiled against the compat shim. Implements the exact
/// <see cref="IActPluginV1"/> contract a legacy plugin does; the static counters let the load test
/// observe that InitPlugin/DeInitPlugin ran across the plugin ALC boundary.
/// </summary>
public sealed class SampleLegacyPlugin : IActPluginV1
{
    /// <summary>Times <see cref="InitPlugin"/> has been called (across the ALC, read by the test).</summary>
    public static int InitCount;

    /// <summary>Times <see cref="DeInitPlugin"/> has been called.</summary>
    public static int DeInitCount;

    /// <summary>The last status text the plugin wrote.</summary>
    public static string LastStatus = string.Empty;

    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        InitCount++;
        pluginStatusText.Text = "Sample legacy plugin online";
        LastStatus = pluginStatusText.Text;

        // Prove the shared ACT hub is wired to the modern host.
        ActGlobals.oFormActMain.WriteInfoLog("SampleLegacyPlugin initialized");
    }

    public void DeInitPlugin()
    {
        DeInitCount++;
        LastStatus = "offline";
    }
}
