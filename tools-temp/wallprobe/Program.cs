using System;
using System.IO;
using System.Text;
using AODB.Common.RDBObjects;

static class Program
{
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] b = File.ReadAllBytes(@"c:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\GameData\Playfields\4310\Doors.dat");
        Console.WriteLine("len=" + b.Length + " " + BitConverter.ToString(b, 0, Math.Min(24, b.Length)));
        for (int skip = 0; skip <= 32; skip++)
        {
            try
            {
                using var ms = new MemoryStream(b, skip, b.Length - skip);
                using var br = new BinaryReader(ms);
                var d = new PlayfieldDoors();
                d.Deserialize(br);
                long rem = ms.Length - ms.Position;
                int count = d.Doors?.Count ?? -1;
                Console.WriteLine("skip=" + skip + " doors=" + count + " rem=" + rem);
            }
            catch (Exception e)
            {
                if (skip % 4 == 0)
                    Console.WriteLine("skip=" + skip + " FAIL " + e.GetType().Name);
            }
        }
    }
}
