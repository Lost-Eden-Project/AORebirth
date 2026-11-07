using System.Text.Json;
using Ionic.Zlib;
using MsgPack.Serialization;
using ZoneEngine_New.Core.Inventory.Dat;

static List<T> ReadSlices<T>(string path)
{
    var result = new List<T>();
    using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var binaryReader = new BinaryReader(fileStream);
    byte versionLength = binaryReader.ReadByte();
    binaryReader.ReadChars(versionLength);
    binaryReader.ReadInt32();
    binaryReader.ReadInt32();
    int slices = binaryReader.ReadInt32();
    var serializer = MessagePackSerializer.Get<List<T>>();
    for (int i = 0; i < slices; i++)
    {
        int size = binaryReader.ReadInt32();
        byte[] buffer = binaryReader.ReadBytes(size);
        using var compressed = new MemoryStream(buffer);
        using var zlib = new ZlibStream(compressed, CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        zlib.CopyTo(unpacked);
        unpacked.Position = 0;
        List<T>? slice = serializer.Unpack(unpacked);
        if (slice != null)
            result.AddRange(slice);
    }
    return result;
}

string path = args.Length > 0 ? args[0] : @"AORebirth\GameData\items.dat";
if (args.Length > 2 && args[1] == "arg")
{
    // Every item with a function argument equal to args[2].
    string wanted = args[2];
    foreach (var candidate in ReadSlices<DatItemTemplate>(path))
        foreach (var e in candidate.Events)
            foreach (var f in e.Functions)
                if (f.Arguments.Values.Any(v => v.ToObject()?.ToString() == wanted))
                    Console.WriteLine($"{candidate.ID} event={e.EventType} fn={f.FunctionType} args={string.Join(",", f.Arguments.Values.Select(v => v.ToObject()))}");
    return;
}
if (args.Length > 2 && args[1] == "name")
{
    // Items whose name contains args[2..] (any of them), with QL and the requirements that read stat 182.
    var wanted = args.Skip(2).ToArray();
    foreach (var candidate in ReadSlices<DatItemTemplate>(path))
    {
        if (candidate.Name == null || !wanted.Any(w => candidate.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            continue;
        string ql = candidate.Stats.TryGetValue(54, out int q) ? q.ToString() : "?";
        Console.WriteLine($"{candidate.ID}\tQL{ql}\t{candidate.Name}");
    }
    return;
}
if (args.Length > 2 && args[1] == "op")
{
    int op = int.Parse(args[2]);
    foreach (var candidate in ReadSlices<DatItemTemplate>(path))
    {
        foreach (var a in candidate.Actions)
            if (a.Requirements.Any(r => (int)r.Operator == op))
                Console.WriteLine($"{candidate.ID}	{candidate.Name}	action{a.ActionType}	{string.Join(" ", a.Requirements.Select(r => $"({r.Statnumber},{(int)r.Operator},{r.Value})"))}");
        foreach (var e in candidate.Events)
            foreach (var f in e.Functions)
                if (f.Requirements.Any(r => (int)r.Operator == op))
                    Console.WriteLine($"{candidate.ID}	{candidate.Name}	ev{e.EventType} fn{f.FunctionType}	{string.Join(" ", f.Requirements.Select(r => $"({r.Statnumber},{(int)r.Operator},{r.Value})"))}");
    }
    return;
}
if (args.Length > 1 && args[1] == "req")
{
    // Every requirement on stat args[2]: operator and value, counted.
    int stat = int.Parse(args[2]);
    var seen = new SortedDictionary<string, List<string>>();
    foreach (var candidate in ReadSlices<DatItemTemplate>(path))
    {
        foreach (var a in candidate.Actions)
            foreach (var r in a.Requirements)
                if (r.Statnumber == stat)
                {
                    string key = $"action{a.ActionType} op{r.Operator} v{r.Value}";
                    if (!seen.TryGetValue(key, out var l)) seen[key] = l = new List<string>();
                    if (l.Count < 3) l.Add($"{candidate.ID}:{candidate.Name}");
                    else if (l.Count == 3) l.Add("...");
                }
        foreach (var e in candidate.Events)
            foreach (var f in e.Functions)
            {
                var values = f.Arguments.Values.Select(v => v.ToObject()?.ToString()).ToArray();
                if (values.Length > 0 && values[0] == stat.ToString())
                {
                    string key = $"fn{f.FunctionType} ev{e.EventType} args={string.Join(",", values)}";
                    if (!seen.TryGetValue(key, out var l)) seen[key] = l = new List<string>();
                    if (l.Count < 3) l.Add($"{candidate.ID}:{candidate.Name}");
                }
            }
    }
    foreach (var pair in seen) Console.WriteLine($"{pair.Key}\t{string.Join(" | ", pair.Value)}");
    return;
}
if (args.Length > 1 && args[1] == "hit")
{
    // Every Hit (53002) written with three or more arguments whose third is 0 ([stat, N, 0, 0]), grouped by stat.
    // Stat 52 (XP) is listed in full; it is the form the daily-reward percent XP keys on.
    var byStat = new SortedDictionary<long, List<string>>();
    foreach (var candidate in ReadSlices<DatItemTemplate>(path))
        foreach (var e in candidate.Events)
            foreach (var f in e.Functions)
            {
                if (f.FunctionType != 53002)
                    continue;
                var values = f.Arguments.Values.Select(v => Convert.ToInt64(v.ToObject())).ToArray();
                if (values.Length < 3 || values[2] != 0)
                    continue;
                if (!byStat.TryGetValue(values[0], out var list))
                    byStat[values[0]] = list = new List<string>();
                list.Add($"{candidate.ID}:{candidate.Name}:ev{e.EventType}:[{string.Join(",", values)}]");
            }
    foreach (var pair in byStat)
        Console.WriteLine($"stat {pair.Key}\t{pair.Value.Count}\t{string.Join(" | ", pair.Key == 52 ? pair.Value : pair.Value.Take(4))}");
    return;
}
int id = args.Length > 1 ? int.Parse(args[1]) : 205606;
var item = ReadSlices<DatItemTemplate>(path).First(x => x.ID == id);
Console.WriteLine(JsonSerializer.Serialize(new
{
    item.ID,
    item.DynelType,
    item.ItemType,
    stats = item.Stats,
    events = item.Events.Select(e => new
    {
        e.EventType,
        functions = e.Functions.Select(f => new
        {
            f.FunctionType,
            f.Target,
            f.TickCount,
            f.TickInterval,
            requirements = f.Requirements.Select(r => new { r.Statnumber, r.Value, r.Operator }),
            args = f.Arguments.Values.Select(v => v.ToObject()).ToArray()
        })
    }),
    actions = item.Actions.Select(a => new
    {
        a.ActionType,
        requirements = a.Requirements.Select(r => new { r.Statnumber, r.Value, r.Operator })
    }),
    attack = item.Attack,
    defend = item.Defend
}, new JsonSerializerOptions { WriteIndented = true }));
