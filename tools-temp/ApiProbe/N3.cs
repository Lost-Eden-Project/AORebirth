using System.Reflection;
static class N3 {
  public static void Run() {
    var asm = typeof(N3Lite.CharVehicleSim).Assembly;
    foreach (var t in asm.GetTypes()) {
      foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)) {
        if (f.IsLiteral) Console.WriteLine($"CONST {t.FullName}.{f.Name} = {f.GetRawConstantValue()}");
      }
      foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        if (m.Name.Contains("Speed", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Fly", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Vel", StringComparison.OrdinalIgnoreCase))
          Console.WriteLine($"METHOD {t.FullName}::{m.Name}");
    }
  }
}
