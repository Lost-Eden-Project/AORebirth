// Writes JSON schemas for the hand-edited GameData and Config files from the C# types ZoneEngine_New loads them into,
// so the schemas cannot drift from the server. Editors pick them up through .vscode/settings.json "json.schemas".
//
//   dotnet run --project Tools/GameDataSchemas [output-directory]
//
// The default output directory is AORebirth/Schemas under the repository root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons.Static;

string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(FindRepositoryRoot(), "AORebirth", "Schemas");
Directory.CreateDirectory(output);

Type npcHash = typeof(NpcTemplateCatalog).GetNestedType("NpcHashDto", BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NpcTemplateCatalog.NpcHashDto not found.");

Write("MonsterCombatRules", "Monster combat rules: combat behaviour keyed by mob hash.", nameof(MonsterCombatRules),
    typeof(Dictionary<string, MonsterCombatRules>), LoaderOptions(typeof(MonsterCombatRulesCatalog)));
Write("NpcTemplates", "NPC templates: mob hashes, their families and level bands.", "NpcTemplateCatalog.NpcHashDto",
    typeof(Dictionary<,>).MakeGenericType(typeof(string), npcHash), LoaderOptions(typeof(NpcTemplateCatalog)));
// Gameplay.json is written in camelCase; the loader matches names case-insensitively.
Write("Gameplay", "Gameplay settings (Config/Gameplay.json).", nameof(GameplaySettings),
    typeof(GameplaySettings), LoaderOptions(typeof(GameplaySettings), JsonNamingPolicy.CamelCase));
Type rewardFile = typeof(QuestRewardTable).GetNestedType("RewardFile", BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("QuestRewardTable.RewardFile not found.");
Write("QuestRewards", "Quest rewards: the items a Quests.json reward hash gives, first matching entry wins.", "QuestRewardTable.RewardFile",
    typeof(Dictionary<,>).MakeGenericType(typeof(string), rewardFile), LoaderOptions(typeof(QuestRewardTable)));
Write("Quests", "Quests: Quests.json quest templates, a list keyed by Hash.", nameof(QuestTemplate),
    typeof(List<QuestTemplate>), LoaderOptions(typeof(QuestTemplate)));
// Static dungeons: one GameData/Dungeons/Static/<entrance instance>.json per fixed dungeon behind a world door, and the
// per-style site pool GameData/Dungeons/Sites/<style>.json its chests, mines and turrets are rolled from.
Write("StaticDungeon", "Static dungeon: a fixed ACG dungeon behind a world door (Dungeons/Static/<entrance instance>.json).",
    nameof(StaticDungeonFile), typeof(StaticDungeonFile), LoaderOptions(typeof(StaticDungeonFile)));
Write("StaticDungeonSites", "Static dungeon site pool: chest, mine and turret spots per room template of one ACG style (Dungeons/Sites/<style>.json).",
    nameof(StaticDungeonSitePoolFile), typeof(StaticDungeonSitePoolFile), LoaderOptions(typeof(StaticDungeonFile)));
// Org cities: CityRules.json (the server's rules), and CityControllers.json and HouseTemplates.json (the client's RDB
// 1000039/1000040 records, exported by RDBDataExtractor into private GameData).
Write("CityRules", "Org city rules: billing, timers, who may build (CityRules.json).", "CityCatalog.RulesFile",
    typeof(ZoneEngine_New.Core.Cities.CityCatalog.RulesFile), LoaderOptions(typeof(ZoneEngine_New.Core.Cities.CityCatalog)));
Write("CityControllers", "Org city districts per playfield: costs and buildable zones (CityControllers.json, RDB 1000039).",
    "CityCatalog.ControllersFile", typeof(ZoneEngine_New.Core.Cities.CityCatalog.ControllersFile),
    LoaderOptions(typeof(ZoneEngine_New.Core.Cities.CityCatalog)));
Write("HouseTemplates", "Org city house templates: type, upkeep and footprint (HouseTemplates.json, RDB 1000040).",
    "CityCatalog.TemplatesFile", typeof(ZoneEngine_New.Core.Cities.CityCatalog.TemplatesFile),
    LoaderOptions(typeof(ZoneEngine_New.Core.Cities.CityCatalog)));
// Xp.json: level rows keyed by level number, plus the NoKillXpFamilies list beside them.
Type xpLevelRow = typeof(GameDataStore).GetNestedType("XpLevelRow", BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("GameDataStore.XpLevelRow not found.");
JsonSerializerOptions xpOptions = LoaderOptions(typeof(NoKillXpFamilyCatalog));
Emit("Xp", "XP table: the level rows, keyed by level, and the NPC families whose kills give no XP.",
    "GameDataStore.XpLevelRow and NoKillXpFamilyCatalog.FamilyRow", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [NoKillXpFamilyCatalog.PropertyName] = Refine(
                xpOptions.GetJsonSchemaAsNode(typeof(List<NoKillXpFamilyCatalog.FamilyRow>), Exporter())),
        },
        ["patternProperties"] = new JsonObject { ["^[1-9][0-9]*$"] = xpOptions.GetJsonSchemaAsNode(xpLevelRow, Exporter()) },
        ["additionalProperties"] = false,
    });
return 0;

// NoKillXpFamilies: Family is required and an NPCFamily value, 0 or more.
static JsonNode Refine(JsonNode list)
{
    if (list["items"] is JsonObject item && item["properties"] is JsonObject properties)
    {
        item["required"] = new JsonArray("Family");
        properties["Family"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "NPCFamily (stat 455)." };
    }

    return list;
}

void Write(string name, string title, string source, Type type, JsonSerializerOptions options)
    => Emit(name, title, source, options.GetJsonSchemaAsNode(type, Exporter()));

JsonSchemaExporterOptions Exporter()
{
    return new JsonSchemaExporterOptions
    {
        TreatNullObliviousAsNonNullable = true,
        // A misspelt property loads as nothing at all; flag it in the editor.
        TransformSchemaNode = (context, node) =>
        {
            // QuestRewards item Ql is read as a raw JsonElement: a positive QL or "level" (the player's level).
            if (context.PropertyInfo is { Name: "Ql" } property && property.DeclaringType.DeclaringType == typeof(QuestRewardTable))
                return new JsonObject
                {
                    ["description"] = "QL to give: a positive number, or \"level\" for the player's level. Default 1.",
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        new JsonObject { ["const"] = "level" }),
                };
            if (context.PropertyInfo is { } questProperty && questProperty.DeclaringType == typeof(QuestTemplate) && node is JsonObject questNode)
                RefineQuestProperty(questProperty.Name, questNode);
            if (context.PropertyInfo is { } cityProperty && cityProperty.DeclaringType.DeclaringType == typeof(ZoneEngine_New.Core.Cities.CityCatalog)
                && node is JsonObject cityNode)
                RefineCityProperty(cityProperty.Name, cityNode);
            // Quests.json reward items live in QuestRewards.json, keyed by Reward.Hash; Records is not read from it.
            if (context.TypeInfo.Type == typeof(QuestReward) && node is JsonObject rewardNode
                && rewardNode["properties"] is JsonObject rewardProperties)
                rewardProperties.Remove("Records");
            if (node is JsonObject obj && obj.ContainsKey("properties") && !obj.ContainsKey("additionalProperties"))
                obj["additionalProperties"] = false;
            return node;
        },
    };
}

