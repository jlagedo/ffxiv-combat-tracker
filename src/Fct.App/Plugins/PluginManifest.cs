using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fct.App.Plugins;

/// <summary>
/// A plugin's <c>plugin.json</c>, read <b>without loading the assembly</b> — the manifest gate that
/// replaces legacy discovery-by-reflection. <see cref="Assembly"/> is the entry DLL (relative to the
/// plugin directory); <see cref="Entry"/> is the full name of its <c>IPlugin</c> type.
/// </summary>
internal sealed record PluginManifest(
    string Id,
    string Version,
    string Contract,
    string Assembly,
    string Entry,
    IReadOnlyList<string> Capabilities)
{
    public bool HasCapability(string capability)
        => Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read + validate a manifest file. Returns false (with <paramref name="error"/>) on any problem.</summary>
    public static bool TryLoad(string path, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<Dto>(json, Options);
            if (dto is null) { error = "manifest deserialized to null"; return false; }

            if (string.IsNullOrWhiteSpace(dto.Id)) { error = "missing 'id'"; return false; }
            if (string.IsNullOrWhiteSpace(dto.Version)) { error = "missing 'version'"; return false; }
            if (string.IsNullOrWhiteSpace(dto.Contract)) { error = "missing 'contract'"; return false; }
            if (string.IsNullOrWhiteSpace(dto.Assembly)) { error = "missing 'assembly'"; return false; }
            if (string.IsNullOrWhiteSpace(dto.Entry)) { error = "missing 'entry'"; return false; }

            manifest = new PluginManifest(
                dto.Id!, dto.Version!, dto.Contract!, dto.Assembly!, dto.Entry!,
                dto.Capabilities ?? Array.Empty<string>());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Dto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? Contract { get; set; }
        public string? Assembly { get; set; }
        public string? Entry { get; set; }
        public string[]? Capabilities { get; set; }
    }
}
