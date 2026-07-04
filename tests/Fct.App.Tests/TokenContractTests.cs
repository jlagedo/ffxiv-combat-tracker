using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Fct.Abstractions.UI;
using Xunit;

namespace Fct.App.Tests;

/// <summary>
/// Drift guard for the plugin design-token contract. The <c>Fct.Abstractions.UI</c> constants
/// (<see cref="FctTokens"/>/<see cref="FctStyleClasses"/>) are only useful if the shell actually
/// defines the matching resource keys and style-class selectors — but the two live in different
/// projects (the contract is net10, the shell XAML is net10-windows and not referenced here), so a
/// rename on one side wouldn't otherwise fail the build. These tests read the shell's XAML source and
/// assert every advertised token/class is present, catching that drift in CI.
/// </summary>
public class TokenContractTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ffxiv-combat-tracker.slnx")))
                dir = dir.Parent;
            Assert.True(dir is not null, "could not locate repo root (ffxiv-combat-tracker.slnx)");
            return dir!.FullName;
        }
    }

    private static string ReadSource(params string[] relative)
    {
        var path = Path.Combine(new[] { RepoRoot }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"expected shell source at {path}");
        return File.ReadAllText(path);
    }

    private static string[] StringConstants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void Every_FctTokens_key_is_defined_in_App_axaml()
    {
        var app = ReadSource("src", "Fct.App", "App.axaml");
        foreach (var key in StringConstants(typeof(FctTokens)))
            Assert.True(app.Contains($"x:Key=\"{key}\""),
                $"App.axaml is missing a resource for token key '{key}' — the shell mapping drifted from FctTokens.");
    }

    [Fact]
    public void Every_FctStyleClass_is_defined_in_PluginTokens_axaml()
    {
        var styles = ReadSource("src", "Fct.App", "Styles", "PluginTokens.axaml");
        foreach (var cls in StringConstants(typeof(FctStyleClasses)))
            Assert.True(styles.Contains($".{cls}"),
                $"PluginTokens.axaml has no selector for style class '{cls}' — the shell classes drifted from FctStyleClasses.");
    }

    [Fact]
    public void Token_keys_use_the_expected_prefixes()
    {
        // Cheap intra-contract sanity: brush/font keys are PascalCase 'Fct*', classes are kebab 'fct-*'.
        Assert.All(StringConstants(typeof(FctTokens)), k => Assert.StartsWith("Fct", k));
        Assert.All(StringConstants(typeof(FctStyleClasses)), c => Assert.StartsWith("fct-", c));
    }
}
