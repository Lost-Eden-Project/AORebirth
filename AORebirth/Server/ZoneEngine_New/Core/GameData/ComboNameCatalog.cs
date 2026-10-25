namespace ZoneEngine_New.Core.GameData;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

using AORebirth.Core.GameData;

using ZoneEngine_New.Core.Logging;

/// <summary>
/// ComboNames.json: the name parts a ComboNameGen item (the "Name Giver" boss equipment) builds an NPC's name from,
/// keyed by the NPC's Clan stat. A name is a Name part, a space, then a Title part. Each part is one of its Words
/// followed by one of its Endings, or one of its Standalone entries, which may also end the part alone
/// (<see cref="StandaloneEndChancePercent"/>). Texts are joined exactly as written: an Ending that starts a new word
/// carries its own leading space (" Slayer"), one that continues the word does not ("gash").
/// </summary>
public sealed class ComboNameCatalog
{
    public static ComboNameCatalog Empty { get; } = new();

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public int StandaloneEndChancePercent { get; set; }

    public Dictionary<int, ComboNameClan> Clans { get; set; } = new();

    public static ComboNameCatalog Load(string root, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string path = Path.Combine(root, GameDataPaths.ComboNamesFileName);
        if (!File.Exists(path))
        {
            logger.Warn(string.Format(CultureInfo.InvariantCulture, "ComboNames.json not found at {0}; NPCs keep their template names", path));
            return Empty;
        }

        try
        {
            ComboNameCatalog catalog = JsonSerializer.Deserialize<ComboNameCatalog>(File.ReadAllText(path), JsonOptions) ?? Empty;
            catalog.StandaloneEndChancePercent = Math.Clamp(catalog.StandaloneEndChancePercent, 0, 100);
            logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData combo name clans={0} from {1}", catalog.Clans.Count, path));
            return catalog;
        }
        catch (Exception exception)
        {
            logger.Error(exception, string.Format(CultureInfo.InvariantCulture, "Failed to load ComboNames.json from {0}; NPCs keep their template names", path));
            return Empty;
        }
    }

    /// <summary>A generated name for this clan, or false when the clan has no Name and Title parts.</summary>
    public bool TryGenerate(int clan, Random random, out string name)
    {
        ArgumentNullException.ThrowIfNull(random);
        name = string.Empty;
        if (!Clans.TryGetValue(clan, out ComboNameClan? entry) || entry.Name.IsEmpty || entry.Title.IsEmpty)
            return false;

        var builder = new StringBuilder();
        entry.Name.AppendTo(builder, random, StandaloneEndChancePercent);
        builder.Append(' ');
        entry.Title.AppendTo(builder, random, StandaloneEndChancePercent);
        name = builder.ToString();
        return true;
    }
}

public sealed class ComboNameClan
{
    /// <summary>What the clan's names are for; documentation only.</summary>
    public string Creature { get; set; } = string.Empty;

    public ComboNamePart Name { get; set; } = new();

    public ComboNamePart Title { get; set; } = new();
}

public sealed class ComboNamePart
{
    public string[] Words { get; set; } = [];

    public string[] Standalone { get; set; } = [];

    public string[] Endings { get; set; } = [];

    public bool IsEmpty => Words.Length + Standalone.Length == 0 || (Standalone.Length == 0 && Endings.Length == 0);

    /// <summary>One first entry from Words and Standalone together; then an Ending unless a Standalone entry ends it.</summary>
    public void AppendTo(StringBuilder builder, Random random, int standaloneEndChancePercent)
    {
        int pick = random.Next(Words.Length + Standalone.Length);
        bool standalone = pick >= Words.Length;
        builder.Append(standalone ? Standalone[pick - Words.Length] : Words[pick]);
        if (Endings.Length == 0 || (standalone && random.Next(100) < standaloneEndChancePercent))
            return;

        builder.Append(Endings[random.Next(Endings.Length)]);
    }
}
