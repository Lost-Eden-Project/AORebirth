#r "c:\Users\tyler\OneDrive\Documents\AORebirth\tools-temp\aodb-nupkg\lib\net10.0\AODB.Common.dll"
using System;
using System.Reflection;
using System.Linq;

var asm = typeof(AODB.Common.RDBObjects.PlayfieldWalls).Assembly;
foreach (var t in asm.GetTypes().Where(x => x.Name.Contains("Wall") || x.Name == "Tilemap").OrderBy(x => x.FullName))
{
    Console.WriteLine("=== " + t.FullName + " ===");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine("  P " + p.Name + " : " + p.PropertyType);
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine("  F " + f.Name + " : " + f.FieldType);
}
