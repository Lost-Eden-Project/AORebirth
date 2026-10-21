namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// GameData/WaterExits.json: playfields whose flagged water (Water.dat, e.g. the sea under the Shadowlands zones) zones
    /// a character who goes under its surface, keyed by playfield id, e.g.
    /// <c>{ "4310": { "DestinationPlayfield": 4001, "X": 826, "Y": 43.81, "Z": 822 } }</c>. The landing is the server's
    /// choice, not client data (retail capture 2026-10-20T20:59:06Z: N3Teleport from 4310's sea to 4001 (826, 822)).
    /// An entry with no destination (<c>"DestinationPlayfield"</c> missing, 0 or -1, <see cref="WaterExit.ToSavePoint"/>)
    /// sends the character to its save point instead, so listed water never leaves anyone stuck in it. Loaded once per
    /// GameData root.
    /// </summary>
    public sealed class WaterExitCatalog
    {
        public const string FileName = "WaterExits.json";

        static readonly ConcurrentDictionary<string, WaterExitCatalog> ByRoot = new(StringComparer.OrdinalIgnoreCase);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        readonly Dictionary<int, WaterExit> _exits;

        WaterExitCatalog(Dictionary<int, WaterExit> exits)
        {
            _exits = exits;
        }

        public static WaterExitCatalog For(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            return ByRoot.GetOrAdd(gameDataRoot, Load);
        }

        public bool TryGet(int playfieldId, out WaterExit exit)
        {
            if (_exits.TryGetValue(playfieldId, out WaterExit? found))
            {
                exit = found;
                return true;
            }

            exit = null!;
            return false;
        }

        static WaterExitCatalog Load(string root)
        {
            var exits = new Dictionary<int, WaterExit>();
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path))
                return new WaterExitCatalog(exits);

            Dictionary<string, WaterExit>? file =
                JsonSerializer.Deserialize<Dictionary<string, WaterExit>>(File.ReadAllText(path), JsonOptions);
            foreach (KeyValuePair<string, WaterExit> entry in file ?? new())
            {
                if (entry.Value is not WaterExit exit
                    || !int.TryParse(entry.Key.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
                    || playfieldId <= 0)
                    continue;

                // No destination given: the save point.
                if (exit.DestinationPlayfield <= 0)
                    exit.DestinationPlayfield = WaterExit.ToSavePoint;
                exits[playfieldId] = exit;
            }

            return new WaterExitCatalog(exits);
        }
    }

    /// <summary>Where one playfield's flagged water sends a character.</summary>
    public sealed class WaterExit
    {
        /// <summary>
        /// <see cref="DestinationPlayfield"/> that sends the character to its own save point. A missing or 0 destination
        /// loads as this.
        /// </summary>
        public const int ToSavePoint = -1;

        public int DestinationPlayfield { get; set; }

        public float X { get; set; }

        /// <summary>Ground height at the landing (retail sends Y 1.0 and the destination puts the character on the ground).</summary>
        public float Y { get; set; }

        public float Z { get; set; }

        /// <summary>Water.dat flags a plane needs (any of them) to zone. Retail's zoning sea carries 0x10.</summary>
        public int WaterFlags { get; set; } = 0x10;
    }
}
