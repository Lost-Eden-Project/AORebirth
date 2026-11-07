using System; using System.Collections; using System.IO; using System.Linq; using System.Reflection;
static class Scan {
  static object Get(object o, string n) { var t = o.GetType(); var p = t.GetProperty(n); if (p != null) return p.GetValue(o); return t.GetField(n)?.GetValue(o); }
  public static void Run(int[] tpls) {
    string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
    var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
    var store = asm.GetTypes().First(t => t.Name == "GameDataStore");
    var m = store.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(q => q.Name.Contains("DeserializeRdbObject") && q.GetParameters().Length == 2 && q.GetParameters()[0].ParameterType == typeof(byte[]));
    var dt = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } }).First(t => t.Name == "PlayfieldDynels");
    foreach (var path in Directory.GetFiles(@"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData\Playfields", "Dynels.dat", SearchOption.AllDirectories)) {
      object d; try { d = m.MakeGenericMethod(dt).Invoke(null, new object[] { File.ReadAllBytes(path), path }); } catch { continue; }
      foreach (var x in (IEnumerable)Get(d, "Dynels")) { int tpl = Convert.ToInt32(Get(x, "TemplateId")); if (tpls.Contains(tpl)) Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))} tpl={tpl} type={Get(x,"IdentityType")} inst={Get(x,"IdentityInstance")} pos={Get(x,"Position")}"); }
    }
  }
}
