using System.Collections;
using System.Reflection;
// args: builtDir oldItemsDat newItemsDat
string built = args[0];
AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
    string p = Path.Combine(built, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
var asm = Assembly.LoadFrom(Path.Combine(built, "ZoneEngine_New.dll"));
var reader = asm.GetType("ZoneEngine_New.Core.Inventory.Dat.ItemsDatReader", true);
var read = reader.GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
IList a = (IList)read.Invoke(null, new object[] { args[1] });
IList b = (IList)read.Invoke(null, new object[] { args[2] });
Console.WriteLine($"old={a.Count} new={b.Count}");
string Dump(object o) {
    var sb = new System.Text.StringBuilder();
    void W(object v, int d) {
        if (v == null) { sb.Append("null"); return; }
        var t = v.GetType();
        if (t.IsPrimitive || v is string || t.IsEnum || v is decimal) { sb.Append(v); return; }
        if (t.FullName.StartsWith("MsgPack.MessagePackObject")) { sb.Append(v.ToString()); return; }
        if (d > 8) { sb.Append(t.Name); return; }
        if (v is IDictionary dict) { sb.Append('{'); foreach (DictionaryEntry e in dict) { W(e.Key, d + 1); sb.Append(':'); W(e.Value, d + 1); sb.Append(','); } sb.Append('}'); return; }
        if (v is IEnumerable en) { sb.Append('['); foreach (var x in en) { W(x, d + 1); sb.Append(','); } sb.Append(']'); return; }
        sb.Append(t.Name).Append('{');
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) { if (p.GetIndexParameters().Length > 0) continue; sb.Append(p.Name).Append('='); W(p.GetValue(v), d + 1); sb.Append(' '); }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) { sb.Append(f.Name).Append('='); W(f.GetValue(v), d + 1); sb.Append(' '); }
        sb.Append('}');
    }
    W(o, 0); return sb.ToString();
}
int differing = 0, onlyTicks = 0; var samples = new List<string>();
for (int i = 0; i < Math.Min(a.Count, b.Count); i++) {
    string x = Dump(a[i]), y = Dump(b[i]);
    if (x == y) continue;
    differing++;
    string xn = System.Text.RegularExpressions.Regex.Replace(x, @"TickCount=-?\d+", "TickCount=?");
    string yn = System.Text.RegularExpressions.Regex.Replace(y, @"TickCount=-?\d+", "TickCount=?");
    if (xn == yn) onlyTicks++; else if (samples.Count < 5) samples.Add(x.Substring(0, Math.Min(400, x.Length)) + "\n  vs\n" + y.Substring(0, Math.Min(400, y.Length)));
}
Console.WriteLine($"differing={differing} onlyTickCount={onlyTicks}");
foreach (var s in samples) Console.WriteLine(s + "\n---");
// TickCount distribution in new
var counts = new Dictionary<int, int>();
foreach (var item in b) {
    var ev = item.GetType().GetProperty("Events")?.GetValue(item) ?? item.GetType().GetField("Events")?.GetValue(item);
}
