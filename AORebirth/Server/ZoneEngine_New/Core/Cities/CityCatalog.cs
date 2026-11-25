namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.Content;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;

    /// <summary>City.dll's <c>HouseType_e</c> (the IsX getters at 1000dfb9..1000e009; GetHouseTypeName 10014f62).</summary>
    public enum HouseType
    {
        Normal = 0,
        Headquarters = 1,
        Market = 2,
        Grid = 3,
        GuardHouse = 4,
        RadarStation = 5,
        CloakingDevice = 6,
        MiniCloakingDevice = 7,
        TaxOffice = 8,
        Unique = 9
    }

    /// <summary>
    /// One buildable zone of a city controller: a 10 x 10 tile square (40 m) whose corner tile follows from its id
    /// (City.dll SetOffsetTileFromZoneID 10013521), and its mask of buildable tiles (bit z * 10 + x, lowest bit first).
    /// </summary>
    public sealed class CityZone
    {
        public const int Tiles = 10;

        readonly byte[] _mask;

        public CityZone(int zoneId, int originTileX, int originTileZ, byte[] mask)
        {
            if (mask == null || mask.Length * 8 < Tiles * Tiles)
                throw new ArgumentException("A zone mask holds 100 bits.", nameof(mask));
            ZoneId = zoneId;
            OriginTileX = originTileX;
            OriginTileZ = originTileZ;
            _mask = mask;
        }

        public int ZoneId { get; }
        public int OriginTileX { get; }
        public int OriginTileZ { get; }

        /// <summary>Whether the tile (<paramref name="localX"/>, <paramref name="localZ"/>) inside the zone may carry a house.</summary>
        public bool IsBuildable(int localX, int localZ)
        {
            if ((uint)localX >= Tiles || (uint)localZ >= Tiles)
                return false;
            int bit = localZ * Tiles + localX;
            return (_mask[bit >> 3] & (1 << (bit & 7))) != 0;
        }
    }

    /// <summary>
    /// One city controller of a playfield's RDB 1000039 record: the district a player buys, what it costs, and where on
    /// it houses may stand. The client builds the same table from its own RDB.
    /// </summary>
    public sealed class CityControllerDefinition
    {
        readonly Dictionary<int, CityZone> _zones;
        readonly int _zonesPerRow;

        public CityControllerDefinition(int playfieldId, int index, int dynelInstance, int houseMinLevel, int houseMaxLevel,
            long purchaseCost, long upkeepCost, int tileExtentX, int tileExtentZ, IReadOnlyList<CityZone> zones)
        {
            PlayfieldId = playfieldId;
            Index = index;
            DynelInstance = dynelInstance;
            HouseMinLevel = houseMinLevel;
            HouseMaxLevel = houseMaxLevel;
            PurchaseCost = purchaseCost;
            UpkeepCost = upkeepCost;
            TileExtentX = tileExtentX;
            TileExtentZ = tileExtentZ;
            _zonesPerRow = Math.Max(1, tileExtentX / CityZone.Tiles);
            _zones = zones.ToDictionary(zone => zone.ZoneId);
        }

        public int PlayfieldId { get; }

        /// <summary>The controller's place in the record: the dynel instance's bits 16..29.</summary>
        public int Index { get; }

        /// <summary>The controller dynel's instance, <c>0xC0000000 | (index &lt;&lt; 16) | playfield</c>.</summary>
        public int DynelInstance { get; }

        public int HouseMinLevel { get; }
        public int HouseMaxLevel { get; }
        public long PurchaseCost { get; }
        public long UpkeepCost { get; }
        public int TileExtentX { get; }
        public int TileExtentZ { get; }
        public IReadOnlyCollection<CityZone> Zones => _zones.Values;

        /// <summary>
        /// The instance the client's requests name the city by, and CityID_t's controller instance: the dynel instance
        /// shifted right 16 and masked to 16 bits (Gamecode LoadPlayfieldCityData 101226c9), <c>0xC000 | index</c>.
        /// </summary>
        public int CityInstance => (int)(((uint)DynelInstance >> 16) & 0xFFFF);

        /// <summary>The zone of this controller holding the tile, or null.</summary>
        public CityZone? ZoneAt(int tileX, int tileZ)
        {
            if (tileX < 0 || tileZ < 0 || tileX >= TileExtentX || tileZ >= TileExtentZ)
                return null;
            int zoneId = tileZ / CityZone.Tiles * _zonesPerRow + tileX / CityZone.Tiles;
            return _zones.TryGetValue(zoneId, out CityZone? zone) ? zone : null;
        }

        /// <summary>The number of buildable tiles: the district's size.</summary>
        public int BuildableTiles
        {
            get
            {
                int count = 0;
                foreach (CityZone zone in _zones.Values)
                    for (int z = 0; z < CityZone.Tiles; z++)
                        for (int x = 0; x < CityZone.Tiles; x++)
                            if (zone.IsBuildable(x, z))
                                count++;
                return count;
            }
        }
    }

    /// <summary>The city controllers of one playfield.</summary>
    public sealed class CityPlayfieldDefinition
    {
        readonly Dictionary<int, CityControllerDefinition> _byIndex;
        readonly Dictionary<int, CityControllerDefinition> _byDynel;

        public CityPlayfieldDefinition(int playfieldId, string name, int tileExtentX, int tileExtentZ, IReadOnlyList<CityControllerDefinition> controllers)
        {
            PlayfieldId = playfieldId;
            Name = name ?? string.Empty;
            TileExtentX = tileExtentX;
            TileExtentZ = tileExtentZ;
            Controllers = controllers;
            _byIndex = controllers.ToDictionary(c => c.Index);
            _byDynel = controllers.ToDictionary(c => c.DynelInstance);
        }

        public int PlayfieldId { get; }

        /// <summary>The playfield's name (RDBPlayfield), for the owners' messages.</summary>
        public string Name { get; }

        public int TileExtentX { get; }
        public int TileExtentZ { get; }
        public IReadOnlyList<CityControllerDefinition> Controllers { get; }

        public bool TryGetByIndex(int index, out CityControllerDefinition controller)
            => _byIndex.TryGetValue(index, out controller!);

        /// <summary>The controller a client request names by its city instance (<c>0xC000 | index</c>).</summary>
        public bool TryGetByCityInstance(int cityInstance, out CityControllerDefinition controller)
        {
            controller = null!;
            return (cityInstance & 0xC000) == 0xC000 && _byIndex.TryGetValue(cityInstance & 0x3FFF, out controller!)
                && controller.CityInstance == cityInstance;
        }

        public bool TryGetByDynelInstance(int dynelInstance, out CityControllerDefinition controller)
            => _byDynel.TryGetValue(dynelInstance, out controller!);
    }

    /// <summary>One RDB 1000040 house template: what the server needs of it, its type, upkeep and footprint.</summary>
    public sealed class HouseTemplateDefinition
    {
        public HouseTemplateDefinition(int id, HouseType type, long upkeep, HouseFootprint footprint)
        {
            Id = id;
            Type = type;
            Upkeep = upkeep;
            Footprint = footprint;
        }

        public int Id { get; }
        public HouseType Type { get; }
        public long Upkeep { get; }

        /// <summary>The footprint as the template stores it (rotation 0).</summary>
        public HouseFootprint Footprint { get; }

        /// <summary>The playfield the house's inside is (CityHouseTemplate_c::GetInsideTemplateInstance); 0 for none.</summary>
        public int InsideTemplate { get; init; }

        /// <summary>The doors spawned with a standing house (DoorData_t), in record order.</summary>
        public IReadOnlyList<HouseDoorDefinition> Doors { get; init; } = [];

        /// <summary>The guards spawned with a standing house (GuardData_t), in record order.</summary>
        public IReadOnlyList<HouseGuardDefinition> Guards { get; init; } = [];
    }

    /// <summary>A house door: an item template spawned as a static, at a house-local position (metres from the layout's
    /// corner, rotation 0) and turn in degrees as the record stores them.</summary>
    public sealed record HouseDoorDefinition(int Template, float X, float Y, float Z, int Turn);

    /// <summary>A house guard: an NPC hash, the int stored with it (GuardData_t +0x30), a house-local position and turn as
    /// the record stores them.</summary>
    public sealed record HouseGuardDefinition(string Hash, int Value, float X, float Y, float Z, int Turn);

    /// <summary>
    /// CityRules.json: the server's side of org cities, everything the client does not decide. Times are whole units so
    /// a GameData author reads them at a glance.
    /// </summary>
    public sealed class CityRules
    {
        public static CityRules Disabled(string reason) => new() { DisabledReason = reason };

        /// <summary>
        /// The playfields whose districts are for sale. The client's data also names test, template and instanced
        /// playfields (6, 106, 500, 4898, 4899, 5001, 5002, 6010), some at placeholder prices; only these are offered.
        /// </summary>
        public IReadOnlyCollection<int> Playfields { get; init; } = [];

        /// <summary>Why cities are off; null when the rules loaded and are enabled.</summary>
        public string? DisabledReason { get; private init; }

        public bool Enabled => DisabledReason == null;

        /// <summary>Days between upkeep bills (retail: a month).</summary>
        public int BillingPeriodDays { get; init; } = 30;

        /// <summary>Hours before a bill the online members are warned.</summary>
        public int UpkeepWarningHours { get; init; } = 24;

        /// <summary>Hours a district may stand empty before it is lost (retail text 10002/140816276).</summary>
        public int EmptyPlotReleaseHours { get; init; } = 24;

        /// <summary>Hours an organization that bought a district and never built its headquarters may not buy it again.</summary>
        public int NoHeadquartersLockoutHours { get; init; } = 48;

        /// <summary>Seconds from "Demolish Building" until the house is gone (retail text 10002/140816276: 2 minutes).</summary>
        public int DemolishSeconds { get; init; } = 120;

        /// <summary>Seconds between two changes of a cloaking device.</summary>
        public int CloakToggleCooldownSeconds { get; init; } = 7200;

        /// <summary>A new district's controller charge, 0..1. Nothing charges or drains it yet.</summary>
        public float StartingControllerCharge { get; init; }

        /// <summary>Advantages are active while the controller charge is at least this.</summary>
        public float AdvantageChargeThreshold { get; init; }

        /// <summary>The quality an advantage item family is used at (retail sent QL 300).</summary>
        public int AdvantageQuality { get; init; } = 300;

        /// <summary>
        /// Who may build, demolish and cloak: the leader, everyone in a single-rank form, and members at least this many
        /// ranks above the form's lowest. Only the organization's leader buys its district, with their own credits.
        /// </summary>
        public int ManageMinRanksAboveLowest { get; init; } = 1;

        /// <summary>The most houses one district holds, so a district's houses always fit PlayfieldAllCities.</summary>
        public int MaxHousesPerPlot { get; init; } = 40;

        /// <summary>How far (metres, edge to edge) from its controller a player may be when a window request arrives.</summary>
        public float InteractionRange { get; init; } = 15f;

        /// <summary>
        /// Minutes a purchase or a house may wait for the player's write-behind lane to confirm it before the server
        /// treats it as abandoned (a crash between the claim and the payment). Far above any healthy lane delay.
        /// </summary>
        public int PendingTimeoutMinutes { get; init; } = 30;

        /// <summary>
        /// The door of a house inside (by inside playfield) a character entering lands at. Stock's server chose it and
        /// nothing in the client data says which: an inside with one door, or one whose placement marks an exit
        /// (ExitInstance), needs no entry here; the Superior Player Market (4357) and the HQ insides have several.
        /// </summary>
        public IReadOnlyDictionary<int, int> InsideEntryDoors { get; init; } = new Dictionary<int, int>();

        public TimeSpan BillingPeriod => TimeSpan.FromDays(BillingPeriodDays);
        public TimeSpan UpkeepWarning => TimeSpan.FromHours(UpkeepWarningHours);
        public TimeSpan EmptyPlotRelease => TimeSpan.FromHours(EmptyPlotReleaseHours);
        public TimeSpan NoHeadquartersLockout => TimeSpan.FromHours(NoHeadquartersLockoutHours);
        public TimeSpan Demolish => TimeSpan.FromSeconds(DemolishSeconds);
        public TimeSpan CloakToggleCooldown => TimeSpan.FromSeconds(CloakToggleCooldownSeconds);
        public TimeSpan PendingTimeout => TimeSpan.FromMinutes(PendingTimeoutMinutes);

        /// <summary>Whether a member of <paramref name="rank"/> may build, demolish and cloak.</summary>
        public bool CanManage(Organizations.GoverningForm form, int rank, bool isLeader)
            => isLeader || form.SingleRank || form.LowestRank - rank >= ManageMinRanksAboveLowest;

        internal IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (BillingPeriodDays < 1) problems.Add("BillingPeriodDays must be 1 or more.");
            if (UpkeepWarningHours < 0) problems.Add("UpkeepWarningHours must be 0 or more.");
            if (EmptyPlotReleaseHours < 1) problems.Add("EmptyPlotReleaseHours must be 1 or more.");
            if (NoHeadquartersLockoutHours < 0) problems.Add("NoHeadquartersLockoutHours must be 0 or more.");
            if (DemolishSeconds < 0) problems.Add("DemolishSeconds must be 0 or more.");
            if (CloakToggleCooldownSeconds < 0) problems.Add("CloakToggleCooldownSeconds must be 0 or more.");
            if (StartingControllerCharge is < 0f or > 1f) problems.Add("StartingControllerCharge must be from 0 to 1.");
            if (AdvantageChargeThreshold is < 0f or > 1f) problems.Add("AdvantageChargeThreshold must be from 0 to 1.");
            if (AdvantageQuality < 1) problems.Add("AdvantageQuality must be 1 or more.");
            if (ManageMinRanksAboveLowest < 0) problems.Add("ManageMinRanksAboveLowest must be 0 or more.");
            if (MaxHousesPerPlot is < 1 or > CityLimits.MaxHousesPerPlotCap) problems.Add("MaxHousesPerPlot must be from 1 to " + CityLimits.MaxHousesPerPlotCap + ".");
            if (InteractionRange <= 0f) problems.Add("InteractionRange must be above 0.");
            if (PendingTimeoutMinutes < 1) problems.Add("PendingTimeoutMinutes must be 1 or more.");
            if (Playfields.Count == 0 || Playfields.Any(id => id <= 0)) problems.Add("Playfields must list the playfield ids whose districts are for sale.");
            return problems;
        }
    }

    /// <summary>Wire limits the data must respect.</summary>
    public static class CityLimits
    {
        /// <summary>PlayfieldAllCities carries its houses behind a 16-bit length: 4 bytes of count, 25 per house.</summary>
        public const int MaxHousesPerPlayfield = (ushort.MaxValue - 4) / 25;

        /// <summary>A per-district cap no data may raise above.</summary>
        public const int MaxHousesPerPlotCap = 200;
    }

    /// <summary>
    /// The org city data: CityControllers.json and HouseTemplates.json (exported from the client's RDB by
    /// RDBDataExtractor), and CityRules.json. Swapped whole on reload; a file that fails keeps the old data and reports
    /// why. Without CityControllers.json, HouseTemplates.json or CityRules.json, cities are off.
    /// </summary>
    public sealed class CityCatalog
    {
        public const string ReloadName = "Cities";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        readonly string _root;
        volatile Snapshot _data = Snapshot.Empty(CityRules.Disabled("not loaded"));

        public CityCatalog(IGameData gameData, IZoneLogger logger)
            : this((gameData ?? throw new ArgumentNullException(nameof(gameData))).RootPath)
        {
            ArgumentNullException.ThrowIfNull(logger);
            foreach (string problem in LoadProblems)
                logger.Warn("Cities: " + problem);
            logger.Info(Rules.Enabled
                ? "Cities playfields=" + _data.Playfields.Count + " controllers=" + _data.Playfields.Values.Sum(p => p.Controllers.Count)
                    + " templates=" + _data.Templates.Count
                : "Cities disabled: " + Rules.DisabledReason);
        }

        public CityCatalog(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            _root = gameDataRoot;
            LoadProblems = Reload().Problems;
        }

        /// <summary>Data for tests: no files.</summary>
        internal CityCatalog(CityRules rules, IEnumerable<CityPlayfieldDefinition> playfields, IEnumerable<HouseTemplateDefinition> templates)
        {
            _root = string.Empty;
            _data = new Snapshot(rules, playfields.ToDictionary(p => p.PlayfieldId), templates.ToDictionary(t => t.Id));
            LoadProblems = [];
        }

        public IReadOnlyList<string> LoadProblems { get; }

        public CityRules Rules => _data.Rules;

        public bool Enabled => _data.Rules.Enabled;

        public IReadOnlyCollection<CityPlayfieldDefinition> Playfields => _data.Playfields.Values;

        public bool TryGetPlayfield(int playfieldId, out CityPlayfieldDefinition playfield)
            => _data.Playfields.TryGetValue(playfieldId, out playfield!);

        public bool TryGetController(int playfieldId, int index, out CityControllerDefinition controller)
        {
            controller = null!;
            return _data.Playfields.TryGetValue(playfieldId, out CityPlayfieldDefinition? playfield)
                && playfield.TryGetByIndex(index, out controller);
        }

        public bool TryGetTemplate(int templateId, out HouseTemplateDefinition template)
            => _data.Templates.TryGetValue(templateId, out template!);

        /// <summary>Every house template loaded.</summary>
        public IEnumerable<HouseTemplateDefinition> AllTemplates => _data.Templates.Values;

        /// <summary>Raised off the tick after a reload swapped the data in, so open windows can get the new prices.</summary>
        public event Action? Reloaded;

        /// <summary>Reads the three files again. Off the playfield tick.</summary>
        public GameDataReloadResult Reload()
        {
            var problems = new List<string>();
            try
            {
                Snapshot loaded = Load(_root, problems);
                _data = loaded;
                Reloaded?.Invoke();
                return new GameDataReloadResult(ReloadName, true, loaded.Playfields.Count, problems);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or NotSupportedException or ArgumentException)
            {
                problems.Add(exception.Message);
                return new GameDataReloadResult(ReloadName, false, _data.Playfields.Count, problems);
            }
        }

        static Snapshot Load(string root, List<string> problems)
        {
            string rulesPath = GameDataPaths.ResolveFile(root, GameDataPaths.CityRulesFileName);
            string controllersPath = GameDataPaths.ResolveFile(root, GameDataPaths.CityControllersFileName);
            string templatesPath = GameDataPaths.ResolveFile(root, GameDataPaths.HouseTemplatesFileName);
            if (!File.Exists(rulesPath))
                return Snapshot.Empty(CityRules.Disabled(GameDataPaths.CityRulesFileName + " is missing."));
            RulesFile rulesFile = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(rulesPath), JsonOptions)
                ?? throw new InvalidDataException(GameDataPaths.CityRulesFileName + " is empty.");
            CityRules rules = rulesFile.ToRules();
            IReadOnlyList<string> ruleProblems = rules.Validate();
            if (ruleProblems.Count > 0)
                throw new InvalidDataException(GameDataPaths.CityRulesFileName + ": " + string.Join(" ", ruleProblems));
            if (!rulesFile.Enabled)
                return Snapshot.Empty(CityRules.Disabled(GameDataPaths.CityRulesFileName + " has Enabled false."));
            if (!File.Exists(controllersPath) || !File.Exists(templatesPath))
                return Snapshot.Empty(CityRules.Disabled(GameDataPaths.CityControllersFileName + " or " + GameDataPaths.HouseTemplatesFileName
                    + " is missing (private GameData, exported by RDBDataExtractor)."));

            ControllersFile controllers = JsonSerializer.Deserialize<ControllersFile>(File.ReadAllText(controllersPath), JsonOptions)
                ?? throw new InvalidDataException(GameDataPaths.CityControllersFileName + " is empty.");
            TemplatesFile templates = JsonSerializer.Deserialize<TemplatesFile>(File.ReadAllText(templatesPath), JsonOptions)
                ?? throw new InvalidDataException(GameDataPaths.HouseTemplatesFileName + " is empty.");

            var playfields = new Dictionary<int, CityPlayfieldDefinition>();
            var offered = new HashSet<int>(rules.Playfields);
            for (int i = 0; i < (controllers.Playfields?.Count ?? 0); i++)
            {
                PlayfieldRow row = controllers.Playfields![i] ?? throw new InvalidDataException("Playfields[" + i + "] is null.");
                if (!offered.Contains(row.PlayfieldId))
                    continue;
                string where = GameDataPaths.CityControllersFileName + " playfield " + row.PlayfieldId;
                if (row.PlayfieldId <= 0 || row.TileExtentX <= 0 || row.TileExtentZ <= 0 || row.TileExtentX % CityZone.Tiles != 0)
                    throw new InvalidDataException(where + ": PlayfieldId and the tile extents (a multiple of 10 across) must be positive.");
                if (playfields.ContainsKey(row.PlayfieldId))
                    throw new InvalidDataException(where + " is listed twice.");
                int zonesPerRow = row.TileExtentX / CityZone.Tiles;
                int zoneCount = zonesPerRow * ((row.TileExtentZ + CityZone.Tiles - 1) / CityZone.Tiles);
                var list = new List<CityControllerDefinition>();
                // Two districts may split one zone square (595, 4894, 6102 do), each building on its own tiles. A tile
                // buildable for both would let two organizations' houses stand on it, so the masks must not overlap.
                var claimedTiles = new Dictionary<int, byte[]>();
                foreach (ControllerRow c in row.Controllers ?? [])
                {
                    if (c == null)
                        throw new InvalidDataException(where + ": a controller is null.");
                    int instance = unchecked((int)c.DynelInstance);
                    if (c.DynelType != (int)LostEden.Protocol.GameData.IdentityType.CityController
                        || ((c.DynelInstance >> 16) & 0x3FFF) != c.Index || (c.DynelInstance & 0xFFFF) != (uint)(row.PlayfieldId & 0xFFFF)
                        || (c.DynelInstance & 0xC0000000) != 0xC0000000)
                        throw new InvalidDataException(where + " controller " + c.Index + ": the dynel must be 0xC418:0xC0000000|(index<<16)|playfield.");
                    if (c.PurchaseCost < 0 || c.UpkeepCost < 0)
                        throw new InvalidDataException(where + " controller " + c.Index + ": costs must be 0 or more.");
                    var zones = new List<CityZone>();
                    var ownZones = new HashSet<int>();
                    foreach (ZoneRow z in c.Zones ?? [])
                    {
                        if (z == null || z.ZoneId < 0 || z.ZoneId >= zoneCount || string.IsNullOrEmpty(z.Mask) || z.Mask.Length != 26)
                            throw new InvalidDataException(where + " controller " + c.Index + ": a zone id is outside the playfield or its mask is not 13 bytes.");
                        if (!ownZones.Add(z.ZoneId))
                            throw new InvalidDataException(where + " controller " + c.Index + ": zone " + z.ZoneId + " is listed twice.");
                        byte[] mask = Convert.FromHexString(z.Mask);
                        if (claimedTiles.TryGetValue(z.ZoneId, out byte[]? claimed))
                        {
                            for (int b = 0; b < mask.Length; b++)
                            {
                                if ((claimed[b] & mask[b]) != 0)
                                    throw new InvalidDataException(where + ": zone " + z.ZoneId + " has a tile two controllers may build on.");
                                claimed[b] |= mask[b];
                            }
                        }
                        else
                        {
                            claimedTiles[z.ZoneId] = (byte[])mask.Clone();
                        }

                        zones.Add(new CityZone(z.ZoneId, z.ZoneId % zonesPerRow * CityZone.Tiles, z.ZoneId / zonesPerRow * CityZone.Tiles, mask));
                    }

                    // A controller with nowhere to build (Coast of Harmony's three placeholders, 10000 credits) is not for sale.
                    if (zones.Count == 0)
                        continue;
                    list.Add(new CityControllerDefinition(row.PlayfieldId, c.Index, instance, c.HouseMinLevel, c.HouseMaxLevel,
                        c.PurchaseCost, c.UpkeepCost, row.TileExtentX, row.TileExtentZ, zones));
                }

                if (list.Select(c => c.Index).Distinct().Count() != list.Count)
                    throw new InvalidDataException(where + ": two controllers share an index.");
                playfields[row.PlayfieldId] = new CityPlayfieldDefinition(row.PlayfieldId, row.Name ?? string.Empty, row.TileExtentX, row.TileExtentZ, list);
            }

            var templateMap = new Dictionary<int, HouseTemplateDefinition>();
            foreach (TemplateRow t in templates.Templates ?? [])
            {
                if (t == null)
                    continue;
                string where = GameDataPaths.HouseTemplatesFileName + " template " + t.Id;
                if (t.Id <= 0 || templateMap.ContainsKey(t.Id))
                    throw new InvalidDataException(where + ": the id must be positive and unique.");
                if (!Enum.IsDefined(typeof(HouseType), t.HouseType) || t.Upkeep < 0)
                    throw new InvalidDataException(where + ": unknown house type " + t.HouseType + " or negative upkeep.");
                if (t.TilesX is <= 0 or > 64 || t.TilesZ is <= 0 or > 64 || string.IsNullOrEmpty(t.Layout)
                    || t.Layout.Length != (t.TilesX * t.TilesZ + 7) / 8 * 2)
                    throw new InvalidDataException(where + ": the layout must be TilesX * TilesZ bits (1..64 tiles a side).");
                HouseFootprint footprint = HouseFootprint.FromBits(t.TilesX, t.TilesZ, Convert.FromHexString(t.Layout));
                if (footprint.OccupiedCount == 0)
                {
                    problems.Add(where + " covers no tile and is left out.");
                    continue;
                }

                var doors = new List<HouseDoorDefinition>();
                foreach (DoorRow door in t.Doors ?? [])
                {
                    if (door.Template <= 0)
                        problems.Add(where + ": a door with no item template is left out.");
                    else
                        doors.Add(new HouseDoorDefinition(door.Template, door.X, door.Y, door.Z, door.Turn));
                }

                var guards = new List<HouseGuardDefinition>();
                foreach (GuardRow guard in t.Guards ?? [])
                {
                    if (string.IsNullOrWhiteSpace(guard.Hash))
                        problems.Add(where + ": a guard with no NPC hash is left out.");
                    else
                        guards.Add(new HouseGuardDefinition(guard.Hash, guard.Value, guard.X, guard.Y, guard.Z, guard.Turn));
                }

                templateMap[t.Id] = new HouseTemplateDefinition(t.Id, (HouseType)t.HouseType, t.Upkeep, footprint)
                {
                    InsideTemplate = t.InsideTemplate,
                    Doors = doors,
                    Guards = guards,
                };
            }

            return new Snapshot(rules, playfields, templateMap);
        }

        sealed record Snapshot(CityRules Rules, Dictionary<int, CityPlayfieldDefinition> Playfields, Dictionary<int, HouseTemplateDefinition> Templates)
        {
            public static Snapshot Empty(CityRules rules) => new(rules, new Dictionary<int, CityPlayfieldDefinition>(), new Dictionary<int, HouseTemplateDefinition>());
        }

        // ---------------------------------------------------------------------------------------- file shapes

        /// <summary>CityRules.json.</summary>
        public sealed class RulesFile
        {
            public bool Enabled { get; set; } = true;
            /// <summary>The playfield ids whose districts are for sale.</summary>
            public List<int>? Playfields { get; set; }
            public int BillingPeriodDays { get; set; } = 30;
            public int UpkeepWarningHours { get; set; } = 24;
            public int EmptyPlotReleaseHours { get; set; } = 24;
            public int NoHeadquartersLockoutHours { get; set; } = 48;
            public int DemolishSeconds { get; set; } = 120;
            public int CloakToggleCooldownSeconds { get; set; } = 7200;
            public float StartingControllerCharge { get; set; }
            public float AdvantageChargeThreshold { get; set; }
            public int AdvantageQuality { get; set; } = 300;
            public int ManageMinRanksAboveLowest { get; set; } = 1;
            public int MaxHousesPerPlot { get; set; } = 40;
            public float InteractionRange { get; set; } = 15f;
            public int PendingTimeoutMinutes { get; set; } = 30;
            /// <summary>Inside playfield id to its entry door's instance, as hex ("4357": "C0011105").</summary>
            public Dictionary<string, string>? InsideEntryDoors { get; set; }

            internal CityRules ToRules() => new()
            {
                Playfields = (Playfields ?? []).Distinct().ToArray(),
                BillingPeriodDays = BillingPeriodDays,
                UpkeepWarningHours = UpkeepWarningHours,
                EmptyPlotReleaseHours = EmptyPlotReleaseHours,
                NoHeadquartersLockoutHours = NoHeadquartersLockoutHours,
                DemolishSeconds = DemolishSeconds,
                CloakToggleCooldownSeconds = CloakToggleCooldownSeconds,
                StartingControllerCharge = StartingControllerCharge,
                AdvantageChargeThreshold = AdvantageChargeThreshold,
                AdvantageQuality = AdvantageQuality,
                ManageMinRanksAboveLowest = ManageMinRanksAboveLowest,
                MaxHousesPerPlot = MaxHousesPerPlot,
                InteractionRange = InteractionRange,
                PendingTimeoutMinutes = PendingTimeoutMinutes,
                InsideEntryDoors = (InsideEntryDoors ?? []).ToDictionary(
                    pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                    pair => unchecked((int)uint.Parse(pair.Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)))
            };
        }

        /// <summary>CityControllers.json (RDB 1000039).</summary>
        public sealed class ControllersFile
        {
            public List<PlayfieldRow>? Playfields { get; set; }
        }

        public sealed class PlayfieldRow
        {
            public int PlayfieldId { get; set; }
            public string? Name { get; set; }
            public int TileExtentX { get; set; }
            public int TileExtentZ { get; set; }
            public List<ControllerRow>? Controllers { get; set; }
        }

        public sealed class ControllerRow
        {
            public int Index { get; set; }
            public int DynelType { get; set; }
            public uint DynelInstance { get; set; }
            public int HouseMinLevel { get; set; }
            public int HouseMaxLevel { get; set; }
            public long PurchaseCost { get; set; }
            public long UpkeepCost { get; set; }
            public List<ZoneRow>? Zones { get; set; }
        }

        public sealed class ZoneRow
        {
            public int ZoneId { get; set; }
            public int Type { get; set; }
            /// <summary>13 bytes of hex: 100 bits, bit z * 10 + x, lowest bit first.</summary>
            public string? Mask { get; set; }
        }

        /// <summary>HouseTemplates.json (RDB 1000040).</summary>
        public sealed class TemplatesFile
        {
            public List<TemplateRow>? Templates { get; set; }
        }

        public sealed class TemplateRow
        {
            public int Id { get; set; }
            public int Version { get; set; }
            public string? Name { get; set; }
            public int HouseType { get; set; }
            public long Upkeep { get; set; }
            public int InsideTemplate { get; set; }
            public int FixturesAllowed { get; set; }
            public float StealthFactor { get; set; }
            public int TilesX { get; set; }
            public int TilesZ { get; set; }
            /// <summary>TilesX * TilesZ bits of hex, bit z * TilesX + x, lowest bit first.</summary>
            public string? Layout { get; set; }
            public List<DoorRow>? Doors { get; set; }
            public List<GuardRow>? Guards { get; set; }
        }

        public sealed class DoorRow
        {
            public int Template { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public int Turn { get; set; }
        }

        public sealed class GuardRow
        {
            public string? Hash { get; set; }
            public int Value { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public int Turn { get; set; }
        }
    }

    /// <summary>The Cities reload: CityRules.json, CityControllers.json and HouseTemplates.json.</summary>
    public sealed class CitiesReload(CityCatalog catalog) : IReloadableGameData
    {
        public string Name => CityCatalog.ReloadName;

        public IReadOnlyList<string> Files { get; } =
            [GameDataPaths.CityRulesFileName, GameDataPaths.CityControllersFileName, GameDataPaths.HouseTemplatesFileName];

        public GameDataReloadResult Reload() => catalog.Reload();
    }
}
