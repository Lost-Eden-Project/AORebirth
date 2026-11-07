using System.Collections;
using System.Reflection;

static string Show(object? v)
{
    if (v is string s) return "\"" + s + "\"";
    if (v is IEnumerable e)
    {
        var parts = new List<string>();
        foreach (var i in e) parts.Add(Show(i));
        return "{" + string.Join(", ", parts) + "}";
    }

    return v?.ToString() ?? "null";
}

var dll = @"C:\Users\tyler\.nuget\packages\aodb\1.0.6\lib\net10.0\AODB.Common.dll";
var asm = Assembly.LoadFrom(dll);

if (args.Length > 0 && args[0] == "criterion")
{
    var t = asm.GetType("AODB.Common.Structs.RequirementCriterion")!;
    Console.WriteLine(t.FullName);
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        Console.WriteLine($"  field {f.Name}: {f.FieldType.Name}");
    foreach (var p in t.GetProperties())
        Console.WriteLine($"  prop {p.Name}: {p.PropertyType.Name}");
    return;
}

var dynelsType = asm.GetType("AODB.Common.RDBObjects.PlayfieldDynels")!;

string root = args.Length > 4
    ? args[4]
    : @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\Built\Debug\ZoneEngine_New\GameData\Playfields";

// args: <playfield> <x> <z> <radius> [playfieldsRoot] [instance]
string pf = args[0];
float px = float.Parse(args[1]);
float pz = float.Parse(args[2]);
float radius = float.Parse(args[3]);

string path = Path.Combine(root, pf, "Dynels.dat");
byte[] payload = File.ReadAllBytes(path);

object? best = null;
long bestRemaining = long.MaxValue;
foreach (int offset in new[] { 12, 0 })
{
    try
    {
        object rec = Activator.CreateInstance(dynelsType)!;
        using var ms = new MemoryStream(payload, offset, payload.Length - offset, false);
        using var br = new BinaryReader(ms);
        dynelsType.GetMethod("Deserialize")!.Invoke(rec, new object[] { br });
        long remaining = ms.Length - ms.Position;
        if (remaining < bestRemaining) { best = rec; bestRemaining = remaining; }
    }
    catch
    {
    }
}

var list = (IList)best!.GetType().GetField("Dynels")!.GetValue(best)!;
Console.WriteLine($"==== PF {pf}: {list.Count} dynels; within {radius}u of ({px},{pz}) ====");

foreach (var d in list)
{
    var t = d!.GetType();
    var pos = t.GetField("Position")!.GetValue(d)!;
    var pt = pos.GetType();
    float Component(string name) =>
        (float)(pt.GetField(name)?.GetValue(pos) ?? pt.GetProperty(name)!.GetValue(pos))!;

    float x = Component("X");
    float y = Component("Y");
    float z = Component("Z");
    int idType = (int)t.GetField("IdentityType")!.GetValue(d)!;
    int idInst = (int)t.GetField("IdentityInstance")!.GetValue(d)!;
    if (args.Length > 5 && int.TryParse(args[5], out int only) && only != 0 && idInst != only)
        continue;

    float dist = MathF.Sqrt(((x - px) * (x - px)) + ((z - pz) * (z - pz)));
    if (dist > radius) continue;
    int tmpl = (int)t.GetField("TemplateId")!.GetValue(d)!;
    var mods = t.GetField("Modifiers")!.GetValue(d) as IDictionary;
    Console.WriteLine($"  dist={dist,6:F2} type={idType} inst=0x{idInst:X8} tmpl={tmpl} pos=({x:F1},{y:F1},{z:F1}) events={mods?.Count ?? 0}");
    if (mods == null) continue;

    foreach (DictionaryEntry me in mods)
    {
        Console.WriteLine($"    event {me.Key}");
        if (me.Value?.GetType().GetField("Modifiers")!.GetValue(me.Value) is not IDictionary inner) continue;
        foreach (DictionaryEntry fe in inner)
        {
            Console.WriteLine($"      fn {fe.Key}");
            foreach (var argSet in (IEnumerable)fe.Value!)
            {
                var parts = new List<string>();
                foreach (DictionaryEntry a in (IDictionary)argSet!)
                {
                    if (a.Key.ToString() == "Criteria" && a.Value is IEnumerable criteria)
                    {
                        int ci = 0;
                        foreach (var c in criteria)
                        {
                            var ct = c!.GetType();
                            int stat = (int)ct.GetField("Stat")!.GetValue(c)!;
                            uint value = (uint)ct.GetField("Value")!.GetValue(c)!;
                            object op = ct.GetField("Operator")!.GetValue(c)!;
                            Console.WriteLine($"          Criteria[{ci}] Stat={stat} Op={op} Value={value}");
                            ci++;
                        }
                        continue;
                    }

                    parts.Add($"{a.Key}={Show(a.Value)}");
                }
                Console.WriteLine("        [" + string.Join(", ", parts) + "]");
            }
        }
    }
}
