using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
// args: pf identityType instance
class P {
  static object Get(object o, string n) { var t = o.GetType(); var p = t.GetProperty(n); if (p != null) return p.GetValue(o); var f = t.GetField(n); return f?.GetValue(o); }
  static string Show(object v) {
    if (v == null) return "null";
    if (v is string s) return "\"" + s + "\"";
    if (v is IDictionary d) return "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => k + "=" + Show(d[k]))) + "}";
    if (v is IEnumerable e) return "[" + string.Join(", ", e.Cast<object>().Select(Show)) + "]";
    var fields = v.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
    var props = v.GetType().GetProperties();
    if (fields.Length > 0 && !v.GetType().IsPrimitive && !v.GetType().IsEnum) return v.GetType().Name + "(" + string.Join(" ", fields.Select(f => f.Name + "=" + f.GetValue(v)).Concat(props.Select(p => p.Name + "=" + p.GetValue(v)))) + ")";
    if (v.GetType().IsPrimitive || v.GetType().IsEnum || props.Length == 0) return v.ToString();
    return v.GetType().Name + "(" + string.Join(" ", props.Select(p => p.Name + "=" + p.GetValue(v))) + ")";
  }
  static void Main(string[] a) {
    string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var f = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(f) ? Assembly.LoadFrom(f) : null; };
    var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
    var store = asm.GetTypes().First(t => t.Name == "GameDataStore");
    var m = store.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(q => q.Name.Contains("DeserializeRdbObject") && q.GetParameters().Length == 2 && q.GetParameters()[0].ParameterType == typeof(byte[]));
    var dynelsType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } }).First(t => t.Name == "PlayfieldDynels");
    if (a[0] == "listpf") { ListPf.Run(int.Parse(a[1]), int.Parse(a[2])); return; }
    if (a[0] == "scan") { Scan.Run(a.Skip(1).Select(int.Parse).ToArray()); return; }
    int pf = int.Parse(a[0]), type = int.Parse(a[1]), inst = int.Parse(a[2]);
    string path = $@"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData\Playfields\{pf}\Dynels.dat";
    object d = m.MakeGenericMethod(dynelsType).Invoke(null, new object[] { File.ReadAllBytes(path), path });
    foreach (var x in (IEnumerable)Get(d, "Dynels")) {
      if (Convert.ToInt32(Get(x, "IdentityType")) != type || Convert.ToInt32(Get(x, "IdentityInstance")) != inst) continue;
      Console.WriteLine($"tpl={Get(x,"TemplateId")} pos={Get(x,"Position")}");
      var mods = (IDictionary)Get(x, "Modifiers");
      if (mods == null) { Console.WriteLine("no modifiers"); continue; }
      foreach (var ev in mods.Keys) {
        Console.WriteLine("event " + ev);
        var mod = mods[ev]; var fns = (IDictionary)Get(mod, "Modifiers");
        foreach (var fn in fns.Keys) foreach (var set in (IEnumerable)fns[fn]) Console.WriteLine("  " + fn + " " + Show(set));
      }
    }
  }
}
