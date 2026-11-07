namespace DungeonObjDump
{
    using System;
    using System.IO;

    using AORebirth.World.Collision;

    using SmokeLounge.AOtomation.Messaging.GameData;

    internal static class Program
    {
        const string PacketHex =
            "0002000A000100AF00000DAF71A954685F4B1A3900009C500020D30700000000043FE6870040A051EC41C81480610000C7A1C00A021C000000000000000000009C500020D3070000C7A1C00A021C000000010003000A00050040000001446464640000000B002300000200002F00010101001A00050101001300060303000500040302000D00030000000A00020302001500060403002500030401000600060000000A00070101FFFFFFFFFFFFFFFF";

        static int Main()
        {
            string repoRoot = FindRepoRoot();
            string gameDataRoot = Path.Combine(repoRoot, "AORebirth", "GameData");
            string outPath = Path.Combine(repoRoot, "artifacts", "dungeon-c7a1-style324.obj");

            byte[] packet = Convert.FromHexString(PacketHex);
            byte[] generator = ExtractGenerator(packet);
            if (!AcgBuildingGeneratorData.TryParse(generator, out AcgBuildingGeneratorData? data) || data == null)
            {
                Console.Error.WriteLine("ACG generator blob did not parse.");
                return 2;
            }

            Console.WriteLine(
                "style={0} size={1}x{2} floorHeight={3} rooms={4}",
                data.Style,
                data.Width,
                data.Height,
                data.RoomsPerFloor,
                data.Rooms.Length);

            PlayfieldCollisionSet set = DungeonCollisionBuilder.Build(gameDataRoot, data);
            CollisionObjDumper.DumpObj(set, outPath);
            Console.WriteLine("meshes={0} obj={1}", set.SurfaceMeshes.Count, outPath);
            return 0;
        }

        static byte[] ExtractGenerator(byte[] packet)
        {
            for (int i = 0; i <= packet.Length - 16; i++)
            {
                bool acg = packet[i] == 0
                    && packet[i + 1] == 0
                    && packet[i + 2] == 0xC7
                    && (packet[i + 3] == 0xA1 || packet[i + 3] == 0x9F);
                if (!acg)
                    continue;
                if (packet[i + 12] != 0 || packet[i + 13] != 3)
                    continue;

                int end = packet.Length;
                if (end >= 8
                    && packet[end - 8] == 0xFF
                    && packet[end - 7] == 0xFF
                    && packet[end - 6] == 0xFF
                    && packet[end - 5] == 0xFF)
                    end -= 8;

                byte[] blob = new byte[end - i];
                Buffer.BlockCopy(packet, i, blob, 0, blob.Length);
                return blob;
            }

            throw new InvalidDataException("No C79F/C7A1 v3 generator blob was found.");
        }

        static string FindRepoRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "AGENTS.md")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException(
                "Could not locate repository root from " + AppContext.BaseDirectory);
        }
    }
}
