using System.Reflection;
// args: assemblySimpleName typeFullName...
if (args[0] == "n3") { N3.Run(); return; }
if (args[0] == "nano") { Nano.Run(args[1], int.Parse(args[2]), int.Parse(args[3])); return; }
if (args[0] == "shop") { Shop.Run(args[1], args.Skip(2).Select(int.Parse).ToArray()); return; }
var asm = AppDomain.CurrentDomain.GetAssemblies().Concat(new[]{ Assembly.Load(args[0]) }).First(a => a.GetName().Name == args[0]);
foreach (string n in args.Skip(1))
{
    var t = asm.GetType(n) ?? asm.GetTypes().FirstOrDefault(x => x.FullName.EndsWith(n));
    if (t == null) { Console.WriteLine("== " + n + " NOT FOUND"); continue; }
    Console.WriteLine($"== {t.FullName} valueType={t.IsValueType} base={t.BaseType?.Name}");
    foreach (var c in t.GetConstructors()) Console.WriteLine("  ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    foreach (var f in t.GetFields()) Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
    foreach (var p in t.GetProperties()) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
}
