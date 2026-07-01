using System.IO;
using Fct.App.Plugins;
using Xunit;

namespace Fct.App.Tests.Plugins;

public class PluginManifestTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "fct-manifest-" + Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Valid_manifest_parses()
    {
        var path = WriteTemp("""
        {
          "id": "com.test.plugin",
          "version": "2.1.0",
          "contract": "1.0",
          "assembly": "Test.dll",
          "entry": "Test.Entry",
          "capabilities": [ "raw", "ui" ]
        }
        """);
        try
        {
            Assert.True(PluginManifest.TryLoad(path, out var m, out var err));
            Assert.Null(err);
            Assert.Equal("com.test.plugin", m!.Id);
            Assert.Equal("Test.Entry", m.Entry);
            Assert.True(m.HasCapability("RAW"));   // case-insensitive
            Assert.False(m.HasCapability("net"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_required_field_is_rejected()
    {
        var path = WriteTemp("""{ "id": "x", "version": "1.0", "contract": "1.0" }""");
        try
        {
            Assert.False(PluginManifest.TryLoad(path, out var m, out var err));
            Assert.Null(m);
            Assert.Contains("assembly", err);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.4", true)]   // additive minor bumps stay compatible
    [InlineData("2.0", false)]  // major mismatch
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void Contract_gate_matches_on_major(string contract, bool accepted)
        => Assert.Equal(accepted, HostContract.Accepts(contract));
}
