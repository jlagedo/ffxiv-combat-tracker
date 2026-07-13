using System;
using System.IO;
using System.Text.Json;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>User preferences for the shell itself (not plugin settings). Persisted as JSON next to
/// the logs so it survives restarts.</summary>
public sealed class UiSettings
{
    /// <summary>Start the .NET Framework 4.8 satellite automatically when the app opens.</summary>
    public bool LaunchSatelliteOnStartup { get; set; } = true;
}

/// <summary>Loads and saves <see cref="UiSettings"/> to <c>&lt;AppData.Root&gt;\ui-settings.json</c>.
/// Best-effort: a read/write failure falls back to defaults rather than blocking the UI.</summary>
public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppData.Root, "ui-settings.json");

    private readonly ILogger _log;

    public UiSettingsStore(ILogger<UiSettingsStore>? log = null)
        => _log = log ?? (ILogger)NullLogger<UiSettingsStore>.Instance;

    public UiSettings Current { get; private set; } = new();

    public UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (Exception ex)
        {
            // Defaulting shell prefs is low-consequence, but preserve the bad file (don't overwrite it)
            // and log so a persistently unreadable settings file isn't a silent mystery.
            var quarantined = Quarantine(FilePath);
            _log.LogWarning(LogEvents.SettingsLoadFailed, ex,
                "Shell settings '{Path}' are unreadable; preserved as '{Quarantined}' and reset to defaults",
                FilePath, quarantined ?? "(preserve failed)");
            Current = new UiSettings();
        }
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
        catch (Exception ex)
        {
            // Preferences aren't worth crashing over, but a silent drop means a toggle never persists and
            // the user never learns why.
            _log.LogWarning(LogEvents.SettingsSaveFailed, ex, "Failed to persist shell settings to '{Path}'", FilePath);
        }
    }

    // Rename a corrupt settings file aside so it is never silently lost. Best-effort — returns null if
    // even the rename failed.
    private static string? Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var quarantined = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json";
            File.Move(path, quarantined);
            return quarantined;
        }
        catch { return null; }
    }
}