void Emit(string name, string title, string source, JsonNode schema)
{
    if (schema is JsonObject root)
    {
        var ordered = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = name + ".schema.json",
            ["title"] = title,
            ["description"] = "Generated by Tools/GameDataSchemas from " + source + "; do not edit by hand.",
        };
        foreach ((string key, JsonNode? value) in root.ToList())
        {
            root.Remove(key);
            ordered[key] = value;
        }

        schema = ordered;
    }

    string path = Path.Combine(output, name + ".schema.json");
    File.WriteAllText(path, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine(path);
}

// Quests.json value sets, taken from the private Quests.json (3022 quests, 2026-10-29). A quest hash is four
// characters, digits, capitals or '_'; mission bits are 0 to MissionBits.Count - 1.
static void RefineQuestProperty(string name, JsonObject node)
{
    const string hashPattern = "^[0-9A-Z_]{4}$";
    const string flag = "(NoXp|NoCash|Failed|NoDelete)";
    switch (name)
    {
        case nameof(QuestTemplate.Hash):
        case nameof(QuestTemplate.NextStep):
            node["pattern"] = hashPattern;
            break;
        case nameof(QuestTemplate.Scope):
            node["enum"] = new JsonArray("Solo", "Team");
            break;
        case nameof(QuestTemplate.Action):
            node["enum"] = new JsonArray("KillOne", "KillMultiple", "TargetNpc", "TalkTo", "FindItem", "FindItem2",
                "ReturnToQuestGiver", "UseItemOnCharacter", "UseItemOnCharacterDev", "UseItemOnItem", "Assemble", "Equip",
                "CreateItem", "BringItemHere", "EscortItem", "Escort");
            break;
        case nameof(QuestTemplate.Flags):
            node["pattern"] = "^(None|" + flag + "(, " + flag + ")*)$";
            node["description"] = "Comma list of NoXp, NoCash, Failed, NoDelete, or \"None\".";
            break;
        case nameof(QuestTemplate.MissionBit):
            node["minimum"] = 0;
            node["maximum"] = MissionBits.Count - 1;
            break;
        case nameof(QuestTemplate.IconId):
        case nameof(QuestTemplate.QasInstance):
            node["minimum"] = 0;
            break;
    }
}

