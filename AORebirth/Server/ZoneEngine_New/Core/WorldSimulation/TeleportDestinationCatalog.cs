namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.Json;
    using AODB.Common.Enums;
    using AODB.Common.RDBObjects;
    using AORebirth.Core.GameData;
    using SmokeLounge.AOtomation.Messaging.GameData;
    using ZoneEngine_New.Core.Logging;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>One TeleportRoutes.json route: a placed statel and where using it sends the character.</summary>
    public sealed class TeleportRoute
    {
        public int Playfield { get; set; }
        public int StatelType { get; set; }
        public uint StatelInstance { get; set; }
        public int DestinationPlayfield { get; set; }
        public int DestinationType { get; set; }
        public uint DestinationInstance { get; set; }

        /// <summary>Landing on <see cref="DestinationPlayfield"/>; null for door routes.</summary>
        public float? DestinationX { get; set; }

        public float? DestinationY { get; set; }

        public float? DestinationZ { get; set; }

        /// <summary>A landing route that also makes the landing the character's save point.</summary>
        public bool SetsSavePoint { get; set; }
    }

    /// <summary>
    /// The server's routing overrides from GameData TeleportRoutes.json, applied before portal and reverse-exit baking.
    /// A route names its statel by playfield, identity type name and hex instance, then either a destination door, a
    /// landing (DestinationPlayfield with X/Y/Z; SetSavePoint also saves the character there, as an insurance terminal
    /// does), or Disabled to switch the statel's raw destination off:
    /// <code>
    /// { "Routes": [
    ///   { "Playfield": 605, "Type": "Door", "Instance": "C001025D", "DestinationPlayfield": 1328, "DestinationType": "Door", "DestinationInstance": "C0000530" },
    ///   { "Playfield": 4833, "Type": "Terminal", "Instance": "C00412E1", "DestinationPlayfield": 800, "X": 692.0, "Y": 72.8, "Z": 476.8, "SetSavePoint": true },
    ///   { "Playfield": 655, "Type": "Door", "Instance": "C00E028F", "Disabled": true } ] }
    /// </code>
    /// </summary>
    public sealed class TeleportDestinationCatalog
    {
        static readonly JsonDocumentOptions JsonOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        readonly Dictionary<(int Playfield, int Type, uint Instance), (int Playfield, int Type, uint Instance)> _routes = new();
        readonly Dictionary<(int Playfield, int Type, uint Instance), (int Playfield, float X, float Y, float Z, bool SetsSavePoint)> _landings = new();
        readonly Action<string>? _warn;

        public TeleportDestinationCatalog(IEnumerable<TeleportRoute> rows, Action<string>? warn = null)
        {
            ArgumentNullException.ThrowIfNull(rows);
            _warn = warn;
            foreach (TeleportRoute row in rows)
            {
                var key = (row.Playfield, row.StatelType, row.StatelInstance);
                if (row.DestinationX is float x && row.DestinationY is float y && row.DestinationZ is float z)
                {
                    var landing = (row.DestinationPlayfield, x, y, z, row.SetsSavePoint);
                    if (_landings.TryGetValue(key, out var existingLanding) && existingLanding != landing)
                        throw new InvalidDataException($"Conflicting teleport landings for {key}.");
                    _landings[key] = landing;
                    continue;
                }

                var target = (row.DestinationPlayfield, row.DestinationType, row.DestinationInstance);
                if (_routes.TryGetValue(key, out var existing) && existing != target)
                    throw new InvalidDataException($"Conflicting teleport routes for {key}.");
                _routes[key] = target;
            }
        }

        public int Count => _routes.Count + _landings.Count;

        /// <summary>
        /// Reads TeleportRoutes.json through the GameData override folder. A missing file routes nothing; a malformed
        /// one stops startup, since silently dropping every route would send players through the raw RDB doors.
        /// </summary>
        public static TeleportDestinationCatalog Load(string root, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            string path = GameDataPaths.ResolveFile(root, GameDataPaths.TeleportRoutesFileName);
            if (!File.Exists(path))
            {
                logger.Warn("TeleportRoutes.json not found at " + path + "; no teleport routes");
                return new TeleportDestinationCatalog(Array.Empty<TeleportRoute>(), logger.Warn);
            }

            TeleportDestinationCatalog catalog;
            try
            {
                catalog = new TeleportDestinationCatalog(Parse(File.ReadAllText(path)), logger.Warn);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or ArgumentException)
            {
                throw new InvalidDataException("Invalid TeleportRoutes.json at " + path + ": " + exception.Message, exception);
            }

            logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData teleport routes={0} from {1}", catalog.Count, path));
            return catalog;
        }

        public static List<TeleportRoute> Parse(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json, JsonOptions);
            var routes = new List<TeleportRoute>();
            int index = 0;
            foreach (JsonElement entry in document.RootElement.GetProperty("Routes").EnumerateArray())
            {
                string at = "route " + index++.ToString(CultureInfo.InvariantCulture) + ": ";
                var route = new TeleportRoute
                {
                    Playfield = entry.GetProperty("Playfield").GetInt32(),
                    StatelType = ParseType(entry, "Type", at),
                    StatelInstance = ParseInstance(entry, "Instance", at)
                };

                if (entry.TryGetProperty("Disabled", out JsonElement disabled) && disabled.GetBoolean())
                {
                    routes.Add(route);
                    continue;
                }

                route.DestinationPlayfield = entry.GetProperty("DestinationPlayfield").GetInt32();
                if (entry.TryGetProperty("X", out JsonElement x))
                {
                    route.DestinationX = x.GetSingle();
                    route.DestinationY = entry.GetProperty("Y").GetSingle();
                    route.DestinationZ = entry.GetProperty("Z").GetSingle();
                    route.SetsSavePoint = entry.TryGetProperty("SetSavePoint", out JsonElement save) && save.GetBoolean();
                }
                else
                {
                    route.DestinationType = ParseType(entry, "DestinationType", at);
                    route.DestinationInstance = ParseInstance(entry, "DestinationInstance", at);
                }

                routes.Add(route);
            }

            return routes;
        }

        static int ParseType(JsonElement entry, string name, string at)
        {
            string text = entry.GetProperty(name).GetString() ?? string.Empty;
            if (!Enum.TryParse(text, ignoreCase: false, out IdentityType type) || int.TryParse(text, out _))
                throw new FormatException(at + name + " '" + text + "' is not an identity type name");
            return (int)type;
        }

        static uint ParseInstance(JsonElement entry, string name, string at)
        {
            string text = entry.GetProperty(name).GetString() ?? string.Empty;
            if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint instance))
                throw new FormatException(at + name + " '" + text + "' is not a hex instance");
            return instance;
        }

        /// <summary>A route that lands on coordinates of <paramref name="destinationPlayfield"/> rather than at a door.</summary>
        public bool TryGetLanding(
            int playfield,
            int statelType,
            uint statelInstance,
            out int destinationPlayfield,
            out Vector3 landing,
            out bool setsSavePoint)
        {
            if (_landings.TryGetValue((playfield, statelType, statelInstance), out var target))
            {
                destinationPlayfield = target.Playfield;
                landing = new Vector3(target.X, target.Y, target.Z);
                setsSavePoint = target.SetsSavePoint;
                return true;
            }

            destinationPlayfield = 0;
            landing = default!;
            setsSavePoint = false;
            return false;
        }

        public bool TryGet(
            int playfield,
            int statelType,
            uint statelInstance,
            out int destinationPlayfield,
            out int destinationType,
            out uint destinationInstance)
        {
            if (_routes.TryGetValue((playfield, statelType, statelInstance), out var target))
            {
                destinationPlayfield = target.Playfield;
                destinationType = target.Type;
                destinationInstance = target.Instance;
                return true;
            }

            destinationPlayfield = 0;
            destinationType = 0;
            destinationInstance = 0;
            return false;
        }

        public void Apply(int sourcePlayfieldId, PlayfieldDynels? dynels)
        {
            if (dynels?.Dynels == null) return;
            foreach (PlayfieldDynel dynel in dynels.Dynels)
            {
                if (!_routes.TryGetValue((sourcePlayfieldId, dynel.IdentityType, unchecked((uint)dynel.IdentityInstance)), out var target)
                    || dynel.Modifiers == null) continue;
                foreach (Modifier modifier in dynel.Modifiers.Values)
                {
                    if (modifier?.Modifiers == null) continue;
                    foreach (FunctionType function in new[] { FunctionType.TeleportProxy, FunctionType.TeleportProxy2 })
                    {
                        if (!modifier.Modifiers.TryGetValue(function, out var sets)) continue;
                        foreach (var arguments in sets)
                        {
                            if (!arguments.TryGetValue(FunctionOperator.Arg1, out object? raw)
                                || raw is not IList values || values.Count < 3
                                || Convert.ToInt32(values[0]) != (int)IdentityType.PlayfieldDoor) continue;
                            // The route names a door, so drop any TeleportProxy2 destination line.
                            while (!values.IsFixedSize && values.Count > 4) values.RemoveAt(values.Count - 1);
                            if (target.Type != (int)IdentityType.Door || target.Playfield <= 0 || target.Playfield > 0xFFFF
                                || (target.Instance & 0xFF000000u) != 0xC0000000u
                                || (target.Instance & 0xFFFFu) != (uint)target.Playfield)
                            {
                                // Zero destinations disable a route. Other unsupported targets must
                                // also fail locally, without reverting to the wrong raw RDB door.
                                values[1] = 0;
                                values[2] = 0;
                                if (target.Playfield != 0 || target.Type != 0 || target.Instance != 0)
                                    _warn?.Invoke($"Teleport route disabled: unsupported target sourcePf={sourcePlayfieldId} door={dynel.IdentityInstance:X8} destinationPf={target.Playfield} destinationType={target.Type} destination={target.Instance:X8}");
                                continue;
                            }
                            // Preserve the original event, proxy kind, clearance and return behavior.
                            values[1] = target.Playfield;
                            values[2] = (int)((target.Instance >> 16) & 0xFFu);
                        }
                    }
                }
            }
        }
    }
}
