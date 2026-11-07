using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
static class ListPf
{
    static object Get(object o, string n) { var t = o.GetType(); var p = t.GetProperty(n); if (p != null) return p.GetValue(o); return t.GetField(n)?.GetValue(o); }
    public static void Run(int pf, int type)
    {
        string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
        var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
        var store = asm.GetTypes().First(t => t.Name == "GameDataStore");
        var m = store.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(q => q.Name.Contains("DeserializeRdbObject") && q.GetParameters().Length == 2 && q.GetParameters()[0].ParameterType == typeof(byte[]));
        var dt = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } }).First(t => t.Name == "PlayfieldDynels");
        string path = @"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData\Playfields\" + pf + @"\Dynels.dat";
        object d = m.MakeGenericMethod(dt).Invoke(null, new object[] { File.ReadAllBytes(path), path });
        foreach (var x in (IEnumerable)Get(d, "Dynels"))
        {
            if (Convert.ToInt32(Get(x, "IdentityType")) != type) continue;
            Console.WriteLine($"pf={pf} type={type} inst={Get(x, "IdentityInstance")} tpl={Get(x, "TemplateId")} pos={Get(x, "Position")}");
        }
    }
}
