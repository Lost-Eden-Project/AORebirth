using System;
using System.IO;
using System.Reflection;

static class Program
{
    static void Main()
    {
        string dll = @"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\0ef8f5acc19ad274fcbda20766272962\nuget\aodb\1.0.7\lib\net10.0\AODB.Common.dll";
        Assembly asm = Assembly.LoadFrom(dll);
        Type playfieldType = asm.GetType("AODB.Common.RDBObjects.RDBPlayfield")!;
        PropertyInfo destProp = playfieldType.GetProperty("Destinations")!;

        foreach (int pf in new[] { 3081, 610, 800, 954 })
        {
            string path = @"AORebirth\GameData\Playfields\" + pf + @"\Destinations.dat";
            if (!File.Exists(path))
            {
                Console.WriteLine("pf=" + pf + " MISSING");
                continue;
            }

            byte[] payload = File.ReadAllBytes(path);
            object trial = Activator.CreateInstance(playfieldType)!;
            using var stream = new MemoryStream(payload, 12, payload.Length - 12, writable: false);
            using var reader = new BinaryReader(stream);
            playfieldType.GetMethod("Deserialize", new[] { typeof(BinaryReader) })!
                .Invoke(trial, new object[] { reader });
            var dict = (System.Collections.IDictionary)destProp.GetValue(trial)!;
            Console.WriteLine("=== pf " + pf + " count=" + dict.Count + " ===");
            int ord = 1;
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                int key = Convert.ToInt32(entry.Key);
                object dest = entry.Value!;
                int id = Convert.ToInt32(dest.GetType().GetProperty("DestinationId")!.GetValue(dest));
                int hi = id >> 16;
                int lo = id & 0xFFFF;
                Console.WriteLine(
                    "  foreachOrd=" + ord
                    + " dictKey=" + key
                    + " id=" + id
                    + " id>>16=" + hi
                    + " id&FFFF=" + lo
                    + " matchOrd=" + (hi == ord));
                ord++;
            }
        }
    }
}
