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
