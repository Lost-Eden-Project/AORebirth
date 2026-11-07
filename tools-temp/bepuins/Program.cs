using System;
using System.Linq;
using System.Reflection;
class Program {
  static void Main() {
    var util = Assembly.LoadFrom(@"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\d35a7f5605fbccd52726bb1f090c4866\nuget\bepuutilities\2.5.0-beta.25\lib\net8.0\BepuUtilities.dll");
    var a = Assembly.LoadFrom(@"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\d35a7f5605fbccd52726bb1f090c4866\nuget\bepuphysics\2.5.0-beta.25\lib\net8.0\BepuPhysics.dll");
    foreach (var t in a.GetExportedTypes().Where(x => x.Name == "Simulation")) {
      foreach (var m in t.GetMethods().Where(x => x.Name.Contains("Sweep") || x.Name.Contains("RayCast"))) {
        Console.WriteLine(m);
      }
    }
    foreach (var t in a.GetExportedTypes().Where(x => x.Name.Contains("HitHandler"))) {
      Console.WriteLine("TYPE " + t.FullName);
      foreach (var m in t.GetMethods()) Console.WriteLine("  " + m);
    }
  }
}
