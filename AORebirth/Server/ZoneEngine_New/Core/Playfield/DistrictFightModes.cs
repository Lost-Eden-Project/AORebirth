namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    using AORebirth.Core.GameData;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The client's FightTypeAllowed_e, a district's suppression gas. Gamecode.dll
    /// N3Msg_HasVulnerableFightMode (0x10017052) decides who may be attacked from it.
    /// </summary>
    public enum FightMode
    {
        /// <summary>100% gas: no fighting.</summary>
        NoFight = 0,

        /// <summary>75% gas: players only when PvP-flagged.</summary>
        Normal = 1,

        /// <summary>25% gas: players of another side.</summary>
        Side = 2,

        /// <summary>5% gas: players of another organization.</summary>
        Organization = 3,

        /// <summary>0% gas: every player.</summary>
        All = 4,
    }

    /// <summary>
    /// Each zone's fight mode, from the playfield's Districts.json. A zone is an outdoor 40 m cell or an indoor room,
    /// which is what the client looks up (Gamecode.dll 0x1003e388: GetZoneInstanceID, then GetDistrictData).
    /// Tower fields change a district's mode at runtime; that layer is not here.
    /// </summary>
    public sealed class DistrictFightModes
    {
        /// <summary>The client's mode when a zone has no district.</summary>
        const FightMode UnknownDistrict = FightMode.Side;

        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>PvP grace on entering a land-control district (Gamecode.dll 0x1003ecff).</summary>
        public const int LandControlGraceSeconds = 60;

        /// <summary>PvP grace on entering any other district, or a zone with no district.</summary>
        public const int GraceSeconds = 15;

        readonly byte[]? _zoneToDistrict;
        readonly Dictionary<int, FightMode> _byDistrict;
        readonly HashSet<int> _landControl;
        readonly FightMode? _uniform;

        /// <summary>No district data: everywhere is <see cref="FightMode.Normal"/>.</summary>
        public static readonly DistrictFightModes None = new(null, new Dictionary<int, FightMode>(), [], FightMode.Normal);

        DistrictFightModes(byte[]? zoneToDistrict, Dictionary<int, FightMode> byDistrict, HashSet<int> landControl,
            FightMode? uniform)
        {
            _zoneToDistrict = zoneToDistrict;
            _byDistrict = byDistrict;
            _landControl = landControl;
            _uniform = uniform;
        }

        /// <summary>
        /// The mode in <paramref name="zone"/>. A playfield whose districts all share one mode has it everywhere,
        /// so rooms the server does not resolve still get the right one.
        /// </summary>
        public FightMode At(int zone)
        {
            if (_uniform is FightMode uniform)
                return uniform;

            return _byDistrict.TryGetValue(DistrictOf(zone), out FightMode mode) ? mode : UnknownDistrict;
        }

        /// <summary>
        /// How long the PvP grace lasts on entering <paramref name="zone"/>: <see cref="LandControlGraceSeconds"/> in a
        /// district with a land-control level (DistrictData +0x44 above 0), else <see cref="GraceSeconds"/>.
        /// </summary>
        public int GraceSecondsAt(int zone)
            => _landControl.Contains(DistrictOf(zone)) ? LandControlGraceSeconds : GraceSeconds;

        int DistrictOf(int zone)
            => _zoneToDistrict is { } map ? (zone >= 0 && zone < map.Length ? map[zone] : -1) : zone;

        public static DistrictFightModes Load(string root, int playfieldId, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            string path = Path.Combine(root, GameDataPaths.PlayfieldDistrictsRelativePath(playfieldId));
            if (!File.Exists(path))
                return None;

            PlayfieldDistrictsData? data;
            try
            {
                data = JsonSerializer.Deserialize<PlayfieldDistrictsData>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to read " + path);
                return None;
            }

            if (data?.Districts == null || data.Districts.Length == 0)
                return None;

            var byDistrict = new Dictionary<int, FightMode>(data.Districts.Length);
            var landControl = new HashSet<int>();
            FightMode? uniform = null;
            bool mixed = false;
            foreach (PlayfieldDistrictEntry district in data.Districts)
            {
                if (district == null)
                    continue;

                // The client clamps the mode into 0..4.
                var mode = (FightMode)Math.Clamp(district.FightMode, (int)FightMode.NoFight, (int)FightMode.All);
                byDistrict[district.DistrictIndex] = mode;
                if (district.LandControlMinLevel > 0)
                    landControl.Add(district.DistrictIndex);
                if (uniform == null)
                    uniform = mode;
                else if (uniform != mode)
                    mixed = true;
            }

            return new DistrictFightModes(data.ZoneToDistrictMap, byDistrict, landControl, mixed ? null : uniform);
        }
    }
}
