using System;
using System.Linq;
using System.Reflection;
class P {
  static void Main() {
    var common = Environment.GetEnvironmentVariable("AODB_COMMON")!;
    var aodbPath = Environment.GetEnvironmentVariable("AODB")!;
    Assembly.LoadFrom(common);
    var a = Assembly.LoadFrom(aodbPath);
    var commonAsm = Assembly.LoadFrom(common);
    foreach (var t in commonAsm.GetExportedTypes().Concat(a.GetExportedTypes()).Where(x => x.Name.Contains("Deser") || x.Name == "RDBObject" || x.Name.Contains("Serializer"))) {
      Console.WriteLine(t.FullName);
      foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly).Take(20))
        Console.WriteLine("  " + m);
    }
  }
}
