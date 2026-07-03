using System;
using System.IO;
using System.Threading.Tasks;
using Fct.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>
/// Real disk-backed <see cref="IPluginStorage"/> — the production form of the reference
/// <c>FakeStorage</c>. Each plugin gets a private directory under
/// <c>%LOCALAPPDATA%\FFXIVCombatTracker\plugins\&lt;id&gt;</c>; settings serialize to
/// <c>&lt;name&gt;.json</c> via <see cref="System.Text.Json"/>. Replaces
/// <c>PluginGetSelfData</c>/<c>AppDataFolder</c>.
/// </summary>
internal sealed class PluginStorage : IPluginStorage
{
    private readonly string _root;

    public PluginStorage(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin id required", nameof(pluginId));
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCombatTracker", "plugins", Sanitize(pluginId));
    }

    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(_root);
            return _root;
        }
    }

    public async Task<T?> LoadSettingsAsync<T>(string name = "settings") where T : class
    {
        var path = SettingsPath(name);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(stream).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync<T>(T value, string name = "settings") where T : class
    {
        Directory.CreateDirectory(_root);
        var path = SettingsPath(name);
        await using var stream = File.Create(path);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, value).ConfigureAwait(false);
    }

    private string SettingsPath(string name) => Path.Combine(_root, Sanitize(name) + ".json");

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}
