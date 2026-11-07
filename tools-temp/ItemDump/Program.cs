using System.Collections;
using System.Reflection;
// args: builtDir itemsDat id...
string built = args[0];
AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
    string p = Path.Combine(built, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
var asm = Assembly.LoadFrom(Path.Combine(built, "ZoneEngine_New.dll"));
var read = asm.GetType("ZoneEngine_New.Core.Inventory.Dat.ItemsDatReader", true).GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
IList items = (IList)read.Invoke(null, new object[] { args[1] });
if (args[2] == "scan") {
    var rx = new System.Text.RegularExpressions.Regex(@"Values=\[([A-Z0-9]{4,6}), (-?\d+), (-?\d+)[^\]]*\]\} FunctionType=53064 ");
    foreach (var item in items) {
        string dump = Show(item);
        var name = System.Text.RegularExpressions.Regex.Match(dump, @"Name=(.*?) Rdb=").Groups[1].Value;
        if (!(name.Contains("Boss") || name.Contains("Lootgiver"))) continue;
        var parts = rx.Matches(dump).Select(m => m.Groups[1].Value + "/" + m.Groups[2].Value + "/" + m.Groups[3].Value).ToList();
        if (parts.Count > 0) Console.WriteLine(item.GetType().GetField("ID").GetValue(item) + "\t" + name + "\t" + string.Join(" ", parts));
    }
    return;
}
if (args[2] == "names") {
    // Every item's id and name, tab separated.
    foreach (var item in items) {
        var name = System.Text.RegularExpressions.Regex.Match(Show(item, 6), @"Name=(.*?) Rdb=").Groups[1].Value;
        Console.WriteLine(item.GetType().GetField("ID").GetValue(item) + "\t" + name);
    }
    return;
}
if (args[2] == "scanhit") {
    // Every Hit (53002) spell written with three or more arguments whose third is 0, by stat: the [stat, N, 0, 0] form.
    var rx = new System.Text.RegularExpressions.Regex(@"Values=\[(-?\d+), (-?\d+), (-?\d+)(?:, (-?\d+))?\]\} FunctionType=53002 ");
    var byStat = new Dictionary<string, List<string>>();
    foreach (var item in items) {
        string dump = Show(item);
        var name = System.Text.RegularExpressions.Regex.Match(dump, @"Name=(.*?) Rdb=").Groups[1].Value;
        var id = item.GetType().GetField("ID").GetValue(item);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(dump)) {
            if (m.Groups[3].Value != "0") continue;
            if (!byStat.TryGetValue(m.Groups[1].Value, out var l)) byStat[m.Groups[1].Value] = l = new List<string>();
            l.Add(id + ":" + name + ":" + m.Groups[2].Value);
        }
    }
    foreach (var kv in byStat.OrderBy(k => int.Parse(k.Key))) Console.WriteLine(kv.Key + "\t" + kv.Value.Count + "\t" + string.Join(" | ", kv.Value.Take(8)));
    return;
}
var ids = args.Skip(2).Select(int.Parse).ToHashSet();
string Show(object v, int d = 0) {
    if (v == null) return "null";
    var t = v.GetType();
    if (t.IsPrimitive || v is string || t.IsEnum) return v.ToString();
    if (t.FullName.StartsWith("MsgPack.MessagePackObject")) return v.ToString();
    if (d > 6) return t.Name;
    if (v is IDictionary dict) { var parts = new List<string>(); foreach (DictionaryEntry e in dict) parts.Add(Show(e.Key, d + 1) + ":" + Show(e.Value, d + 1)); return "{" + string.Join(", ", parts) + "}"; }
    if (v is IEnumerable en) return "[" + string.Join(", ", en.Cast<object>().Select(x => Show(x, d + 1))) + "]";
    return t.Name + "{" + string.Join(" ", t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name + "=" + Show(p.GetValue(v), d + 1))
        .Concat(t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name + "=" + Show(f.GetValue(v), d + 1)))) + "}";
}
foreach (var item in items) {
    var idObj = item.GetType().GetField("ID")?.GetValue(item) ?? item.GetType().GetProperty("ID")?.GetValue(item);
    if (idObj is int id && ids.Contains(id)) Console.WriteLine(Show(item) + "\n");
}
