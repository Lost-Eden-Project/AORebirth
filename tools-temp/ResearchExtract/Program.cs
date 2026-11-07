using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AODB;
using AODB.Common.RDBObjects;

// Research.json from the client resource database perk records (type 1000036), the table Gamecode.dll's perk manager
// (FUN_1002c0d5) serves N3Msg_IsResearch / N3Msg_PersonalResearchGoals / N3Msg_GetPerkProgress from.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.SetIn(new StringReader(string.Empty));
string aoPath = new[] { @"C:\Users\tyler\OneDrive\Desktop\Anarchy Online", @"C:\Users\tyler\Desktop\Anarchy Online", @"F:\Anarchy Online",
    @"C:\Program Files (x86)\Steam\steamapps\common\Anarchy Online" }
    .First(p => File.Exists(Path.Combine(p, "cd_image", "data", "db", "ResourceDatabase.idx")));
Directory.SetCurrentDirectory(aoPath);
using var rdb = new RdbController(aoPath);

const int PerkType = 1000036;
const int FlagPersonal = 0x40, FlagGlobal = 0x80, FlagLandControl = 0x100;
const int StatProfession = 60, StatLevel = 54, OpEqualTo = 0, OpGreaterThan = 2, ActionUse = 6;
string[] professionNames =
[
    "", "Soldier", "MartialArtist", "Engineer", "Fixer", "Agent", "Adventurer", "Trader", "Bureaucrat", "Enforcer",
    "Doctor", "NanoTechnician", "MetaPhysicist", "", "Keeper", "Shade"
];

var perks = new Dictionary<int, Perk>();
foreach (int id in rdb.RecordTypeToId[PerkType].Keys.OrderBy(x => x))
{
    byte[] raw = rdb.GetRaw(PerkType, id);
    if (raw == null || raw.Length < 36)
        continue;
    int I(int offset) => BitConverter.ToInt32(raw, offset);
    int flags = I(20);
    if ((flags & (FlagPersonal | FlagGlobal)) == 0)
        continue;

    ItemObject? item = null;
    try { item = rdb.Get<ItemObject>(I(12)); } catch { }
    var professions = new List<int>();
    int minLevel = 0;
    if (item != null && item.ActionCriteria.TryGetValue(ActionUse, out var criteria))
    {
        foreach (var c in criteria)
        {
            if (c.Value1 == StatProfession && c.Operator == OpEqualTo)
                professions.Add(c.Value2);
            else if (c.Value1 == StatLevel && c.Operator == OpGreaterThan)
                minLevel = c.Value2 + 1;
        }
    }

    perks[I(4)] = new Perk(I(4), I(12), I(16), flags, I(24), I(32), item?.Name ?? "", professions, minLevel);
}

// A line is the PreviousId chain from a root (PreviousId 0); a perk several perks follow (global research branches)
// starts one line per branch, each carrying the shared tiers.
var children = perks.Values.Where(p => p.PreviousId != 0).GroupBy(p => p.PreviousId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());
var lines = new List<List<Perk>>();
void Walk(List<Perk> chain)
{
    if (!children.TryGetValue(chain[^1].Id, out var next))
    {
        lines.Add(chain);
        return;
    }
    foreach (Perk child in next)
        Walk([.. chain, child]);
}
foreach (Perk start in perks.Values.Where(p => p.PreviousId == 0 || !perks.ContainsKey(p.PreviousId)).OrderBy(p => p.Id))
    Walk([start]);

string ProfessionName(int id) => id > 0 && id < professionNames.Length && professionNames[id] != "" ? professionNames[id] : id.ToString();
List<string> MaskProfessions(int mask)
    => Enumerable.Range(0, 32).Where(bit => (mask & (1 << bit)) != 0).Select(ProfessionName).ToList();

var personal = new JsonArray();
var global = new JsonArray();
var byProfession = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
foreach (List<Perk> line in lines)
{
    Perk first = line[0];
    bool isGlobal = (first.Flags & FlagGlobal) != 0;
    var tiers = new JsonArray();
    for (int i = 0; i < line.Count; i++)
    {
        Perk p = line[i];
        var tier = new JsonObject { ["Tier"] = i + 1, ["Id"] = p.Id, ["ItemId"] = p.ItemId };
        if (isGlobal)
        {
            tier["Goal"] = (long)p.Field18 * 1000;
            if ((p.Flags & FlagLandControl) != 0)
                tier["LandControlOnly"] = true;
        }
        else
        {
            tier["Xp"] = p.Field18;
            if (p.MinLevel > 0)
                tier["MinLevel"] = p.MinLevel;
        }

        tiers.Add(tier);
    }

    if (isGlobal)
    {
        global.Add(new JsonObject { ["Name"] = first.Name, ["Tiers"] = tiers });
        continue;
    }

    // Who may research it: the tier-1 item's Profession criteria, as the client reads them (Gamecode.dll FUN_10052d48:
    // none means every profession). The record's ProfessionMask (1 << profession) says the same.
    List<string> professions = first.Professions.Count > 0
        ? first.Professions.Select(ProfessionName).ToList()
        : MaskProfessions(first.ProfessionMask);
    var entry = new JsonObject { ["Name"] = first.Name };
    entry["Professions"] = professions.Count > 0 ? new JsonArray(professions.Select(n => (JsonNode)n!).ToArray()) : "All";
    entry["ProfessionMask"] = first.ProfessionMask;
    entry["Tiers"] = tiers;
    personal.Add(entry);

    foreach (string profession in professions.Count > 0 ? professions : ["All"])
    {
        if (!byProfession.TryGetValue(profession, out var names))
            byProfession[profession] = names = new List<string>();
        names.Add(first.Name);
    }
}

var byProfessionJson = new JsonObject();
foreach ((string profession, List<string> names) in byProfession)
    byProfessionJson[profession] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray());

var root = new JsonObject
{
    ["Notes"] = new JsonArray(
        "Generated from the client resource database perk records (type 1000036, 36 bytes: type, Id, version, ItemId, PreviousId, Flags, ProfessionMask, Field14, Field18), the table Gamecode.dll's perk manager serves research from.",
        "Personal research is Flags 0x40, global 0x80 (0x100 also: usable only in the owner's land control area). A line is the PreviousId chain from its first tier.",
        "Xp (personal) is Field18, the research XP a tier needs: N3Msg_GetPerkProgress reports (Field18 - remaining) / Field18. Goal (global) is Field18 x 1000.",
        "Professions come from the tier-1 item's ToUse (action 6) criteria Profession = N, as the client checks them (FUN_10052d48); \"All\" when there are none. MinLevel is the item's Level > N criterion plus one."),
    ["PersonalResearch"] = personal,
    ["PersonalResearchByProfession"] = byProfessionJson,
    ["GlobalResearch"] = global
};

File.WriteAllText(args[0], root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
Console.WriteLine($"personal lines={personal.Count} global lines={global.Count} professions={byProfession.Count}");

sealed record Perk(int Id, int ItemId, int PreviousId, int Flags, int ProfessionMask, int Field18, string Name,
    List<int> Professions, int MinLevel);
