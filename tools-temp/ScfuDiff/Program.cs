using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;

// args: log [nameFilter]  - with a filter, the first matching SimpleCharFullUpdate as JSON plus its raw hex
class P
{
    static void Main(string[] args)
    {
        string? filter = args.Length > 1 ? args[1] : null;
        foreach (string line in File.ReadLines(args[0]))
        {
            string[] parts = line.Split(' ');
            if (parts.Length < 4 || parts[1] != "RX" || !parts[3].Contains("271B3A6B")) continue;
            try
            {
                var m = new MessageSerializer().Deserialize(new MemoryStream(Convert.FromHexString(parts[3])));
                if (m.Body is not SimpleCharFullUpdateMessage s) continue;
                if (filter == null)
                {
                    Console.WriteLine($"{s.Name,-24} npc={(((int)s.Flags) & 1)} flags=0x{(int)s.Flags:X8} charFlags=0x{(int)s.CharacterFlags:X8}");
                    continue;
                }

                if (s.Name == null || !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("HEX " + parts[3]);
                Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                return;
            }
            catch (Exception e) { Console.WriteLine("ERR " + e.Message); }
        }
    }
}
