namespace ZoneEngine_New.Core.GameData;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

using AORebirth.Core.GameData;

using ZoneEngine_New.Core.Logging;

/// <summary>
/// GoverningForms.json: the organization governing forms, keyed by the organizations.GovernmentForm value, each with
/// its display name and its rank names indexed by the ClanLevel stat (0 is the leader).
/// </summary>
public sealed class GoverningFormCatalog
{
    public static GoverningFormCatalog Empty { get; } = new();

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Dictionary<int, GoverningForm> Forms { get; set; } = new();

    public static GoverningFormCatalog Load(string root, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string path = Path.Combine(root, GameDataPaths.GoverningFormsFileName);
        if (!File.Exists(path))
        {
            logger.Warn(string.Format(CultureInfo.InvariantCulture, "GoverningForms.json not found at {0}; /org info shows no governing form or rank", path));
            return Empty;
        }

        try
        {
            GoverningFormCatalog catalog = JsonSerializer.Deserialize<GoverningFormCatalog>(File.ReadAllText(path), JsonOptions) ?? Empty;
            logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData governing forms={0} from {1}", catalog.Forms.Count, path));
            return catalog;
        }
        catch (Exception exception)
        {
            logger.Error(exception, string.Format(CultureInfo.InvariantCulture, "Failed to load GoverningForms.json from {0}; /org info shows no governing form or rank", path));
            return Empty;
        }
    }

    public string GetFormName(int form) => Forms.TryGetValue(form, out GoverningForm? entry) ? entry.Name : string.Empty;

    public string GetRankName(int form, int rank) =>
        Forms.TryGetValue(form, out GoverningForm? entry) && rank >= 0 && rank < entry.Ranks.Length
            ? entry.Ranks[rank]
            : string.Empty;
}

public sealed class GoverningForm
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Rank names by ClanLevel, highest authority first.</summary>
    public string[] Ranks { get; set; } = [];
}
