using System;
using System.IO;
using Xunit;

namespace Fct.App.Tests.Plugins;

/// <summary>
/// Phase 5 goal, enforced: the modern host's static graph carries NO ACT-impersonation identities.
/// The compat shim + its two legacy-impersonating facades (<c>Advanced Combat Tracker</c> /
/// <c>FFXIV_ACT_Plugin.Common</c>) load from a staged <c>compat\</c> package at runtime, so they must
/// not appear in <c>Fct.App.deps.json</c>. <c>StageAppDepsForTests</c> copies the shipped exe's
/// deps.json next to this test.
/// </summary>
public class StaticGraphTests
{
    private static string AppDepsJson => Path.Combine(AppContext.BaseDirectory, "Fct.App.deps.json");

    [Theory]
    [InlineData("Advanced Combat Tracker")]
    [InlineData("FFXIV_ACT_Plugin.Common")]
    [InlineData("Fct.Compat.Shim")]
    [InlineData("Fct.Aggregation")]
    public void Fct_App_static_graph_has_no_compat_impersonation_identity(string assemblyName)
    {
        Assert.True(File.Exists(AppDepsJson), $"Fct.App.deps.json not staged at {AppDepsJson}");

        var deps = File.ReadAllText(AppDepsJson);
        Assert.DoesNotContain(assemblyName, deps, StringComparison.Ordinal);
    }
}
