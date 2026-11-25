namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A house's tiles: <see cref="TilesX"/> by <see cref="TilesZ"/>, each covered or not (City.dll HouseLayout_c, its bits
    /// indexed z * tilesInX + x). The house stands on the box from its corner tile.
    /// </summary>
    public sealed class HouseFootprint
    {
        readonly bool[] _cells;

        HouseFootprint(int tilesX, int tilesZ, bool[] cells)
        {
            TilesX = tilesX;
            TilesZ = tilesZ;
            _cells = cells;
            int count = 0;
            foreach (bool cell in cells)
                if (cell)
                    count++;
            OccupiedCount = count;
        }

        public int TilesX { get; }
        public int TilesZ { get; }
        public int OccupiedCount { get; }

        public static HouseFootprint FromBits(int tilesX, int tilesZ, byte[] bits)
        {
            if (tilesX <= 0 || tilesZ <= 0)
                throw new ArgumentOutOfRangeException(nameof(tilesX));
            ArgumentNullException.ThrowIfNull(bits);
            if (bits.Length * 8 < tilesX * tilesZ)
                throw new ArgumentException("Too few layout bits.", nameof(bits));
            var cells = new bool[tilesX * tilesZ];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
            return new HouseFootprint(tilesX, tilesZ, cells);
        }

        /// <summary>A solid footprint, for tests and rectangular houses.</summary>
        public static HouseFootprint Solid(int tilesX, int tilesZ)
        {
            var cells = new bool[tilesX * tilesZ];
            Array.Fill(cells, true);
            return new HouseFootprint(tilesX, tilesZ, cells);
        }

        public bool IsOccupied(int x, int z)
            => (uint)x < (uint)TilesX && (uint)z < (uint)TilesZ && _cells[z * TilesX + x];

        /// <summary>The rotations a house may stand at (City.dll HouseLayout_c rotate constructor 10013bc9).</summary>
        public static bool IsValidRotation(int degrees) => degrees is 0 or 90 or 180 or 270;

        /// <summary>
        /// The footprint turned by <paramref name="degrees"/>: 90 and 270 swap the sides, as the client's
        /// <c>MakeRotated</c> (10013d95) does. Which corner a tile moves to is this server's reading; it decides only the
        /// one town template whose footprint is not a full rectangle (0x6A0000, no house item names it).
        /// </summary>
        public HouseFootprint Rotated(int degrees)
        {
            if (!IsValidRotation(degrees))
                throw new ArgumentOutOfRangeException(nameof(degrees));
            if (degrees == 0)
                return this;
            bool swap = degrees is 90 or 270;
            int width = swap ? TilesZ : TilesX;
            int depth = swap ? TilesX : TilesZ;
            var cells = new bool[width * depth];
            for (int z = 0; z < TilesZ; z++)
            {
                for (int x = 0; x < TilesX; x++)
                {
                    if (!_cells[z * TilesX + x])
                        continue;
                    (int nx, int nz) = degrees switch
                    {
                        90 => (TilesZ - 1 - z, x),
                        180 => (TilesX - 1 - x, TilesZ - 1 - z),
                        _ => (z, TilesX - 1 - x)
                    };
                    cells[nz * width + nx] = true;
                }
            }

            return new HouseFootprint(width, depth, cells);
        }
    }

    /// <summary>Why a house may not be built where it was asked for.</summary>
    public enum BuildRefusal
    {
        None,
        BadRotation,
        TooManyHouses,
        /// <summary>The first house of a district is its headquarters; nothing else stands without one (client: "Construct is refused when the map holds no HQ").</summary>
        NeedsHeadquarters,
        /// <summary>At most one headquarters, grid, guard house, radar station, cloaking device and tax office per district.</summary>
        OneOfTypeOnly,
        /// <summary>A unique house (type 9) only once per template.</summary>
        OneOfTemplateOnly,
        /// <summary>A market only in a private city (CityID_t playfield type 0xC79E).</summary>
        MarketNotAllowed,
        /// <summary>A tile of the house's box lies outside the district's zones.</summary>
        OutsideDistrict,
        /// <summary>A covered tile lies on a tile its zone keeps clear.</summary>
        NotBuildable,
        Overlaps
    }

    /// <summary>
    /// The placement rules, the same the client checks before it sends a build (GUI 100e44a2, 100e4d71; City
    /// CanBuildLayout 10002882, LayoutTileAllowedAtGlobalTpos 100139ea, HouseLayout::Intersects 100138fc), checked again
    /// here because a client can send anything. Pure: the tick checks against its cache, the transaction against the rows.
    /// </summary>
    public static class CityPlacement
    {
        /// <summary>Types a district holds at most one of (GUI 100e44a2).</summary>
        public static bool IsOnePerDistrict(HouseType type)
            => type is HouseType.Headquarters or HouseType.Grid or HouseType.GuardHouse or HouseType.RadarStation
                or HouseType.CloakingDevice or HouseType.TaxOffice;

        public static bool IsCloakingDevice(HouseType type) => type is HouseType.CloakingDevice or HouseType.MiniCloakingDevice;

        /// <summary>
        /// Whether <paramref name="template"/> may stand at (<paramref name="tileX"/>, <paramref name="tileZ"/>) turned by
        /// <paramref name="rotation"/>, beside <paramref name="houses"/> (every house of the district, pending, built or being
        /// demolished: each holds its tiles until it is gone). <paramref name="templates"/> resolves the other houses'
        /// templates; a house whose template is no longer known keeps no tiles and no type.
        /// </summary>
        public static BuildRefusal Check(CityControllerDefinition controller, HouseTemplateDefinition template, int tileX, int tileZ,
            int rotation, IReadOnlyList<CityHouse> houses, Func<int, HouseTemplateDefinition?> templates, int maxHouses, bool allowMarket)
        {
            ArgumentNullException.ThrowIfNull(controller);
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(houses);
            ArgumentNullException.ThrowIfNull(templates);
            if (!HouseFootprint.IsValidRotation(rotation))
                return BuildRefusal.BadRotation;
            if (houses.Count >= maxHouses)
                return BuildRefusal.TooManyHouses;
            if (template.Type == HouseType.Market && !allowMarket)
                return BuildRefusal.MarketNotAllowed;

            bool hasHeadquarters = false;
            foreach (CityHouse house in houses)
            {
                HouseTemplateDefinition? other = templates(house.TemplateId);
                if (other == null)
                    continue;
                if (other.Type == HouseType.Headquarters && house.DemolishAtUtc == null)
                    hasHeadquarters = true;
                if (IsOnePerDistrict(template.Type) && other.Type == template.Type)
                    return BuildRefusal.OneOfTypeOnly;
                if (template.Type == HouseType.Unique && other.Id == template.Id)
                    return BuildRefusal.OneOfTemplateOnly;
            }

            if (template.Type != HouseType.Headquarters && !hasHeadquarters)
                return BuildRefusal.NeedsHeadquarters;

            HouseFootprint footprint = template.Footprint.Rotated(rotation);
            for (int z = 0; z < footprint.TilesZ; z++)
            {
                for (int x = 0; x < footprint.TilesX; x++)
                {
                    CityZone? zone = controller.ZoneAt(tileX + x, tileZ + z);
                    if (zone == null)
                        return BuildRefusal.OutsideDistrict;
                    if (footprint.IsOccupied(x, z) && !zone.IsBuildable(tileX + x - zone.OriginTileX, tileZ + z - zone.OriginTileZ))
                        return BuildRefusal.NotBuildable;
                }
            }

            foreach (CityHouse house in houses)
            {
                HouseTemplateDefinition? other = templates(house.TemplateId);
                if (other == null || !HouseFootprint.IsValidRotation(house.Rotation))
                    continue;
                if (Intersects(footprint, tileX, tileZ, other.Footprint.Rotated(house.Rotation), house.TileX, house.TileZ))
                    return BuildRefusal.Overlaps;
            }

            return BuildRefusal.None;
        }

        /// <summary>True when a covered tile of one footprint lands on a covered tile of the other.</summary>
        public static bool Intersects(HouseFootprint a, int ax, int az, HouseFootprint b, int bx, int bz)
        {
            int left = Math.Max(ax, bx), right = Math.Min(ax + a.TilesX, bx + b.TilesX);
            int top = Math.Max(az, bz), bottom = Math.Min(az + a.TilesZ, bz + b.TilesZ);
            for (int z = top; z < bottom; z++)
                for (int x = left; x < right; x++)
                    if (a.IsOccupied(x - ax, z - az) && b.IsOccupied(x - bx, z - bz))
                        return true;
            return false;
        }

        /// <summary>Whether a house's corner tile is at (<paramref name="tileX"/>, <paramref name="tileZ"/>).</summary>
        public static CityHouse? HouseAt(IReadOnlyList<CityHouse> houses, int tileX, int tileZ)
        {
            foreach (CityHouse house in houses)
                if (house.TileX == tileX && house.TileZ == tileZ)
                    return house;
            return null;
        }
    }
}
