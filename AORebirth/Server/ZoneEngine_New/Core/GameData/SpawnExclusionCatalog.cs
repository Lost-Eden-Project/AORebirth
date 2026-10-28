namespace ZoneEngine_New.Core.GameData;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

using AORebirth.Core.GameData;

using ZoneEngine_New.Core.Logging;

/// <summary>
/// SpawnExclusions.json: hash spawn points that are not loaded. An entry names a spawn hash (Spawns.json hashText,
/// compared exactly) and optionally the playfield it applies to; without a playfield it applies on every playfield.
/// Note says why, for whoever edits the file. A missing file excludes nothing.
/// <code>
/// { "Exclusions": [ { "Hash": "3846", "Playfield": 655, "Note": "No template; why" } ] }
/// </code>
/// </summary>
public sealed class SpawnExclusionCatalog
{
    public static SpawnExclusionCatalog Empty { get; } = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    readonly HashSet<string> _everywhere = new(StringComparer.Ordinal);
    readonly HashSet<(string Hash, int Playfield)> _onPlayfield = new();

    public List<SpawnExclusion> Exclusions { get; set; } = new();

    public int Count => _everywhere.Count + _onPlayfield.Count;

    /// <summary>Whether spawn points of <paramref name="hash"/> are skipped on playfield resource <paramref name="playfieldId"/>.</summary>
    public bool Excludes(string? hash, int playfieldId)
        => !string.IsNullOrEmpty(hash) && (_everywhere.Contains(hash) || _onPlayfield.Contains((hash, playfieldId)));

    public static SpawnExclusionCatalog Load(string root, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string path = Path.Combine(root, GameDataPaths.SpawnExclusionsFileName);
        if (!File.Exists(path))
            return Empty;

        try
        {
            SpawnExclusionCatalog catalog = Parse(File.ReadAllText(path));
            logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData spawn exclusions={0} from {1}", catalog.Count, path));
            return catalog;
        }
        catch (Exception exception)
        {
            logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                "Failed to load SpawnExclusions.json from {0}; no spawn points are excluded", path));
            return Empty;
        }
    }

    public static SpawnExclusionCatalog Parse(string json)
    {
        SpawnExclusionCatalog catalog = JsonSerializer.Deserialize<SpawnExclusionCatalog>(json, JsonOptions) ?? new SpawnExclusionCatalog();
        foreach (SpawnExclusion exclusion in catalog.Exclusions)
        {
            string hash = exclusion?.Hash?.Trim() ?? string.Empty;
            if (hash.Length == 0)
                continue;
            if (exclusion!.Playfield is int playfield and > 0)
                catalog._onPlayfield.Add((hash, playfield));
            else
                catalog._everywhere.Add(hash);
        }

        return catalog;
    }
}

public sealed class SpawnExclusion
{
    public string Hash { get; set; } = string.Empty;

    /// <summary>The playfield resource id; null or 0 excludes the hash everywhere.</summary>
    public int? Playfield { get; set; }

    public string Note { get; set; } = string.Empty;
}
