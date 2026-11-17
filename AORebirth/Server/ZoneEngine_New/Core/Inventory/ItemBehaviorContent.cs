namespace ZoneEngine_New.Core.Inventory;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AORebirth.Core.GameData;
using LostEden.Protocol.GameData;

/// <summary>Editable item mechanic bindings. Identity/loot/curve values are content;
/// inventory ownership, transactions and packet semantics remain runtime services.</summary>
public sealed class ItemBehaviorContent
{
    public static ItemBehaviorContent Current { get; } = Load();
    public int[] ProtectedItems { get; set; } = [];
    public FistWeaponContent[] FistWeapons { get; set; } = [];

    /// <summary>Brawl Item template pairs by Brawl skill tier; rows carry no profession.</summary>
    public FistWeaponContent[] BrawlWeapons { get; set; } = [];

    /// <summary>Dimach Item template pairs by profession (null = everyone else) and Dimach skill tier.</summary>
    public FistWeaponContent[] DimachWeapons { get; set; } = [];
    public string[] Provenance { get; set; } = [];

    public static ItemBehaviorContent Load(string? gameDataRoot = null)
        => Parse(File.ReadAllText(Path.Combine(gameDataRoot ?? GameDataPaths.ResolveRuntimeRoot(), "ItemBehavior.json")));

    public static ItemBehaviorContent Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var result = JsonSerializer.Deserialize<ItemBehaviorContent>(json, options)
            ?? throw new InvalidDataException("Item behavior content is empty.");
        if (result.ProtectedItems.Any(id => id <= 0))
            throw new InvalidDataException("Invalid item behavior content.");
        if (result.FistWeapons.Any(value => value.Tier <= 0 || value.LowId <= 0 || value.HighId <= 0
                || value.MidId < 0 || (value.MidId > 0) != (value.MidQuality > 0)
                || value.Profession.HasValue && !Enum.IsDefined(value.Profession.Value))
            || result.FistWeapons.Select(value => (value.Profession, value.Tier)).Distinct().Count() != result.FistWeapons.Length)
            throw new InvalidDataException("Invalid or ambiguous unarmed weapon assignment.");
        if (result.BrawlWeapons.Any(value => value.Tier <= 0 || value.LowId <= 0 || value.HighId <= 0
                || value.Profession.HasValue || value.MidId < 0 || (value.MidId > 0) != (value.MidQuality > 0))
            || result.BrawlWeapons.Select(value => value.Tier).Distinct().Count() != result.BrawlWeapons.Length)
            throw new InvalidDataException("Invalid or ambiguous brawl item assignment.");
        if (result.DimachWeapons.Any(value => value.Tier <= 0 || value.LowId <= 0 || value.HighId <= 0
                || value.MidId < 0 || (value.MidId > 0) != (value.MidQuality > 0)
                || value.Profession.HasValue && !Enum.IsDefined(value.Profession.Value))
            || result.DimachWeapons.Select(value => (value.Profession, value.Tier)).Distinct().Count() != result.DimachWeapons.Length)
            throw new InvalidDataException("Invalid or ambiguous dimach item assignment.");
        return result;
    }

    public bool IsProtected(Item item) => ProtectedItems.Contains(item.LowId) || ProtectedItems.Contains(item.HighId);
}

public sealed class FistWeaponContent
{
    public Profession? Profession { get; set; }
    public int Tier { get; set; }
    public int LowId { get; set; }
    public int HighId { get; set; }

    /// <summary>Optional intermediate template at <see cref="MidQuality"/>; 0 when the tier is one pair.</summary>
    public int MidId { get; set; }

    public int MidQuality { get; set; }
}
