using System.Collections;
using System.Reflection;

var aodbAsm = Assembly.LoadFrom(@"C:\Users\tyler\.nuget\packages\aodb\1.0.6\lib\net10.0\AODB.dll");
object rdb = Activator.CreateInstance(aodbAsm.GetType("AODB.RdbController")!, @"C:\Users\tyler\OneDrive\Desktop\Anarchy Online")!;
var get = aodbAsm.GetType("AODB.RdbController")!.GetMethod("Get", [typeof(int), typeof(int)])!;
var map = (IDictionary)aodbAsm.GetType("AODB.RdbController")!.GetProperty("RecordTypeToId")!.GetValue(rdb)!;
var meta = new HashSet<string>(StringComparer.Ordinal) { "Duration", "Interval", "ApplyOn", "TargetList" };
int hits = 0;
foreach (int type in new[] { 1000020, 1040005 })
{
    foreach (int id in ((IDictionary)map[type]!).Keys.Cast<int>())
    {
        object? rec;
        try { rec = get.Invoke(rdb, [type, id]); } catch { continue; }
        if (rec?.GetType().GetField("Modifiers")!.GetValue(rec) is not IDictionary mods) continue;
        foreach (DictionaryEntry ev in mods)
        {
            if (ev.Value!.GetType().GetField("Modifiers")!.GetValue(ev.Value) is not IDictionary inner) continue;
            foreach (DictionaryEntry fn in inner)
            {
                if (fn.Key.ToString() is not ("Modify" or "ScalingModify" or "SetFlag")) continue;
                foreach (var entry in (IEnumerable)fn.Value!)
                {
                    var d = (IDictionary)entry!;
                    Console.Write($"{type}/{id} {fn.Key}:");
                    foreach (DictionaryEntry kv in d)
                    {
                        if (meta.Contains(kv.Key.ToString()!)) continue;
                        Console.Write($" {kv.Key}={kv.Value}");
                    }
                    Console.WriteLine();
                    if (++hits >= 20) return;
                }
            }
        }
    }
}
