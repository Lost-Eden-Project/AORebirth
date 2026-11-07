using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
class P {
  static object Get(object o, string n) { var t = o.GetType(); var p = t.GetProperty(n); if (p != null) return p.GetValue(o); return t.GetField(n)?.GetValue(o); }
  static string H(uint h) => new string(new[]{(char)(h>>24),(char)((h>>16)&255),(char)((h>>8)&255),(char)(h&255)});
  static void Main(string[] a) {
    string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var f = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(f) ? Assembly.LoadFrom(f) : null; };
    var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
    var reader = asm.GetTypes().First(t => t.Name == "ItemsDatReader");
    var list = (IList)reader.GetMethod("Read", BindingFlags.Public|BindingFlags.Static).Invoke(null, new object[]{ @"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData\items.dat" });
    var want = a.Select(int.Parse).ToHashSet();
    int shops=0, withInv=0, total=0; var amounts = new System.Collections.Generic.Dictionary<int,int>(); var regens=new System.Collections.Generic.Dictionary<int,int>(); var intervals=new System.Collections.Generic.Dictionary<int,int>(); int zeroQl=0, extraSet=0, costNonZero=0;
    foreach (var it in list) {
      int id = (int)Get(it,"ID"); var rdb = Get(it,"Rdb"); var inv = rdb == null ? null : (IList)Get(rdb,"ShopInventory"); int type = Convert.ToInt32(Get(it,"ItemType"));
      int dyn = Convert.ToInt32(Get(it,"DynelType")); if (dyn == 51035) { shops++; if (inv != null && inv.Count>0) withInv++; }
      if (inv == null || inv.Count == 0) continue;
      foreach (var s in inv) { total++; int am=(int)Get(s,"Amount"), rg=(int)Get(s,"Regen"), iv=(int)Get(s,"Interval"); amounts[am]=amounts.GetValueOrDefault(am)+1; regens[rg]=regens.GetValueOrDefault(rg)+1; intervals[iv]=intervals.GetValueOrDefault(iv)+1; if ((int)Get(s,"MinQl")==0 && (int)Get(s,"MaxQl")==0) zeroQl++; if (Get(s,"Extra")!=null) extraSet++; if ((uint)Get(s,"Cost")!=0) costNonZero++; }
      if (want.Contains(id)) {
        Console.WriteLine($"== {id} {Get(it,"Name")} type={type} entries={inv.Count}");
        foreach (var s in inv) Console.WriteLine($"  hash={H((uint)Get(s,"Hash"))} ql={Get(s,"MinQl")}-{Get(s,"MaxQl")} amount={Get(s,"Amount")} regen={Get(s,"Regen")} extra={Get(s,"Extra")} interval={Get(s,"Interval")} u1={Get(s,"Unknown1")} u2={Get(s,"Unknown2")} cost={Get(s,"Cost")}");
      }
    }
    if (Environment.GetEnvironmentVariable("DUMP_HASHES") is string hp) {
      var hs = new HashSet<string>(); int shown=0;
      foreach (var it in list) { var rdb = Get(it,"Rdb"); var inv = rdb == null ? null : (IList)Get(rdb,"ShopInventory"); if (inv==null) continue;
        foreach (var s in inv) { hs.Add(H((uint)Get(s,"Hash"))); if ((int)Get(s,"MinQl")==0 && (int)Get(s,"MaxQl")==0 && shown++<8) Console.WriteLine($"zeroQl shop={Get(it,"ID")} {Get(it,"Name")} hash={H((uint)Get(s,"Hash"))} extra={Get(s,"Extra")} ({(Get(s,"Extra") is uint ux ? H(ux) : "")}) amount={Get(s,"Amount")} u1={Get(s,"Unknown1")} cost={Get(s,"Cost")}"); } }
      File.WriteAllLines(hp, hs.OrderBy(x=>x));
    }
    Console.WriteLine($"shop items={shops} withInventory={withInv} entries={total} zeroQl={zeroQl} extraSet={extraSet} costNonZero={costNonZero}");
    Console.WriteLine("amount: " + string.Join(" ", amounts.OrderByDescending(k=>k.Value).Take(8).Select(k=>$"{k.Key}x{k.Value}")));
    Console.WriteLine("regen: " + string.Join(" ", regens.OrderByDescending(k=>k.Value).Take(8).Select(k=>$"{k.Key}x{k.Value}")));
    Console.WriteLine("interval: " + string.Join(" ", intervals.OrderByDescending(k=>k.Value).Take(8).Select(k=>$"{k.Key}x{k.Value}")));
  }
}