// The ranges CityCatalog checks on load (CityRules.Validate and the file checks).
static void RefineCityProperty(string name, JsonObject node)
{
    switch (name)
    {
        case "BillingPeriodDays":
        case "EmptyPlotReleaseHours":
        case "AdvantageQuality":
        case "PendingTimeoutMinutes":
        case "PlayfieldId":
        case "Id":
            node["minimum"] = 1;
            break;
        case "UpkeepWarningHours":
        case "NoHeadquartersLockoutHours":
        case "DemolishSeconds":
        case "CloakToggleCooldownSeconds":
        case "ManageMinRanksAboveLowest":
        case "PurchaseCost":
        case "UpkeepCost":
        case "Upkeep":
        case "ZoneId":
        case "Index":
            node["minimum"] = 0;
            break;
        case "StartingControllerCharge":
        case "AdvantageChargeThreshold":
            node["minimum"] = 0;
            node["maximum"] = 1;
            break;
        case "Playfields":
            node["minItems"] = 1;
            node["description"] = "The playfield ids whose districts are for sale.";
            break;
        case "MaxHousesPerPlot":
            node["minimum"] = 1;
            node["maximum"] = ZoneEngine_New.Core.Cities.CityLimits.MaxHousesPerPlotCap;
            break;
        case "TilesX":
        case "TilesZ":
            node["minimum"] = 1;
            node["maximum"] = 64;
            break;
        case "HouseType":
            node["minimum"] = 0;
            node["maximum"] = 9;
            break;
        case "Mask":
            node["pattern"] = "^[0-9a-fA-F]{26}$";
            node["description"] = "13 bytes of hex: 100 bits, bit z * 10 + x, lowest bit first.";
            break;
        case "Layout":
            node["pattern"] = "^([0-9a-fA-F]{2})+$";
            node["description"] = "TilesX * TilesZ bits of hex, bit z * TilesX + x, lowest bit first.";
            break;
    }
}

// The loader's own options (naming, enum converters), with a resolver so the exporter can read them.
static JsonSerializerOptions LoaderOptions(Type loader, JsonNamingPolicy? naming = null)
{
    var source = loader.GetField("JsonOptions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        as JsonSerializerOptions ?? throw new InvalidOperationException(loader.Name + ".JsonOptions not found.");
    var options = new JsonSerializerOptions(source) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    if (naming != null)
        options.PropertyNamingPolicy = naming;
    return options;
}

static string FindRepositoryRoot()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "AORebirth")))
            return dir.FullName;
    }

    throw new InvalidOperationException("Repository root not found; pass the output directory.");
}
