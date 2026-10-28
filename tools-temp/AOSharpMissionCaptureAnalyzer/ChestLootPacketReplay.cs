namespace AOSharpMissionCaptureAnalyzer
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Web.Script.Serialization;
    using AORebirth.CaptureProtocol;

    /// <summary>Deterministic offline decoding of a caller's resumable selected-packet index.</summary>
    internal static class ChestLootPacketReplay
    {
        internal static int Run(string inputPath, string outputPath)
        {
            inputPath = Path.GetFullPath(inputPath);
            outputPath = Path.GetFullPath(outputPath);
            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Decoded output must not overwrite its input index.");
            if (string.Equals(inputPath, outputPath + ".summary.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Summary output must not overwrite its input index.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            var json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 128 };
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var errors = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int rows = 0, decoded = 0, failed = 0, outgoingZeroLengths = 0;
            string inputHash = HashFile(inputPath);
            using (var input = new StreamReader(inputPath, Encoding.UTF8, true))
            using (var output = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    rows++;
                    if (string.IsNullOrWhiteSpace(line))
                        throw new InvalidDataException("Empty selected-packet record at line " + rows + ".");
                    Dictionary<string, object> source = json.Deserialize<Dictionary<string, object>>(line);
                    object raw;
                    if (!source.TryGetValue("RawHex", out raw) && !source.TryGetValue("raw_hex", out raw))
                        throw new InvalidDataException("RawHex is absent from selected-packet line " + rows + ".");
                    string rawHex = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    var result = new Dictionary<string, object>();
                    source.Remove("RawHex"); source.Remove("raw_hex");
                    result["Source"] = source;
                    result["InputLine"] = rows;
                    try
                    {
                        byte[] packet = FromHex(rawHex);
                        result["PacketSha256"] = Hash(packet);
                        object direction;
                        if (!source.TryGetValue("Direction", out direction))
                            source.TryGetValue("direction", out direction);
                        bool outgoing = string.Equals(Convert.ToString(direction), "OUT", StringComparison.OrdinalIgnoreCase);
                        Dictionary<string, object> value = RawChestLootPacketDecoder.DecodePacket(packet, outgoing);
                        result["Decoded"] = value;
                        result["Status"] = "DECODED_COMPLETE";
                        string family = (string)value["Family"];
                        counts[family] = counts.ContainsKey(family) ? counts[family] + 1 : 1;
                        if ((string)value["FrameLengthStatus"] != "MATCHED")
                            outgoingZeroLengths++;
                        decoded++;
                    }
                    catch (Exception exception)
                    {
                        result["Status"] = "DECODE_FAILURE";
                        result["Error"] = exception.GetType().Name + ": " + exception.Message;
                        string signature = exception.GetType().Name + ": " + exception.Message;
                        errors[signature] = errors.ContainsKey(signature) ? errors[signature] + 1 : 1;
                        failed++;
                    }
                    output.WriteLine(json.Serialize(result));
                }
            }
            string afterHash = HashFile(inputPath);
            if (inputHash != afterHash)
                throw new InvalidDataException("Selected input index changed during decoding.");
            var summary = new Dictionary<string, object>
            {
                { "Status", failed == 0 ? "PASS_COMPLETE_BYTE_GRAMMAR" : "FAIL_WITH_EXPLICIT_RECORD_ERRORS" },
                { "InputSha256", inputHash }, { "OutputSha256", HashFile(outputPath) },
                { "Rows", rows }, { "Decoded", decoded }, { "Failures", failed },
                { "RecordedOutgoingZeroLengthHeaders", outgoingZeroLengths },
                { "Families", counts }, { "FailureSignatures", errors },
                { "OriginalInputUnchanged", true }
            };
            File.WriteAllText(outputPath + ".summary.json", json.Serialize(summary) + "\n", new UTF8Encoding(false));
            Console.WriteLine("CHEST_LOOT_DECODE rows=" + rows + " decoded=" + decoded + " failures=" + failed);
            Console.WriteLine("CHEST_LOOT_OUTPUT=" + outputPath);
            return failed == 0 ? 0 : 1;
        }

        private static byte[] FromHex(string text)
        {
            if (text == null || text.Length % 2 != 0)
                throw new InvalidDataException("Incomplete packet hex.");
            var bytes = new byte[text.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return bytes;
        }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
