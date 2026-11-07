using System;
using System.IO;
using System.Linq;
using System.Reflection;
class P {
  static object Get(object o, string n) { var t = o.GetType(); var p = t.GetProperty(n); if (p != null) return p.GetValue(o); var f = t.GetField(n); return f?.GetValue(o); }
  static void Main(string[] a) {
    string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var f = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(f) ? Assembly.LoadFrom(f) : null; };
    var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
    var store = asm.GetTypes().First(t => t.Name == "GameDataStore");
    var m = store.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(q => q.Name.Contains("DeserializeRdbObject") && q.GetParameters().Length == 2 && q.GetParameters()[0].ParameterType == typeof(byte[]));
    var dynelsType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } }).First(t => t.Name == "PlayfieldDynels");
    foreach (string pfs in a) {
      int pf = int.Parse(pfs);
      string path = $@"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData\Playfields\{pf}\Dynels.dat";
      if (!File.Exists(path)) { Console.WriteLine($"pf={pf} no Dynels.dat"); continue; }
      object d = m.MakeGenericMethod(dynelsType).Invoke(null, new object[] { File.ReadAllBytes(path), path });
      foreach (var x in (System.Collections.IEnumerable)Get(d, "Dynels")) {
        if (Convert.ToInt32(Get(x, "IdentityType")) != 51016) continue;
        byte[] blob = (byte[])Get(x, "Blob");
        string stats = "";
        try { var b = new AODB.Common.RDBObjects.ItemBase().ReadDynelBlob(blob); stats = string.Join(" ", b.AppliedStats.Select(k => k.Key + "=" + k.Value)); } catch (Exception e) { stats = "blob? " + e.GetType().Name; }
        Console.WriteLine($"pf={pf} door={Convert.ToInt32(Get(x,"IdentityInstance")):X8} tpl={Get(x,"TemplateId")} pos={Get(x,"Position")} head={Get(x,"Heading")} {stats}");
      }
    }
  }
}
