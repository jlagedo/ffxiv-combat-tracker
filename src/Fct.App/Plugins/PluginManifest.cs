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
/// plugin directory). A native plugin names its <c>IPlugin</c> type in <see cref="Entry"/>; a legacy
/// plugin recompiled against the compat shim instead names its <c>IActPluginV1</c> type in
/// <see cref="LegacyEntry"/> (the shim's <c>LegacyPluginHost</c> is the actual <c>IPlugin</c>).
/// Exactly one of the two is set.
/// </summary>
internal sealed record PluginManifest(
    string Id,
    string Version,
    string Contract,
    string Assembly,
    string? Entry,
    IReadOnlyList<string> Capabilities,
    string? LegacyEntry = null)
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

            bool hasEntry = !string.IsNullOrWhiteSpace(dto.Entry);
            bool hasLegacy = !string.IsNullOrWhiteSpace(dto.LegacyEntry);
            if (!hasEntry && !hasLegacy) { error = "missing 'entry' or 'legacyEntry'"; return false; }
            if (hasEntry && hasLegacy) { error = "'entry' and 'legacyEntry' are mutually exclusive"; return false; }

            manifest = new PluginManifest(
                dto.Id!, dto.Version!, dto.Contract!, dto.Assembly!,
                hasEntry ? dto.Entry : null,
                dto.Capabilities ?? Array.Empty<string>(),
                hasLegacy ? dto.LegacyEntry : null);
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
        public string? LegacyEntry { get; set; }
        public string[]? Capabilities { get; set; }
    }
}
