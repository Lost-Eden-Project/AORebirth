namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.Json;

    using AORebirth.Core.GameData;

    /// <summary>
    /// GameData <c>PlayfieldContent\{id}\DoorLandings.json</c>: where a character arriving at one of this playfield's
    /// doors stands, for doors whose computed spot in front of the frame is unusable (Camelot's door C01F0078 puts
    /// arrivals inside its staircase). Every door landing, whether a walk-in portal, an exit back out or an OnUse proxy,
    /// reads it through <see cref="PortalDoorLandingResolver"/>; the arrival keeps the door's heading.
    /// <code>
    /// { "SchemaVersion": 1, "PlayfieldId": 120,
    ///   "Doors": [ { "Instance": "C01F0078", "X": 218.0, "Y": 23.4, "Z": 187.5 } ] }
    /// </code>
    /// A missing file overrides nothing. A malformed one fails the playfield load, since dropping it silently would put
    /// players back inside the geometry it exists to avoid.
    /// </summary>
    public static class DoorLandingCatalog
    {
        public const string FileName = "DoorLandings.json";

        public const int SupportedSchemaVersion = 1;

        static readonly JsonDocumentOptions JsonOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static string RelativePath(int playfieldId)
            => Path.Combine(GameDataPaths.PlayfieldContentRelativeDirectory(playfieldId), FileName);

        /// <summary>The playfield's door landings, or null when it configures none.</summary>
        public static IReadOnlyDictionary<int, (float X, float Y, float Z)>? Load(string root, int playfieldId)
        {
            string path = GameDataPaths.ResolveFile(root, RelativePath(playfieldId));
            if (!File.Exists(path))
                return null;

            try
            {
                return Parse(File.ReadAllText(path), playfieldId);
            }
            catch (Exception exception) when (exception is JsonException
                                                  or FormatException
                                                  or InvalidOperationException
                                                  or KeyNotFoundException)
            {
                throw new InvalidDataException("Invalid " + FileName + " at " + path + ": " + exception.Message, exception);
            }
        }

        public static Dictionary<int, (float X, float Y, float Z)> Parse(string json, int playfieldId)
        {
            using JsonDocument document = JsonDocument.Parse(json, JsonOptions);
            JsonElement root = document.RootElement;

            int schema = root.GetProperty("SchemaVersion").GetInt32();
            if (schema != SupportedSchemaVersion)
                throw new FormatException("SchemaVersion " + schema.ToString(CultureInfo.InvariantCulture) + " is not supported");

            int declared = root.GetProperty("PlayfieldId").GetInt32();
            if (declared != playfieldId)
            {
                throw new FormatException(
                    "PlayfieldId " + declared.ToString(CultureInfo.InvariantCulture)
                    + " does not match playfield " + playfieldId.ToString(CultureInfo.InvariantCulture));
            }

            var landings = new Dictionary<int, (float X, float Y, float Z)>();
            int index = 0;
            foreach (JsonElement entry in root.GetProperty("Doors").EnumerateArray())
            {
                string at = "door " + index++.ToString(CultureInfo.InvariantCulture) + ": ";
                string text = entry.GetProperty("Instance").GetString() ?? string.Empty;
                if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint instance))
                    throw new FormatException(at + "Instance '" + text + "' is not a hex instance");

                float x = entry.GetProperty("X").GetSingle();
                float y = entry.GetProperty("Y").GetSingle();
                float z = entry.GetProperty("Z").GetSingle();
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new FormatException(at + "landing for " + text + " is not a finite position");

                if (!landings.TryAdd(unchecked((int)instance), (x, y, z)))
                    throw new FormatException(at + "door " + text + " is listed twice");
            }

            return landings;
        }
    }
}
