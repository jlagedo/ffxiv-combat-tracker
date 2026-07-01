using System;
using System.IO;

namespace Fct.App.Hosting;

/// <summary>
/// Locations for <b>user-installed</b> plugin binaries. Deliberately separate from
/// <see cref="PluginStorage"/>'s per-plugin <c>plugins\&lt;id&gt;</c> <i>settings</i> directory and from
/// the build-staged <c>plugins\</c> folder next to the host (which carries the dev samples). Follows
/// the <c>%LOCALAPPDATA%\FFXIVCombatTracker</c> convention used by the logs and settings stores.
/// </summary>
internal sealed class PluginInstallPaths
{
    private readonly string _base;

    /// <param name="baseOverride">Test seam: overrides the app data base directory.</param>
    public PluginInstallPaths(string? baseOverride = null)
        => _base = baseOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCombatTracker");

    private string Base => _base;

    /// <summary>Root under which each installed plugin gets its own <c>&lt;id&gt;\</c> folder.</summary>
    public string InstalledRoot => Path.Combine(Base, "installed-plugins");

    /// <summary>Scratch root for extracting zips before the id is known.</summary>
    public string StagingRoot => Path.Combine(Base, "installed-plugins", ".staging");

    /// <summary>The permanent install directory for a given plugin id.</summary>
    public string DirFor(string id) => Path.Combine(InstalledRoot, Sanitize(id));

    /// <summary>A fresh, empty staging directory for one extraction (caller deletes it when done).</summary>
    public string NewStagingDir(string token)
    {
        var dir = Path.Combine(StagingRoot, Sanitize(token));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void EnsureRoots() => Directory.CreateDirectory(InstalledRoot);

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}
