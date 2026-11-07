using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

static class Program
{
    static void Main()
    {
        Probe(
            @"C:\Users\tyler\.nuget\packages\aodb\1.0.6\lib\net10.0\AODB.Common.dll",
            "1.0.6");
        Probe(
            @"C:\Users\tyler\.nuget\packages\aodb\1.0.7\lib\net10.0\AODB.Common.dll",
            "1.0.7");
    }

    static void Probe(string dll, string label)
    {
        Console.WriteLine("=== " + label + " ===");
        var alc = new AssemblyLoadContext(label, isCollectible: true);
        Assembly asm = alc.LoadFromAssemblyPath(dll);
        Type dynelType = asm.GetType("AODB.Common.RDBObjects.PlayfieldDynel")!;
        Type dynelsType = asm.GetType("AODB.Common.RDBObjects.PlayfieldDynels")!;
        Type playfieldType = asm.GetType("AODB.Common.RDBObjects.RDBPlayfield")!;

        Console.WriteLine("PlayfieldDynel:");
        foreach (FieldInfo f in dynelType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine("  F " + f.Name + " : " + f.FieldType.FullName);
        foreach (PropertyInfo p in dynelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine("  P " + p.Name + " : " + p.PropertyType.FullName);

        PropertyInfo destProp = playfieldType.GetProperty("Destinations")!;
        Console.WriteLine("Destinations: " + destProp.PropertyType.FullName);

        DumpDoors(dynelsType, @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\GameData\Playfields\800\Dynels.dat");
        DumpPortalArgs(dynelsType, @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\GameData\Playfields\800\Dynels.dat");
        DumpDest(playfieldType, destProp, @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\GameData\Playfields\3081\Destinations.dat");
        DumpDestRich(playfieldType, destProp, @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\GameData\Playfields\610\Destinations.dat");
        alc.Unload();
    }

    static void DumpPortalArgs(Type dynelsType, string path)
    {
        if (!File.Exists(path)) return;
        byte[] payload = File.ReadAllBytes(path);
        object? best = null;
        long bestRem = long.MaxValue;
        foreach (int offset in new[] { 12, 8, 0 })
        {
            try
            {
                object rec = Activator.CreateInstance(dynelsType)!;
                using var ms = new MemoryStream(payload, offset, payload.Length - offset, false);
                using var br = new BinaryReader(ms);
                dynelsType.GetMethod("Deserialize", new[] { typeof(BinaryReader) })!
                    .Invoke(rec, new object[] { br });
                long rem = ms.Length - ms.Position;
                if (rem < bestRem) { best = rec; bestRem = rem; if (rem == 0) break; }
            }
            catch { }
        }

        IList list = (IList)(dynelsType.GetField("Dynels")?.GetValue(best)
            ?? dynelsType.GetProperty("Dynels")!.GetValue(best))!;
        Console.WriteLine("PF800 portal args sample:");
        int shown = 0;
        for (int i = 0; i < list.Count && shown < 6; i++)
        {
            object d = list[i]!;
            int idType = GetInt(d, "IdentityType");
            if (idType != 51016) continue;
            int idInst = GetInt(d, "IdentityInstance");
            var mods = d.GetType().GetField("Modifiers")?.GetValue(d) as IDictionary
                ?? d.GetType().GetProperty("Modifiers")?.GetValue(d) as IDictionary;
            if (mods == null || mods.Count == 0) continue;
            Console.WriteLine($"  door 0x{idInst:X8}");
            foreach (DictionaryEntry me in mods)
            {
                object? modifier = me.Value;
                if (modifier == null) continue;
                var inner = modifier.GetType().GetField("Modifiers")?.GetValue(modifier) as IDictionary
                    ?? modifier.GetType().GetProperty("Modifiers")?.GetValue(modifier) as IDictionary;
                if (inner == null) continue;
                foreach (DictionaryEntry fe in inner)
                {
                    Console.WriteLine("    fn " + fe.Key);
                    if (fe.Value is not IEnumerable sets) continue;
                    foreach (object? argSet in sets)
                    {
                        if (argSet is not IDictionary dict) continue;
                        foreach (DictionaryEntry a in dict)
                        {
                            if (a.Key.ToString() != "Arg1") continue;
                            if (a.Value is IList args)
                            {
                                var parts = new List<string>();
                                for (int ai = 0; ai < args.Count && ai < 6; ai++)
                                    parts.Add(args[ai]?.ToString() ?? "null");
                                Console.WriteLine("      Arg1=[" + string.Join(", ", parts) + "]");
                            }
                        }
                    }
                }
            }
            shown++;
        }
    }

    static void DumpDestRich(Type playfieldType, PropertyInfo destProp, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("missing rich dest " + path);
            return;
        }

        byte[] payload = File.ReadAllBytes(path);
        object trial = Activator.CreateInstance(playfieldType)!;
        using var stream = new MemoryStream(payload, 12, payload.Length - 12, false);
        using var reader = new BinaryReader(stream);
        playfieldType.GetMethod("Deserialize", new[] { typeof(BinaryReader) })!
            .Invoke(trial, new object[] { reader });
        var dict = (IDictionary)destProp.GetValue(trial)!;
        Console.WriteLine("PF610 destinations count=" + dict.Count + " first keys:");
        int n = 0;
        int ordinal = 1;
        foreach (DictionaryEntry entry in dict)
        {
            object dest = entry.Value!;
            object? id = dest.GetType().GetProperty("DestinationId")?.GetValue(dest)
                ?? dest.GetType().GetField("DestinationId")?.GetValue(dest);
            Console.WriteLine($"  ordinalWouldBe={ordinal} key={entry.Key} DestinationId={id}");
            ordinal++;
            if (++n >= 20) break;
        }
    }

    static void DumpDoors(Type dynelsType, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("missing " + path);
            return;
        }

        byte[] payload = File.ReadAllBytes(path);
        object? best = null;
        long bestRem = long.MaxValue;
        foreach (int offset in new[] { 12, 8, 0 })
        {
            try
            {
                object rec = Activator.CreateInstance(dynelsType)!;
                using var ms = new MemoryStream(payload, offset, payload.Length - offset, false);
                using var br = new BinaryReader(ms);
                dynelsType.GetMethod("Deserialize", new[] { typeof(BinaryReader) })!
                    .Invoke(rec, new object[] { br });
                long rem = ms.Length - ms.Position;
                if (rem < bestRem)
                {
                    best = rec;
                    bestRem = rem;
                    if (rem == 0) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  offset " + offset + ": " + ex.GetBaseException().Message);
            }
        }

        IList list = (IList)(dynelsType.GetField("Dynels")?.GetValue(best)
            ?? dynelsType.GetProperty("Dynels")!.GetValue(best))!;
        Console.WriteLine("PF800 dynels=" + list.Count + " rem=" + bestRem);
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            object d = list[i]!;
            int idType = GetInt(d, "IdentityType");
            if (idType != 51016) continue;
            int idInst = GetInt(d, "IdentityInstance");
            int? inst = TryGetInt(d, "Instance");
            Console.WriteLine(
                $"  Door IdentityInstance=0x{idInst:X8} Instance={(inst.HasValue ? "0x" + inst.Value.ToString("X8") : "n/a")} doorIndex={(idInst >> 16) & 0xFF}");
            if (++n >= 10) break;
        }
    }

    static void DumpDest(Type playfieldType, PropertyInfo destProp, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("missing " + path);
            return;
        }

        byte[] payload = File.ReadAllBytes(path);
        object trial = Activator.CreateInstance(playfieldType)!;
        using var stream = new MemoryStream(payload, 12, payload.Length - 12, false);
        using var reader = new BinaryReader(stream);
        playfieldType.GetMethod("Deserialize", new[] { typeof(BinaryReader) })!
            .Invoke(trial, new object[] { reader });
        var dict = (IDictionary)destProp.GetValue(trial)!;
        Console.WriteLine("PF3081 destinations count=" + dict.Count);
        int n = 0;
        foreach (DictionaryEntry entry in dict)
        {
            object dest = entry.Value!;
            object? id = dest.GetType().GetProperty("DestinationId")?.GetValue(dest)
                ?? dest.GetType().GetField("DestinationId")?.GetValue(dest);
            Console.WriteLine("  key=" + entry.Key + " DestinationId=" + id);
            if (++n >= 12) break;
        }
    }

    static int GetInt(object o, string name)
    {
        FieldInfo? f = o.GetType().GetField(name);
        if (f != null) return Convert.ToInt32(f.GetValue(o));
        return Convert.ToInt32(o.GetType().GetProperty(name)!.GetValue(o));
    }

    static int? TryGetInt(object o, string name)
    {
        FieldInfo? f = o.GetType().GetField(name);
        if (f != null) return Convert.ToInt32(f.GetValue(o));
        PropertyInfo? p = o.GetType().GetProperty(name);
        return p == null ? null : Convert.ToInt32(p.GetValue(o));
    }
}
