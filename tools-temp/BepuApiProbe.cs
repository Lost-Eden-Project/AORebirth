using System;
using System.Linq;
using System.Reflection;
class P {
  static void Main() {
    Assembly.LoadFrom(@"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\d35a7f5605fbccd52726bb1f090c4866\nuget\bepuutilities\2.5.0-beta.25\lib\net8.0\BepuUtilities.dll");
    var a = Assembly.LoadFrom(@"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\d35a7f5605fbccd52726bb1f090c4866\nuget\bepuphysics\2.5.0-beta.25\lib\net8.0\BepuPhysics.dll");
    foreach (var t in a.GetExportedTypes().Where(x => x.Name is "Simulation" or "IRayHitHandler" or "ISweepHitHandler")) {
      Console.WriteLine("TYPE " + t.FullName);
      foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m => m.Name.Contains("Sweep") || m.Name.Contains("Ray") || m.Name.Contains("Hit") || m.Name.Contains("Allow") || m.Name.StartsWith("On")))
        Console.WriteLine("  " + m);
    }
  }
}
