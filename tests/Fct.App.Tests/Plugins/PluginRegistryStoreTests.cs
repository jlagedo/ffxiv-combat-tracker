using System.IO;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Xunit;

namespace Fct.App.Tests.Plugins;

public class PluginRegistryStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "fct-reg-" + Path.GetRandomFileName() + ".json");

    [Fact]
    public void Add_remove_and_load_round_trip_through_the_file()
    {
        var path = TempFile();
        try
        {
            var store = new PluginRegistryStore(path);
            store.Load();
            store.Add(new InstalledPluginRecord("com.a", LoadKind.Native, @"C:\a", "A.Entry", "1.0") { Title = "A" });
            store.Add(new InstalledPluginRecord("com.b", LoadKind.RealLegacy, @"C:\b", null, "2.0"));

            // A fresh store reads the same two records back (source-gen serialize/deserialize).
            var reloaded = new PluginRegistryStore(path);
            var all = reloaded.Load().Plugins;
            Assert.Equal(2, all.Count);
            Assert.Equal(LoadKind.RealLegacy, reloaded.Find("com.b")!.Kind);

            reloaded.Remove("com.a");
            Assert.Null(new PluginRegistryStore(path).Load().Plugins.Find(p => p.Id == "com.a"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_corrupt_registry_is_preserved_not_silently_wiped()
    {
        var path = TempFile();
        try
        {
            // A truncated / garbage file that fails to deserialize.
            File.WriteAllText(path, "{ this is not valid json ");

            var store = new PluginRegistryStore(path);
            var loaded = store.Load();

            // The registry resets to empty (the app still starts) …
            Assert.Empty(loaded.Plugins);
            // … but the bad file is NOT the file at `path` anymore — it was renamed aside, never lost.
            Assert.False(File.Exists(path), "the corrupt file should have been renamed away from its path");
            var dir = Path.GetDirectoryName(path)!;
            var preserved = Directory.GetFiles(dir, Path.GetFileName(path) + ".corrupt-*.json");
            Assert.Single(preserved);
            Assert.Contains("this is not valid json", File.ReadAllText(preserved[0]));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            try { File.Delete(path); } catch { }
            foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + ".corrupt-*.json"))
                try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void Add_replaces_an_existing_record_for_the_same_id()
    {
        var path = TempFile();
        try
        {
            var store = new PluginRegistryStore(path);
            store.Add(new InstalledPluginRecord("com.a", LoadKind.Native, @"C:\a", "A", "1.0"));
            store.Add(new InstalledPluginRecord("com.a", LoadKind.Native, @"C:\a2", "A", "2.0"));
            Assert.Single(store.All());
            Assert.Equal("2.0", store.Find("com.a")!.Version);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Pending_delete_clears_a_freed_directory_on_process()
    {
        var path = TempFile();
        var dir = Path.Combine(Path.GetTempPath(), "fct-pending-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new PluginRegistryStore(path);
            store.MarkPendingDelete(dir);
            Assert.Single(store.Load().PendingDelete);

            store.ProcessPendingDeletes();
            Assert.False(Directory.Exists(dir));
            Assert.Empty(store.Current.PendingDelete);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            try { File.Delete(path); } catch { }
        }
    }
}
