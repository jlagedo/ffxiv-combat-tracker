using System;
using System.IO;
using Fct.Host.Plugins;
using Xunit;

namespace Fct.App.Tests.Plugins;

public class PluginClassifierTests
{
    private static string SampleDir => Path.Combine(AppContext.BaseDirectory, "plugins", "Fct.SamplePlugin");
    private static string SampleDll => Path.Combine(SampleDir, "Fct.SamplePlugin.dll");

    [Fact]
    public void Manifest_is_authoritative_for_a_native_plugin()
    {
        Assert.True(PluginManifest.TryLoad(Path.Combine(SampleDir, "plugin.json"), out var manifest, out _));
        var c = new PluginClassifier().Classify(SampleDir, manifest);
        Assert.Equal(LoadKind.Native, c.Kind);
        Assert.Equal("com.fct.sample", c.Id);
    }

    [Fact]
    public void Detects_a_native_plugin_from_metadata_when_no_manifest()
    {
        Assert.True(File.Exists(SampleDll), $"sample plugin not staged at {SampleDll}");
        var dir = CopyDllsOnly();
        try
        {
            var c = new PluginClassifier().Classify(dir, manifest: null);
            Assert.Equal(LoadKind.Native, c.Kind);
            Assert.Equal("fct.sampleplugin", c.Id);                       // synthesized from the assembly name
            Assert.Equal("Fct.SamplePlugin.SamplePlugin", c.EntryTypeName);
            Assert.Equal("Fct.SamplePlugin.dll", c.AssemblyFile);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Copy the sample's assemblies (no plugin.json) so classification must fall back to metadata.
    private static string CopyDllsOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fct-classify-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var dll in Directory.GetFiles(SampleDir, "*.dll"))
            File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)));
        return dir;
    }
}
