using System;
using System.IO;
using System.Text.Json;

namespace Fct.Host.Hosting;

/// <summary>User preferences for the shell itself (not plugin settings). Persisted as JSON next to
/// the logs so it survives restarts.</summary>
public sealed class UiSettings
{
    /// <summary>Start the .NET Framework 4.8 satellite automatically when the app opens.</summary>
    public bool LaunchSatelliteOnStartup { get; set; } = true;
}

/// <summary>Loads and saves <see cref="UiSettings"/> to
/// <c>%LOCALAPPDATA%\FFXIVCombatTracker\ui-settings.json</c>. Best-effort: a read/write failure
/// falls back to defaults rather than blocking the UI.</summary>
public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FFXIVCombatTracker", "ui-settings.json");

    public UiSettings Current { get; private set; } = new();

    public UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch { Current = new UiSettings(); }
        return Current;
    }

    public void Save(UiSettings settings)
    {
        Current = settings;
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch { /* best-effort: preferences aren't worth crashing over */ }
    }
}
