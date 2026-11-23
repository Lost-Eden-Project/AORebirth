namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using AORebirth.Core.GameData;
    using AORebirth.Core.Vector;

    using Utility;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Playfield.Locality;

    internal enum HashSpawnState
    {
        Alive = 0,
        Dead = 1
    }

    /// <summary>
    /// Rotation spawn shorts are degrees. The client stores
    /// rotationMid = angle * 2 * pi / 360 and rotationWidth = width * 2 * pi / 360.
    /// </summary>
    internal static class SpawnHeading
    {
        internal static float ToRadians(int degrees)
        {
            return degrees * (MathF.PI * 2f / 360f);
        }

        /// <summary>Cone centre. Yaw around Y, matching the heading <c>CharacterMotor</c> writes.</summary>
        internal static Quaternion FromFacingDegrees(int angleDegrees)
        {
            return FromYawRadians(ToRadians(angleDegrees));
        }

        /// <summary>
        /// Uniform sample inside the facing cone. <paramref name="unitRandom"/> is [0, 1]:
        /// 0 is mid - width/2, 1 is mid + width/2.
        /// </summary>
        internal static Quaternion Sample(int angleDegrees, int widthDegrees, double unitRandom)
        {
            float mid = ToRadians(angleDegrees);
            float width = ToRadians(widthDegrees);
            float yaw = mid + (((float)unitRandom - 0.5f) * width);
            return FromYawRadians(yaw);
        }

        static Quaternion FromYawRadians(float yawRadians)
        {
            float half = yawRadians * 0.5f;
            return new Quaternion(0, MathF.Sin(half), 0, MathF.Cos(half));
        }
    }

    /// <summary>One candidate centre, facing cone, and radius for a hash spawn point.</summary>
    internal readonly struct SpawnSite
    {
        internal SpawnSite(Vector3 centre, int facingDegrees, int widthDegrees, float radius)
        {
            Centre = centre;
            FacingDegrees = facingDegrees;
            WidthDegrees = widthDegrees;
            Heading = SpawnHeading.FromFacingDegrees(facingDegrees);
            Radius = Math.Max(0f, radius);
        }

        internal Vector3 Centre { get; }

        internal int FacingDegrees { get; }

        internal int WidthDegrees { get; }

        /// <summary>Cone centre. A live spawn samples uniformly inside the width.</summary>
        internal Quaternion Heading { get; }

        internal float Radius { get; }
    }

    /// <summary>
    /// Runtime HashSpawnPoint: district spawn entry plus Alive/Dead state and the dynels it owns.
    /// A SpawnAll parent owns one dynel per branch.
    /// </summary>
    internal sealed class HashSpawnPoint
    {
        internal HashSpawnPoint(
            string hashText,
            SpawnSite[] sites,
            int respawnTimeSeconds,
            int respawnChance,
            int minLevel,
            int maxLevel,
            int cellId,
            PlayfieldSpawnEntry source,
            bool isStatic)
        {
            ArgumentNullException.ThrowIfNull(sites);
            ArgumentNullException.ThrowIfNull(source);
            if (sites.Length == 0)
                throw new ArgumentException("At least one spawn site is required.", nameof(sites));

            HashText = hashText;
            Sites = sites;
            RespawnTimeSeconds = respawnTimeSeconds;
            RespawnChance = Math.Clamp(respawnChance, 0, 100);
            MinLevel = minLevel;
            MaxLevel = maxLevel;
            CellId = cellId;
            Source = source;
            IsStatic = isStatic;
            State = HashSpawnState.Dead;
            NextSpawnTime = DateTime.UtcNow;
            _members = new List<Dynel>();
        }

        readonly List<Dynel> _members;

        internal string HashText { get; }

        internal PlayfieldSpawnEntry Source { get; }

        internal SpawnSite[] Sites { get; }

        internal int RespawnTimeSeconds { get; }

        /// <summary>Percent chance (0-100) to spawn when state is Dead and the timer has elapsed.</summary>
        internal int RespawnChance { get; }

        internal int MinLevel { get; }

        internal int MaxLevel { get; }

        internal int CellId { get; }

        /// <summary>Hash resolved to an item template, not an NPC template; spawns a static dynel.</summary>
        internal bool IsStatic { get; }

        internal HashSpawnState State { get; set; }

        internal DateTime NextSpawnTime { get; set; }

        /// <summary>
        /// The point has spawned since the playfield loaded. Its first spawn skips the respawn chance, so a fresh
        /// playfield starts populated; later respawns roll as usual.
        /// </summary>
        internal bool HasSpawned { get; set; }

        /// <summary>False for one-shot points (quest dungeons): once its NPC is killed the point never spawns again.</summary>
        internal bool RespawnsAfterDeath { get; init; } = true;

        /// <summary>
        /// A Districts.json point: its NPCs are scattered over the navmesh around the point when they spawn and wander
        /// there (Gameplay.json wander).
        /// </summary>
        internal bool Wanders { get; init; }

        /// <summary>NpcScripts binding key (<see cref="NpcScripts.SpawnPointKey"/>): hash, position and stack ordinal.</summary>
        internal string? Key { get; set; }

        /// <summary>Accepted mission branches; keep their ordinals through sleep and reconstruction.</summary>
        internal MobTemplate[]? FrozenTemplates { get; init; }

        internal Action<NpcCharacter, int>? OnBranchSpawned { get; init; }

        internal Func<int, bool>? IsBranchKilled { get; init; }

        /// <summary>Runs on each NPC this point spawns before it is registered (a mission population's stat overrides).</summary>
        internal Action<NpcCharacter>? Configure { get; init; }

        /// <summary>A one-shot point whose NPC was killed.</summary>
        internal bool Retired { get; set; }

        /// <summary>The level the current spawn rolled, and the statics it picked.</summary>
        internal int? RolledLevel { get; set; }

        internal HashInstance[]? RolledStatics { get; set; }

        /// <summary>
        /// A Districts.json point; its district is <c>Source.DistrictIndex</c>. A spawn reload keeps or replaces a district's
        /// points together, since each load shuffles them over the district's locations.
        /// </summary>
        internal bool FromDistrictsFile { get; init; }

        /// <summary>
        /// The Spawns.json entry this point was built from, hashed: a spawn reload keeps the point (and its NPCs) when its
        /// entry is unchanged. Null until the first reload of its playfield.
        /// </summary>
        internal string? Fingerprint { get; set; }

        /// <summary>
        /// A sleep-despawned roll: the point wakes with these NPCs (or statics) at this level instead of rolling again, so
        /// leaving and returning cannot re-roll a category point for its rare. Only a kill frees the point to roll anew.
        /// </summary>
        internal bool HoldsRoll { get; set; }

        internal MobTemplate[]? HeldTemplates { get; set; }

        internal int MemberCount => _members.Count;

        internal void AddMember(Dynel member)
        {
            ArgumentNullException.ThrowIfNull(member);
            _members.Add(member);
        }

        internal bool RemoveMember(Dynel member)
            => _members.Remove(member);

        internal Dynel[] MembersSnapshot()
            => _members.ToArray();
    }

    /// <summary>
    /// Loads Spawns.json and the district spawn tables in Districts.json, assigns points to cells,
    /// and drives spawn/despawn from cell heat.
    /// </summary>
    public sealed class HashSpawnSystem
    {
        /// <summary>Cap catch-up chance rolls so long sleep cannot explode roll count.</summary>
        private const int MaxCatchUpRolls = 64;

        private readonly Playfield _playfield;
        private readonly SpawnService _spawnService;
        private readonly IGameData _gameData;
        private readonly IZoneLogger _logger;
        private readonly bool _halloweenEnabled;
        private readonly Dictionary<int, List<HashSpawnPoint>> _pointsByCell = new();
        private readonly List<HashSpawnPoint> _allPoints = new();
        private readonly Dictionary<string, List<HashSpawnPoint>> _pointsByHash = new(StringComparer.Ordinal);
        private readonly Dictionary<Dynel, HashSpawnPoint> _pointBySpawned = new();

        /// <summary>Scratch for <see cref="OnCellSleep"/>: the sleeping cell's occupants and the away points they serve.</summary>
        private readonly List<Dynel> _sleepOccupants = new();
        private readonly List<HashSpawnPoint> _sleepStragglerPoints = new();
        private int _spawnRate = 1;
        private bool _initialized;
        private CellGrid? _grid;
        private PlayfieldLocality? _locality;

        // What the spawn reload compares against: the Spawns.json the load-time points were built from, each district's
        // spawn table hashed, and the entries already reported as skipped (a reload reports only new ones).
        private volatile PlayfieldSpawnsData? _loadedSpawns;
        private Dictionary<int, string> _districtFingerprints = new();
        private HashSet<string> _reportedSkips = new(StringComparer.Ordinal);

        // A generated or static dungeon places points after load (AddPlaced*): those are not Spawns.json points to reload.
        private bool _hasPlacedPoints;

        public HashSpawnSystem(
            Playfield playfield,
            SpawnService spawnService,
            IGameData gameData,
            IZoneLogger logger,
            GameplaySettings? gameplay = null)
        {
            ArgumentNullException.ThrowIfNull(playfield);
            ArgumentNullException.ThrowIfNull(spawnService);
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(logger);

            _playfield = playfield;
            _spawnService = spawnService;
            _gameData = gameData;
            _logger = logger;
            _halloweenEnabled = gameplay?.Events.Halloween.Enabled ?? false;
        }

        internal int PointCount => _allPoints.Count;

        internal bool TryGetSpawnPoint(NpcCharacter npc, out HashSpawnPoint point)
        {
            ArgumentNullException.ThrowIfNull(npc);
            return _pointBySpawned.TryGetValue(npc, out point!);
        }

        /// <summary>Called once after playfield DI is ready.</summary>
        public void Initialize(PlayfieldLocality locality)
        {
            ArgumentNullException.ThrowIfNull(locality);
            if (_initialized)
                return;

            _initialized = true;
            _spawnRate = locality.Policy.SpawnRate;
            _grid = locality.Grid;
            _locality = locality;
            var skips = new List<string>();
            PlayfieldSpawnsData spawns = _gameData.GetPlayfieldSpawns(_playfield.ResourceId);
            _loadedSpawns = spawns;
            foreach (HashSpawnPoint point in BuildSpawnPoints(spawns, locality.Grid, skips))
                Insert(point);
            LoadDistrictSpawns(locality.Grid, skips);
            _reportedSkips = new HashSet<string>(skips, StringComparer.Ordinal);
            AssignSpawnPointKeys();
            // Before the cells attach (and anything spawns): a zone swap's carried-over kills and timers.
            TakeInheritedState();
            AttachSpawnCells();

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "HashSpawnSystem ready playfield={0} points={1} spawnCells={2} spawnRate={3}",
                    _playfield.Identity.Instance,
                    _allPoints.Count,
                    _pointsByCell.Count,
                    _spawnRate));
        }

        /// <summary>
        /// The points <paramref name="data"/>'s Spawns.json entries make, in file order, not yet inserted or keyed. Entries
        /// that cannot spawn are logged as before and, when they name a hash, described in <paramref name="skips"/>.
        /// </summary>
        private List<HashSpawnPoint> BuildSpawnPoints(PlayfieldSpawnsData data, CellGrid grid, List<string> skips)
        {
            var points = new List<HashSpawnPoint>();
            PlayfieldSpawnEntry[] entries = data.Spawns ?? [];
            SpawnExclusionCatalog exclusions = _gameData.SpawnExclusions;
            int skipped = 0;
            int excluded = 0;

            foreach (PlayfieldSpawnEntry entry in entries)
            {
                if (!SpawnContentValidation.IsValid(entry))
                {
                    skipped++;
                    continue;
                }
                string spawnHash = entry.HashText ?? string.Empty;
                if (HasInactiveEvent(entry))
                {
                    skipped++;
                    continue;
                }

                string? hashText = entry.HashText;
                if (string.IsNullOrEmpty(hashText))
                {
                    skipped++;
                    continue;
                }

                // SpawnExclusions.json: points left out on purpose, before any template lookup or warning.
                if (exclusions.Excludes(hashText, _playfield.ResourceId))
                {
                    excluded++;
                    continue;
                }

                if (entry.StaticTemplateId is int templateId && !_spawnService.HasStaticTemplate(templateId))
                {
                    _logger.Warn($"Skipping static placement hash={spawnHash}: template={templateId} not found");
                    skips.Add(DescribeSkip(spawnHash, entry.Position, "static template "
                        + templateId.ToString(CultureInfo.InvariantCulture) + " not found"));
                    skipped++;
                    continue;
                }

                bool isStatic = entry.StaticTemplateId.HasValue || _gameData.IsStaticSpawnHash(spawnHash);
                bool isMob = !isStatic && HasSpawnableMob(spawnHash, entry.MinLevel);
                if (!isMob && !isStatic)
                {
                    _logger.Warn(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Hash spawn skipped: unresolved or invalid template={0}, playfield={1}",
                            spawnHash,
                            _playfield.Identity.Instance));
                    skips.Add(DescribeSkip(spawnHash, entry.Position, "no NpcTemplates entry that can spawn at level "
                        + entry.MinLevel.ToString(CultureInfo.InvariantCulture)));
                    skipped++;
                    continue;
                }

                if (entry.Position == null || entry.Position.Length < 3)
                {
                    skipped++;
                    continue;
                }

                Vector3 primaryPosition = new Vector3(entry.Position[0], entry.Position[1], entry.Position[2]);
                if (!grid.TryResolveCell(primaryPosition, out Cell cell))
                {
                    _logger.Warn(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Hash spawn skipped OOB hash={0} pos=({1},{2},{3}) playfield={4}",
                            hashText,
                            primaryPosition.xf,
                            primaryPosition.yf,
                            primaryPosition.zf,
                            _playfield.Identity.Instance));
                    skips.Add(DescribeSkip(hashText, entry.Position, "outside the playfield"));
                    skipped++;
                    continue;
                }

                SpawnSite[] sites = BuildSites(entry, primaryPosition);
                if (sites.Length == 0)
                {
                    skipped++;
                    continue;
                }

                HashSpawnPoint point = new HashSpawnPoint(
                    spawnHash,
                    sites,
                    Math.Max(0, entry.RespawnTime),
                    entry.RespawnChance,
                    entry.MinLevel,
                    entry.MaxLevel,
                    cell.Id,
                    entry,
                    isStatic);

                points.Add(point);
            }

            if (skipped > 0)
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "HashSpawnSystem skipped {0} spawn entries playfield={1}",
                        skipped,
                        _playfield.Identity.Instance));
            }

            if (excluded > 0)
            {
                _logger.Info(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "HashSpawnSystem excluded {0} spawn entries playfield={1} (SpawnExclusions.json)",
                        excluded,
                        _playfield.Identity.Instance));
            }

            return points;
        }

        /// <summary>A skipped spawn entry, as a reload reports it: hash, position and why.</summary>
        private static string DescribeSkip(string hash, float[]? position, string reason)
            => position is { Length: >= 3 }
                ? string.Format(CultureInfo.InvariantCulture, "{0} at ({1:F1}, {2:F1}, {3:F1}): {4}", hash, position[0],
                    position[1], position[2], reason)
                : hash + ": " + reason;

        /// <summary>Puts a built point in its cell and the hash index.</summary>
        private void Insert(HashSpawnPoint point)
        {
            if (!_pointsByCell.TryGetValue(point.CellId, out List<HashSpawnPoint>? list))
            {
                list = new List<HashSpawnPoint>();
                _pointsByCell[point.CellId] = list;
            }

            list.Add(point);
            AddPoint(point);
        }

        /// <summary>Hands the spawn-bearing cells (and which of them stay Cold) to cell heat; again after a spawn reload.</summary>
        private void AttachSpawnCells()
            => _locality!.AttachHashSpawns(
                _pointsByCell.Keys,
                AlwaysColdSpawnCells(),
                OnCellSleep,
                OnCellTick,
                OnIndoorSpawnTick);

        /// <summary>
        /// Every point gets its NpcScripts key, derived from the fields Spawns.json already has; points stacked at one
        /// position are numbered in load order. Bindings that match no point are reported once per playfield.
        /// </summary>
        /// <summary>
        /// Cells holding a spawn point whose NPC runs a script or is <see cref="NpcFeature.Persistent"/>. They stay Cold
        /// from load (CellHeatScheduler), so the NPC spawns at once, runs with no player near, and respawns on its own
        /// timer after a death.
        /// </summary>
        private List<int> AlwaysColdSpawnCells()
        {
            var cells = new List<int>();
            NpcScripts.NpcScriptCatalog? scripts = _playfield.GetService<NpcScripts.NpcScriptCatalog>();
            var templates = new List<MobTemplate>();
            foreach (KeyValuePair<int, List<HashSpawnPoint>> pair in _pointsByCell)
            {
                if (pair.Value.Exists(point =>
                        scripts?.BindsProgram(point.HashText, _playfield.ResourceId, point.Key) == true
                        || IsPersistent(point, templates)))
                    cells.Add(pair.Key);
            }

            return cells;
        }

        /// <summary>A mob point any of whose templates at its level range is <see cref="NpcFeature.Persistent"/>.</summary>
        private bool IsPersistent(HashSpawnPoint point, List<MobTemplate> scratch)
        {
            if (point.IsStatic)
                return false;

            MobTemplate[]? fixedTemplates = point.FrozenTemplates;
            if (fixedTemplates != null)
                return Array.Exists(fixedTemplates, template => template.Has(NpcFeature.Persistent));

            scratch.Clear();
            _gameData.CollectMobSpawns(point.HashText, point.MinLevel, scratch);
            if (point.MaxLevel != point.MinLevel)
                _gameData.CollectMobSpawns(point.HashText, point.MaxLevel, scratch);
            return scratch.Exists(template => template.Has(NpcFeature.Persistent));
        }

        private void AssignSpawnPointKeys()
        {
            var keys = new List<string>(_allPoints.Count);
            foreach (HashSpawnPoint point in _allPoints)
            {
                AssignKey(point);
                keys.Add(point.Key!);
            }

            _playfield.GetService<NpcScripts.NpcScriptCatalog>()?.ReportUnmatchedSpawnPoints(_playfield.ResourceId, keys);
        }

        /// <summary>A district's respawn time in seconds: Districts.json stores minutes. Zero or negative means no timer.</summary>
        private static int DistrictRespawnSeconds(PlayfieldDistrictEntry district)
            => district.RespawnTime <= 0 ? 0 : (int)Math.Min(int.MaxValue, district.RespawnTime * 60L);

        /// <summary>Full circle: district wildlife faces any way.</summary>
        private const int DistrictFacingWidthDegrees = 360;

        private static readonly JsonSerializerOptions DistrictJsonOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// District wildlife (Districts.json): each spawn table entry keeps <c>count</c> of its hash alive. Every one
        /// of those is a normal hash spawn point pinned to one of the district's spawn locations, so cell heat,
        /// respawn chance and timer, and level rolls work the same as Spawns.json. Locations are shuffled and
        /// shared round-robin across the district's entries. District RespawnTime is minutes (Spawns.json uses seconds).
        /// </summary>
        private void LoadDistrictSpawns(CellGrid grid, List<string> skips)
        {
            int playfieldId = _playfield.ResourceId;
            PlayfieldDistrictsData? data;
            try
            {
                data = ReadDistricts(_gameData.RootPath, playfieldId);
            }
            catch (InvalidDataException exception)
            {
                _logger.Error(exception, exception.Message);
                return;
            }

            if (data == null)
                return;

            _districtFingerprints = DistrictFingerprints(data);
            int added = 0;
            int skipped = 0;
            foreach (PlayfieldDistrictEntry district in data.Districts ?? [])
            {
                foreach (HashSpawnPoint point in BuildDistrictPoints(district, grid, skips, ref skipped))
                {
                    Insert(point);
                    added++;
                }
            }

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "District spawns playfield={0} points={1} skipped={2}",
                    playfieldId,
                    added,
                    skipped));
        }

        /// <summary>
        /// <paramref name="playfieldId"/>'s Districts.json, the override folder's copy when it has one; null when there is
        /// none. A file that cannot be read or parsed throws <see cref="InvalidDataException"/>. Reads a file: off the tick.
        /// </summary>
        internal static PlayfieldDistrictsData? ReadDistricts(string root, int playfieldId)
        {
            string path = GameDataPaths.ResolveFile(root, GameDataPaths.PlayfieldDistrictsRelativePath(playfieldId));
            if (!File.Exists(path))
                return null;

            try
            {
                return JsonSerializer.Deserialize<PlayfieldDistrictsData>(File.ReadAllText(path), DistrictJsonOptions);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Failed to read district spawns from " + path + " ("
                    + exception.GetType().Name + ": " + exception.Message + ")", exception);
            }
        }

        /// <summary>The points one district's spawn table makes, its locations shuffled, not yet inserted or keyed.</summary>
        private List<HashSpawnPoint> BuildDistrictPoints(PlayfieldDistrictEntry? district, CellGrid grid, List<string> skips,
            ref int skipped)
        {
            var points = new List<HashSpawnPoint>();
            if (district?.SpawnInfos is not { Length: > 0 } infos)
                return points;

            int playfieldId = _playfield.ResourceId;
            List<PlayfieldDistrictSpawnPoint> locations = new();
            foreach (PlayfieldDistrictSpawnPoint location in district.SpawnPoints ?? [])
            {
                if (location?.Position is { Length: >= 3 })
                    locations.Add(location);
            }

            if (locations.Count == 0)
            {
                skipped += infos.Length;
                return points;
            }

            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(locations));
            int next = 0;
            foreach (PlayfieldDistrictSpawnInfo info in infos)
            {
                string hash = info?.HashText ?? string.Empty;
                if (info == null || info.Count <= 0 || string.IsNullOrEmpty(hash))
                    continue;

                bool isStatic = _gameData.IsStaticSpawnHash(hash);
                if (!isStatic && !HasSpawnableMob(hash, Math.Max(1, district.NpcMinLevel)))
                {
                    _logger.Warn(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "District spawn skipped: unresolved template={0} district={1} playfield={2}",
                            hash,
                            district.DistrictIndex,
                            playfieldId));
                    skips.Add(string.Format(CultureInfo.InvariantCulture,
                        "district {0} {1}: no NpcTemplates entry that can spawn at level {2}", district.DistrictIndex, hash,
                        Math.Max(1, district.NpcMinLevel)));
                    skipped++;
                    continue;
                }

                for (int i = 0; i < info.Count; i++)
                {
                    PlayfieldDistrictSpawnPoint location = locations[next++ % locations.Count];
                    var centre = new Vector3(location.Position[0], location.Position[1], location.Position[2]);
                    if (!grid.TryResolveCell(centre, out Cell cell))
                    {
                        skips.Add(DescribeSkip("district " + district.DistrictIndex.ToString(CultureInfo.InvariantCulture)
                            + " " + hash, location.Position, "outside the playfield"));
                        skipped++;
                        continue;
                    }

                    var entry = new PlayfieldSpawnEntry
                    {
                        DistrictIndex = district.DistrictIndex,
                        Hash = info.Hash,
                        HashText = hash,
                        MinLevel = district.NpcMinLevel,
                        MaxLevel = district.NpcMaxLevel,
                        RespawnChance = district.RespawnChance,
                        RespawnTime = DistrictRespawnSeconds(district),
                        AngleW = DistrictFacingWidthDegrees,
                        Position = location.Position,
                        Radius = location.Radius
                    };
                    points.Add(new HashSpawnPoint(
                        hash,
                        [new SpawnSite(centre, 0, DistrictFacingWidthDegrees, location.Radius)],
                        DistrictRespawnSeconds(district),
                        district.RespawnChance,
                        district.NpcMinLevel,
                        district.NpcMaxLevel,
                        cell.Id,
                        entry,
                        isStatic)
                    {
                        Wanders = !isStatic,
                        FromDistrictsFile = true
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Adds one district spawn point that was placed after the playfield loaded (a generated dungeon's rooms move
        /// their style's district points into place). It behaves like a Districts.json point of
        /// <paramref name="district"/>: same respawn chance and timer, unless <paramref name="respawns"/> is false, when its
        /// NPC spawns once and never again after it is killed. An explicit requested level overrides district levels;
        /// otherwise, without district levels the template's level is kept. Optional frozen mission branches retain
        /// their supplied ordinals; killed branches still reserve this point without spawning replacements.
        /// <paramref name="assistanceRadius"/> is the point's AssistanceRadius (how far its NPCs' calls for help carry, 0:
        /// socialAggro's default), and <paramref name="configure"/> runs on each NPC it spawns before it is registered.
        /// </summary>
        internal bool AddPlacedDistrictPoint(string hash, PlayfieldDistrictEntry district, Vector3 centre, float radius,
            bool useDistrictLevels, bool respawns, int? requestedLevel = null, MobTemplate[]? frozenTemplates = null,
            Action<NpcCharacter, int>? onBranchSpawned = null, Func<int, bool>? isBranchKilled = null,
            int assistanceRadius = 0, Action<NpcCharacter>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(district);
            if (requestedLevel is <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedLevel));
            if (frozenTemplates != null && respawns)
                throw new ArgumentException("Frozen mission branches must be one-shot points.", nameof(respawns));
            if (frozenTemplates == null && (onBranchSpawned != null || isBranchKilled != null))
                throw new ArgumentException("Mission branch callbacks require frozen templates.", nameof(frozenTemplates));
            if (string.IsNullOrEmpty(hash))
                return false;

            if (_grid == null)
                return false;

            int minLevel = requestedLevel ?? (useDistrictLevels ? district.NpcMinLevel : 0);
            int maxLevel = requestedLevel ?? (useDistrictLevels ? district.NpcMaxLevel : 0);
            bool isStatic = _gameData.IsStaticSpawnHash(hash);
            if (frozenTemplates != null)
            {
                if (isStatic || frozenTemplates.Length == 0)
                    return false;
                foreach (MobTemplate template in frozenTemplates)
                {
                    if (template == null || !NpcTemplateValidation.CanSpawn(template))
                        return false;
                }
            }
            else if (!isStatic && !HasSpawnableMob(hash, Math.Max(1, minLevel)))
                return false;
            if (!_grid.TryResolveCell(centre, out Cell cell))
                return false;

            var entry = new PlayfieldSpawnEntry
            {
                DistrictIndex = district.DistrictIndex,
                HashText = hash,
                MinLevel = minLevel,
                MaxLevel = maxLevel,
                RespawnChance = district.RespawnChance,
                RespawnTime = DistrictRespawnSeconds(district),
                AngleW = DistrictFacingWidthDegrees,
                Position = [centre.xf, centre.yf, centre.zf],
                Radius = radius,
                AssistanceRadius = Math.Max(0, assistanceRadius)
            };
            var point = new HashSpawnPoint(
                hash,
                [new SpawnSite(centre, 0, DistrictFacingWidthDegrees, radius)],
                DistrictRespawnSeconds(district),
                district.RespawnChance,
                minLevel,
                maxLevel,
                cell.Id,
                entry,
                isStatic)
            {
                RespawnsAfterDeath = respawns,
                FrozenTemplates = frozenTemplates == null ? null : (MobTemplate[])frozenTemplates.Clone(),
                OnBranchSpawned = onBranchSpawned,
                IsBranchKilled = isBranchKilled,
                Configure = configure
            };
            if (AllFrozenBranchesKilled(point))
            {
                point.Retired = true;
            }

            if (!_pointsByCell.TryGetValue(cell.Id, out List<HashSpawnPoint>? list))
            {
                list = new List<HashSpawnPoint>();
                _pointsByCell[cell.Id] = list;
            }

            list.Add(point);
            AddPoint(point);
            _hasPlacedPoints = true;
            // Placed after load, in the same order on every instance building the same dungeon: keyed the same way, so a
            // zone swap's spawn state finds it.
            AssignKey(point);
            ApplyInheritedState(point);
            return true;
        }

        /// <summary>
        /// Adds one spawn point placed after the playfield loaded that belongs to no district: a static dungeon's captured
        /// monster, standing at <paramref name="centre"/> facing <paramref name="facingDegrees"/>, at <paramref name="level"/>.
        /// With <paramref name="respawnSeconds"/> null its NPC spawns once and never again after it is killed; otherwise a
        /// killed NPC comes back at its own spot after that many seconds, every time. The point never wanders.
        /// <paramref name="assistanceRadius"/> and <paramref name="configure"/> are as for <see cref="AddPlacedDistrictPoint"/>.
        /// Keyed like every placed point, so a zone swap's spawn state finds it.
        /// </summary>
        internal bool AddPlacedPoint(string hash, Vector3 centre, int facingDegrees, int? respawnSeconds, int level,
            int assistanceRadius = 0, Action<NpcCharacter>? configure = null)
        {
            if (level <= 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (respawnSeconds is < 0)
                throw new ArgumentOutOfRangeException(nameof(respawnSeconds));
            if (string.IsNullOrEmpty(hash) || _grid == null)
                return false;

            bool isStatic = _gameData.IsStaticSpawnHash(hash);
            if (!isStatic && !HasSpawnableMob(hash, level))
                return false;
            if (!_grid.TryResolveCell(centre, out Cell cell))
                return false;

            int facing = ((facingDegrees % 360) + 360) % 360;
            var entry = new PlayfieldSpawnEntry
            {
                DistrictIndex = -1,
                HashText = hash,
                MinLevel = level,
                MaxLevel = level,
                RespawnChance = 100,
                RespawnTime = respawnSeconds ?? 0,
                AngleW = 0,
                Position = [centre.xf, centre.yf, centre.zf],
                Radius = 0f,
                AssistanceRadius = Math.Max(0, assistanceRadius)
            };
            var point = new HashSpawnPoint(
                hash,
                [new SpawnSite(centre, facing, 0, 0f)],
                respawnSeconds ?? 0,
                100,
                level,
                level,
                cell.Id,
                entry,
                isStatic)
            {
                RespawnsAfterDeath = respawnSeconds != null,
                Configure = configure
            };

            if (!_pointsByCell.TryGetValue(cell.Id, out List<HashSpawnPoint>? list))
            {
                list = new List<HashSpawnPoint>();
                _pointsByCell[cell.Id] = list;
            }

            list.Add(point);
            AddPoint(point);
            _hasPlacedPoints = true;
            AssignKey(point);
            ApplyInheritedState(point);
            return true;
        }

        // ------------------------------------------------------------------ spawn reload: staff spawn edits, applied live

        /// <summary>
        /// A running playfield's spawn reload, prepared off the tick (<see cref="PrepareReload"/>) from files read off the
        /// tick: the new Spawns.json and Districts.json, every Spawns.json entry (the loaded ones and the new ones) hashed,
        /// and each new district's spawn table hashed.
        /// </summary>
        internal sealed class SpawnReloadInput
        {
            internal SpawnReloadInput(PlayfieldSpawnsData spawns, PlayfieldDistrictsData? districts,
                Dictionary<PlayfieldSpawnEntry, string> entryFingerprints, Dictionary<int, string> districtFingerprints)
            {
                Spawns = spawns;
                Districts = districts;
                EntryFingerprints = entryFingerprints;
                DistrictFingerprints = districtFingerprints;
            }

            internal PlayfieldSpawnsData Spawns { get; }

            internal PlayfieldDistrictsData? Districts { get; }

            internal Dictionary<PlayfieldSpawnEntry, string> EntryFingerprints { get; }

            internal Dictionary<int, string> DistrictFingerprints { get; }
        }

        /// <summary>
        /// False for a playfield whose points are placed by a generated or static dungeon, or not loaded yet: its spawns are
        /// not reloaded. Readable off the tick; <see cref="ApplyReload"/> checks again on it.
        /// </summary>
        internal bool IsReloadable => _initialized && !_hasPlacedPoints;

        /// <summary>
        /// Hashes what <see cref="ApplyReload"/> compares, so the tick does no serializing. Off the tick: reads only the
        /// loaded and new file data, which nothing changes once read.
        /// </summary>
        internal SpawnReloadInput PrepareReload(PlayfieldSpawnsData spawns, PlayfieldDistrictsData? districts)
        {
            ArgumentNullException.ThrowIfNull(spawns);
            var prints = new Dictionary<PlayfieldSpawnEntry, string>();
            foreach (PlayfieldSpawnsData? data in new[] { _loadedSpawns, spawns })
            {
                foreach (PlayfieldSpawnEntry entry in data?.Spawns ?? [])
                {
                    if (entry != null)
                        prints.TryAdd(entry, Fingerprint(entry));
                }
            }

            return new SpawnReloadInput(spawns, districts, prints, DistrictFingerprints(districts));
        }

        /// <summary>
        /// Rebuilds this playfield's Spawns.json and Districts.json points from <paramref name="input"/> without tearing the
        /// playfield down. A Spawns.json point whose entry is unchanged (same key: hash, position and stack ordinal; same
        /// entry) is kept with its NPCs, state and timers; so is every point of a district whose spawn table is unchanged.
        /// A removed or changed point's NPCs and statics are despawned (no corpse, loot or XP), except a charmed pet, which
        /// stays its owner's and only leaves the point. A changed point that was waiting out a death timer keeps the timer
        /// (capped at its new respawn time), so an edit cannot respawn a just-killed NPC. Added points spawn on their normal
        /// rules once their cell is awake. NpcScripts bindings and SpawnExclusions.json apply as at load. Tick thread only.
        /// </summary>
        internal AORebirth.Modules.Content.PlayfieldSpawnReload ApplyReload(SpawnReloadInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            int playfieldId = _playfield.Identity.Instance;
            if (!_initialized || _grid == null || _locality == null)
                return NotReloaded(playfieldId, "its spawn points are not loaded yet");
            if (_hasPlacedPoints)
                return NotReloaded(playfieldId, "a generated or static dungeon places its own spawn points; they are not reloaded");

            var skips = new List<string>();
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var next = new List<HashSpawnPoint>(_allPoints.Count);
            var retiring = new List<HashSpawnPoint>();
            var oldByKey = new Dictionary<string, HashSpawnPoint>(StringComparer.Ordinal);
            var oldByDistrict = new Dictionary<int, List<HashSpawnPoint>>();
            foreach (HashSpawnPoint point in _allPoints)
            {
                if (point.FromDistrictsFile)
                {
                    if (!oldByDistrict.TryGetValue(point.Source.DistrictIndex, out List<HashSpawnPoint>? group))
                        oldByDistrict[point.Source.DistrictIndex] = group = new List<HashSpawnPoint>();
                    group.Add(point);
                }
                else if (point.Key == null || !oldByKey.TryAdd(point.Key, point))
                {
                    retiring.Add(point);
                }
            }

            int kept = 0, added = 0, changed = 0, removed = retiring.Count;
            DateTime now = DateTime.UtcNow;
            foreach (HashSpawnPoint point in BuildSpawnPoints(input.Spawns, _grid, skips))
            {
                AssignKey(point, ordinals);
                point.Fingerprint = input.EntryFingerprints.GetValueOrDefault(point.Source) ?? Fingerprint(point.Source);
                if (oldByKey.Remove(point.Key!, out HashSpawnPoint? old))
                {
                    string? before = old.Fingerprint ?? input.EntryFingerprints.GetValueOrDefault(old.Source);
                    if (string.Equals(before, point.Fingerprint, StringComparison.Ordinal))
                    {
                        old.Fingerprint = before;
                        next.Add(old);
                        kept++;
                        continue;
                    }

                    CarryDeathTimer(old, point, now);
                    retiring.Add(old);
                    changed++;
                }
                else
                {
                    added++;
                }

                next.Add(point);
            }

            removed += oldByKey.Count;
            retiring.AddRange(oldByKey.Values);

            // Districts as a whole: a load shuffles a district's points over its locations, so no point maps to one before.
            var keptDistricts = new HashSet<int>();
            foreach (PlayfieldDistrictEntry district in input.Districts?.Districts ?? [])
            {
                if (district == null)
                    continue;

                int index = district.DistrictIndex;
                if (_districtFingerprints.TryGetValue(index, out string? before)
                    && input.DistrictFingerprints.TryGetValue(index, out string? after)
                    && string.Equals(before, after, StringComparison.Ordinal))
                {
                    if (keptDistricts.Add(index) && oldByDistrict.Remove(index, out List<HashSpawnPoint>? same))
                    {
                        foreach (HashSpawnPoint point in same)
                        {
                            NextOrdinal(point, ordinals);
                            next.Add(point);
                            kept++;
                        }
                    }

                    continue;
                }

                int ignored = 0;
                foreach (HashSpawnPoint point in BuildDistrictPoints(district, _grid, skips, ref ignored))
                {
                    AssignKey(point, ordinals);
                    next.Add(point);
                    added++;
                }
            }

            foreach (List<HashSpawnPoint> group in oldByDistrict.Values)
            {
                retiring.AddRange(group);
                removed += group.Count;
            }

            // Skips of a kept district were not looked at again: they stay reported.
            foreach (string skip in _reportedSkips)
            {
                if (skip.StartsWith("district ", StringComparison.Ordinal)
                    && int.TryParse(skip.AsSpan(9, Math.Max(0, skip.IndexOf(' ', 9) - 9)), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int index)
                    && keptDistricts.Contains(index))
                    skips.Add(skip);
            }

            var problems = new List<string>();
            foreach (string skip in skips)
            {
                if (!_reportedSkips.Contains(skip))
                    problems.Add("skipped " + skip);
            }

            _reportedSkips = new HashSet<string>(skips, StringComparer.Ordinal);
            _loadedSpawns = input.Spawns;
            _districtFingerprints = new Dictionary<int, string>(input.DistrictFingerprints);

            int despawned = 0;
            if (added + changed + removed > 0)
            {
                foreach (HashSpawnPoint point in retiring)
                    despawned += Retire(point);

                _allPoints.Clear();
                _pointsByCell.Clear();
                _pointsByHash.Clear();
                foreach (HashSpawnPoint point in next)
                    Insert(point);
                _keyOrdinals.Clear();
                foreach (KeyValuePair<string, int> pair in ordinals)
                    _keyOrdinals[pair.Key] = pair.Value;

                _playfield.GetService<NpcScripts.NpcScriptCatalog>()?.ReportUnmatchedSpawnPoints(_playfield.ResourceId,
                    next.ConvertAll(point => point.Key!));
                AttachSpawnCells();
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Spawn points reloaded playfield={0} kept={1} added={2} changed={3} removed={4} despawned={5} newSkips={6}",
                playfieldId, kept, added, changed, removed, despawned, problems.Count));
            return new AORebirth.Modules.Content.PlayfieldSpawnReload(playfieldId, true, kept, added, changed, removed,
                despawned, problems);
        }

        internal static AORebirth.Modules.Content.PlayfieldSpawnReload NotReloaded(int playfieldId, string why)
            => new(playfieldId, false, 0, 0, 0, 0, 0, [why]);

        /// <summary>
        /// Takes a removed or changed point out of play: its NPCs and statics despawn (no corpse, loot or XP). A charmed pet
        /// is out with its owner: it stays theirs and only stops belonging to the point. The number despawned.
        /// </summary>
        private int Retire(HashSpawnPoint point)
        {
            int despawned = 0;
            foreach (Dynel member in point.MembersSnapshot())
            {
                _pointBySpawned.Remove(member);
                point.RemoveMember(member);
                if (member is NpcCharacter npc)
                {
                    npc.Died -= OnSpawnedDied;
                    if (npc.Pet != null || npc.Playfield == null)
                        continue;

                    LogSpawn("reload-despawn", point, npc.Identity.Instance, npc.Name, npc.Position);
                    _spawnService.DespawnNpc(npc);
                    despawned++;
                }
                else if (member is StaticDynel dynel && dynel.Playfield != null)
                {
                    LogSpawn("reload-despawn", point, dynel.Identity.Instance, dynel.Template.Name, dynel.Position);
                    _spawnService.DespawnStatic(dynel);
                    despawned++;
                }
            }

            point.Retired = true;
            return despawned;
        }

        /// <summary>A changed point replacing one that waits out a death timer waits too, at most its own respawn time.</summary>
        private static void CarryDeathTimer(HashSpawnPoint old, HashSpawnPoint replacement, DateTime now)
        {
            if (!old.HasSpawned || old.State != HashSpawnState.Dead || old.MemberCount > 0)
                return;

            replacement.HasSpawned = true;
            replacement.State = HashSpawnState.Dead;
            DateTime latest = now.AddSeconds(replacement.RespawnTimeSeconds);
            replacement.NextSpawnTime = old.NextSpawnTime < latest ? old.NextSpawnTime : latest;
        }

        private static readonly JsonSerializerOptions FingerprintJsonOptions = new();

        private static string Fingerprint<T>(T value)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(value, FingerprintJsonOptions)), 0, 16);

        /// <summary>
        /// Each district's spawn table hashed (levels, respawn chance and time, spawn infos and locations), by district
        /// index; districts sharing an index hash together. Its name, music and fight mode do not count.
        /// </summary>
        private static Dictionary<int, string> DistrictFingerprints(PlayfieldDistrictsData? data)
        {
            var grouped = new Dictionary<int, List<object>>();
            foreach (PlayfieldDistrictEntry district in data?.Districts ?? [])
            {
                if (district == null)
                    continue;
                if (!grouped.TryGetValue(district.DistrictIndex, out List<object>? group))
                    grouped[district.DistrictIndex] = group = new List<object>();
                group.Add(new
                {
                    district.NpcMinLevel,
                    district.NpcMaxLevel,
                    district.RespawnChance,
                    district.RespawnTime,
                    district.SpawnInfos,
                    district.SpawnPoints
                });
            }

            var prints = new Dictionary<int, string>(grouped.Count);
            foreach (KeyValuePair<int, List<object>> pair in grouped)
                prints[pair.Key] = Fingerprint(pair.Value);
            return prints;
        }

        // ------------------------------------------------------------------ zone swap: spawn state carried across

        // Ordinals per base key, shared by load-time and placed points so stacked points number the same everywhere.
        private readonly Dictionary<string, int> _keyOrdinals = new(StringComparer.Ordinal);

        // What the instance that handed this playfield over had, by point key; applied as each point appears.
        private Dictionary<string, AORebirth.World.Contracts.V1.SpawnPointState>? _inherited;

        private void AssignKey(HashSpawnPoint point) => AssignKey(point, _keyOrdinals);

        private static void AssignKey(HashSpawnPoint point, Dictionary<string, int> ordinals)
            => point.Key = NpcScripts.SpawnPointKey.Build(point.HashText, point.Sites[0].Centre.xf,
                point.Sites[0].Centre.yf, point.Sites[0].Centre.zf, NextOrdinal(point, ordinals));

        /// <summary>Counts <paramref name="point"/> among the points stacked at its hash and position; its 1-based ordinal.</summary>
        private static int NextOrdinal(HashSpawnPoint point, Dictionary<string, int> ordinals)
        {
            Vector3 centre = point.Sites[0].Centre;
            string baseKey = NpcScripts.SpawnPointKey.Build(point.HashText, centre.xf, centre.yf, centre.zf, 1);
            int ordinal = ordinals.TryGetValue(baseKey, out int seen) ? seen + 1 : 1;
            ordinals[baseKey] = ordinal;
            return ordinal;
        }

        /// <summary>
        /// The points not in their starting state, for a zone swap hand-over: killed ones with when they may spawn again,
        /// and one-shot ones gone for good. Living NPCs are left out; they start fresh where the playfield is taken over.
        /// Playfield thread.
        /// </summary>
        internal AORebirth.World.Contracts.V1.PlayfieldSpawnState CaptureHandOverState()
        {
            var state = new AORebirth.World.Contracts.V1.PlayfieldSpawnState
            {
                PlayfieldId = _playfield.Identity.Instance,
                CapturedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            foreach (HashSpawnPoint point in _allPoints)
            {
                if (point.Key == null || !(point.Retired || (point.HasSpawned && point.State == HashSpawnState.Dead)))
                    continue;
                DateTime next = point.NextSpawnTime.Kind == DateTimeKind.Utc ? point.NextSpawnTime : point.NextSpawnTime.ToUniversalTime();
                state.Points.Add(new AORebirth.World.Contracts.V1.SpawnPointState
                {
                    Key = point.Key,
                    NextSpawnUnixMs = next == DateTime.MaxValue ? long.MaxValue : new DateTimeOffset(next).ToUnixTimeMilliseconds(),
                    Retired = point.Retired
                });
            }

            return state;
        }

        /// <summary>Takes the spawn state World handed this instance for the playfield (zone swap), and applies it.</summary>
        private void TakeInheritedState()
        {
            AORebirth.World.Contracts.V1.PlayfieldSpawnState? state =
                _playfield.GetService<PlayfieldManager>()?.World?.TakeHandedSpawnState(_playfield.Identity.Instance);
            if (state == null || state.Points.Count == 0)
                return;
            _inherited = new Dictionary<string, AORebirth.World.Contracts.V1.SpawnPointState>(StringComparer.Ordinal);
            foreach (AORebirth.World.Contracts.V1.SpawnPointState point in state.Points)
                if (!string.IsNullOrEmpty(point.Key))
                    _inherited[point.Key] = point;
            int applied = 0;
            foreach (HashSpawnPoint point in _allPoints)
                if (ApplyInheritedState(point))
                    applied++;
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Spawn state carried over playfield={0} points={1} applied={2} (zone swap)", _playfield.Identity.Instance,
                state.Points.Count, applied));
        }

        private bool ApplyInheritedState(HashSpawnPoint point)
        {
            if (_inherited == null || point.Key == null || !_inherited.Remove(point.Key, out var carried))
                return false;
            // Dead as it was: it waits out the rest of its timer and then rolls its respawn chance like any respawn.
            point.State = HashSpawnState.Dead;
            point.HasSpawned = true;
            point.NextSpawnTime = carried.NextSpawnUnixMs >= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                ? DateTime.MaxValue
                : DateTimeOffset.FromUnixTimeMilliseconds(carried.NextSpawnUnixMs).UtcDateTime;
            if (carried.Retired)
                point.Retired = true;
            return true;
        }

        void AddPoint(HashSpawnPoint point)
        {
            _allPoints.Add(point);
            if (!_pointsByHash.TryGetValue(point.HashText, out List<HashSpawnPoint>? byHash))
                _pointsByHash[point.HashText] = byHash = new List<HashSpawnPoint>();
            byHash.Add(point);
        }

        /// <summary>
        /// Where <paramref name="hash"/> is on this playfield, nearest <paramref name="from"/>: a living NPC of a spawn point
        /// with that spawn hash, else such a point's first site (nothing alive there now); when no point has the hash, a
        /// living spawned NPC whose template hash it is. False when none. Playfield thread.
        /// </summary>
        public bool TryFindNearest(string hash, Vector3 from, out Vector3 at)
        {
            at = default;
            if (string.IsNullOrEmpty(hash))
                return false;

            double best = double.MaxValue;
            Vector3 found = default;
            void Consider(Vector3 position)
            {
                double dx = position.x - from.x, dz = position.z - from.z, distance = (dx * dx) + (dz * dz);
                if (distance < best)
                    (best, found) = (distance, position);
            }

            if (_pointsByHash.TryGetValue(hash, out List<HashSpawnPoint>? points))
            {
                foreach (HashSpawnPoint point in points)
                    foreach (Dynel member in point.MembersSnapshot())
                        if (member is NpcCharacter { IsDead: false } npc)
                            Consider(npc.Position);

                if (best == double.MaxValue)
                    foreach (HashSpawnPoint point in points)
                        Consider(point.Sites[0].Centre);
            }
            else
            {
                foreach (Dynel spawned in _pointBySpawned.Keys)
                    if (spawned is NpcCharacter { IsDead: false } npc && string.Equals(npc.MobTemplate?.Hash, hash, StringComparison.Ordinal))
                        Consider(npc.Position);
            }

            at = found;
            return best < double.MaxValue;
        }

        private bool HasInactiveEvent(PlayfieldSpawnEntry entry)
        {
            PlayfieldHashSpawnExtensionEvent[]? events = entry.Extensions?.Events;
            if (events == null)
                return false;

            for (int i = 0; i < events.Length; i++)
            {
                string? name = events[i]?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!events[i].Active || (!_halloweenEnabled && IsHalloweenEvent(name)))
                    return true;
            }

            return false;
        }

        // Exact event identities from Spawns.json; do not classify unrelated events by a name prefix.
        internal static bool IsHalloweenEvent(string name) => name is
            "halloween_all" or "halloween2019_all" or "halloween_uph" or
            "halloween_decor" or "halloween_pvp_uph" or "halloween_pvp_uph_boss" or
            "halloween_quests" or "halloween_fc_shop" or "halloween_buttons";

        private static SpawnSite[] BuildSites(PlayfieldSpawnEntry entry, Vector3 primaryPosition)
        {
            List<SpawnSite> sites = new()
            {
                new SpawnSite(
                    primaryPosition,
                    entry.Angle,
                    entry.AngleW,
                    entry.Radius)
            };

            PlayfieldRotationSpawnPoint[]? additional = entry.AdditionalPoints;
            if (additional == null)
                return sites.ToArray();

            foreach (PlayfieldRotationSpawnPoint extra in additional)
            {
                if (extra?.Position == null || extra.Position.Length < 3)
                    continue;

                sites.Add(
                    new SpawnSite(
                        new Vector3(extra.Position[0], extra.Position[1], extra.Position[2]),
                        extra.Angle,
                        extra.AngleW,
                        extra.Radius));
            }

            return sites.ToArray();
        }

        private void OnCellSleep(int cellId)
        {
            if (_pointsByCell.TryGetValue(cellId, out List<HashSpawnPoint>? points))
            {
                foreach (HashSpawnPoint point in points)
                    DespawnForSleep(point);
            }

            // A member standing here for a point whose own cell already slept (kept up because a player was near it)
            // sleeps with this cell instead.
            _sleepOccupants.Clear();
            _sleepStragglerPoints.Clear();
            _locality?.Grid.CopyOccupants(cellId, _sleepOccupants);
            for (int i = 0; i < _sleepOccupants.Count; i++)
            {
                if (_pointBySpawned.TryGetValue(_sleepOccupants[i], out HashSpawnPoint? point)
                    && point.CellId != cellId
                    && !_locality!.IsCellAwake(point.CellId)
                    && !_sleepStragglerPoints.Contains(point))
                    _sleepStragglerPoints.Add(point);
            }

            for (int i = 0; i < _sleepStragglerPoints.Count; i++)
                DespawnForSleep(_sleepStragglerPoints[i]);

            _sleepOccupants.Clear();
            _sleepStragglerPoints.Clear();
        }

        private void OnCellTick(int cellId)
        {
            if (!_pointsByCell.TryGetValue(cellId, out List<HashSpawnPoint>? points))
                return;

            EvaluateCell(points);
        }

        private void OnIndoorSpawnTick()
        {
            foreach (KeyValuePair<int, List<HashSpawnPoint>> pair in _pointsByCell)
                EvaluateCell(pair.Value);
        }

        private void EvaluateCell(List<HashSpawnPoint> points)
        {
            int budget = _spawnRate;
            if (budget <= 0)
                return;

            DateTime now = DateTime.UtcNow;
            foreach (HashSpawnPoint point in points)
            {
                if (budget <= 0)
                    return;

                if (!ShouldSpawn(point, now))
                    continue;

                if (!TrySpawn(point))
                    continue;

                budget--;
            }
        }

        private bool ShouldSpawn(HashSpawnPoint point, DateTime now)
        {
            if (point.Retired)
                return false;

            RecoverOrphanedSpawn(point);

            if (point.MemberCount > 0)
                return false;

            if (AllFrozenBranchesKilled(point))
            {
                point.Retired = true;
                return false;
            }

            if (point.State == HashSpawnState.Alive)
                return true;

            return now >= point.NextSpawnTime;
        }

        private static bool AllFrozenBranchesKilled(HashSpawnPoint point)
        {
            if (point.FrozenTemplates == null || point.IsBranchKilled == null)
                return false;
            for (int i = 0; i < point.FrozenTemplates.Length; i++)
            {
                if (!point.IsBranchKilled(i))
                    return false;
            }
            return true;
        }

        private bool TrySpawn(HashSpawnPoint point, bool skipRespawnChance = false)
        {
            RecoverOrphanedSpawn(point);

            // A 0% point never spawns, first time included.
            bool firstSpawn = !point.HasSpawned && point.RespawnChance > 0;
            if (point.State == HashSpawnState.Dead && !skipRespawnChance && !firstSpawn && !TryConsumeRespawnChance(point))
                return false;

            try
            {
                int? level = point.HoldsRoll ? point.RolledLevel : RollLevel(point);
                int spawned = point.IsStatic
                    ? SpawnStaticBranches(point, level)
                    : SpawnMobBranches(point, level);
                point.HoldsRoll = false;
                point.HeldTemplates = null;
                if (spawned == 0)
                    return false;

                point.RolledLevel = level;
                point.State = HashSpawnState.Alive;
                point.HasSpawned = true;
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Hash spawn failed hash={0} cell={1} playfield={2}",
                        point.HashText,
                        point.CellId,
                        _playfield.Identity.Instance));
                return false;
            }
        }

        /// <summary>
        /// Dead points roll once per elapsed respawn window (catch-up while asleep).
        /// Stops at the first success so at most one spawn is created.
        /// </summary>
        private static bool TryConsumeRespawnChance(HashSpawnPoint point)
        {
            DateTime now = DateTime.UtcNow;
            int windowSeconds = point.RespawnTimeSeconds;
            int rolls = 1;
            if (windowSeconds > 0 && now > point.NextSpawnTime)
            {
                double elapsedSeconds = (now - point.NextSpawnTime).TotalSeconds;
                rolls = 1 + (int)(elapsedSeconds / windowSeconds);
                if (rolls > MaxCatchUpRolls)
                    rolls = MaxCatchUpRolls;
                else if (rolls < 1)
                    rolls = 1;
            }

            for (int i = 0; i < rolls; i++)
            {
                if (PassesRespawnChance(point))
                    return true;
            }

            point.NextSpawnTime = windowSeconds > 0
                ? now.AddSeconds(windowSeconds)
                : now;
            return false;
        }

        private static bool PassesRespawnChance(HashSpawnPoint point)
        {
            if (point.RespawnChance >= 100)
                return true;
            if (point.RespawnChance <= 0)
                return false;

            return Random.Shared.Next(100) < point.RespawnChance;
        }

        /// <summary>
        /// Inclusive random level from the spawn entry range. Returns null when the range is unset/invalid
        /// so <see cref="SpawnService"/> keeps the template level.
        /// </summary>
        private static int? RollLevel(HashSpawnPoint point)
        {
            int min = point.MinLevel;
            int max = point.MaxLevel;
            if (max < min)
                (min, max) = (max, min);
            if (max < 1)
                return null;
            if (min < 1)
                min = 1;

            if (min == max)
                return min;

            return Random.Shared.Next(min, max + 1);
        }

        private static void PickSpawnTransform(
            HashSpawnPoint point,
            out Vector3 position,
            out Quaternion heading)
        {
            SpawnSite site = point.Sites[Random.Shared.Next(point.Sites.Length)];
            heading = SpawnHeading.Sample(
                site.FacingDegrees,
                site.WidthDegrees,
                Random.Shared.NextDouble());
            position = site.Centre;
            if (site.Radius <= 0f)
                return;

            // Uniform disk on XZ; Y stays at the site centre.
            double angle = Random.Shared.NextDouble() * (Math.PI * 2.0);
            double distance = Math.Sqrt(Random.Shared.NextDouble()) * site.Radius;
            position = new Vector3(
                site.Centre.xf + (float)(Math.Cos(angle) * distance),
                site.Centre.yf,
                site.Centre.zf + (float)(Math.Sin(angle) * distance));
        }

        /// <summary>A random navmesh point within <paramref name="radius"/> of <paramref name="anchor"/>, reachable from it.</summary>
        private bool TryScatter(Vector3 anchor, float radius, out Vector3 scattered)
        {
            scattered = anchor;
            if (_playfield.Pathfinder is not { } finder)
                return false;

            var centre = new System.Numerics.Vector3((float)anchor.x, (float)anchor.y, (float)anchor.z);
            if (!finder.TryFindRandomPointWithin(centre, radius, Random.Shared, out System.Numerics.Vector3 point))
                return false;

            scattered = new Vector3(point.X, point.Y, point.Z);
            return true;
        }

        private int SpawnMobBranches(HashSpawnPoint point, int? level)
        {
            using Metrics.TickStallWatch.StageScope stageScope = Metrics.TickStallWatch.Enter("spawn.templates", point.CellId);
            IReadOnlyList<MobTemplate> templates;
            if (point.FrozenTemplates != null)
            {
                templates = point.FrozenTemplates;
            }
            else if (point.HoldsRoll && point.HeldTemplates != null)
            {
                templates = point.HeldTemplates;
            }
            else
            {
                var resolved = new List<MobTemplate>();
                _gameData.CollectMobSpawns(point.HashText, level, resolved);
                templates = resolved;
            }
            int spawned = 0;
            for (int i = 0; i < templates.Count; i++)
            {
                if (point.IsBranchKilled?.Invoke(i) == true || !NpcTemplateValidation.CanSpawn(templates[i]))
                    continue;

                Metrics.TickStallWatch.Stage("spawn.place", point.CellId);
                PickSpawnTransform(point, out Vector3 position, out Quaternion heading);
                Action<NpcCharacter>? configure = point.Configure;
                GameplayWander? wander = point.Wanders ? _playfield.GetService<GameplaySettings>()?.Wander : null;
                // A body that cannot move (plants, towers, turrets) stays exactly on its point.
                bool immobile = templates[i].Stats.TryGetValue((int)CharacterStat.MonsterData,
                        out int monsterData)
                    && _gameData.TryGetMonsterFeatures(monsterData, out int features)
                    && MonsterBodyFeatures.IsImmobile(features);
                if (wander is { Radius: > 0f } && !immobile)
                {
                    // Scattered over the walkable area around the point, so a district's NPCs are spread out from the
                    // start; the point itself stays their home to wander around and leash to.
                    Vector3 anchor = point.Sites[0].Centre;
                    if (TryScatter(anchor, wander.Radius, out Vector3 scattered))
                        position = scattered;
                    Action<NpcCharacter>? pointConfigure = point.Configure;
                    configure = npc =>
                    {
                        pointConfigure?.Invoke(npc);
                        npc.Brain?.EnableWander(anchor, wander.Radius, wander.MinPauseSeconds, wander.MaxPauseSeconds);
                    };
                }

                NpcCharacter character = _spawnService.SpawnMob(
                    templates[i],
                    position,
                    heading,
                    level,
                    SpawnSource.HashSpawn,
                    spawnHash: point.HashText,
                    configure: configure,
                    spawnPointKey: point.Key);
                point.AddMember(character);
                _pointBySpawned[character] = point;
                character.Died += OnSpawnedDied;
                point.OnBranchSpawned?.Invoke(character, i);
                LogSpawn("spawn", point, character.Identity.Instance, character.Name, position);
                spawned++;
            }

            return spawned;
        }

        private int SpawnStaticBranches(HashSpawnPoint point, int? level)
        {
            var instances = new List<HashInstance>();
            if (point.HoldsRoll && point.RolledStatics != null)
                instances.AddRange(point.RolledStatics);
            else if (point.Source.StaticTemplateId is int templateId)
                instances.Add(new HashInstance(point.HashText, new[] { templateId }));
            else
                _gameData.CollectHashSpawns(point.HashText, instances);
            point.RolledStatics = instances.ToArray();
            int spawned = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                PickSpawnTransform(point, out Vector3 position, out Quaternion heading);
                // A one-template family stands at that template's own QL; the point's level range only picks within a
                // QL-range family. TRRS points roll 1-200, but the Rho Signal Relay 284293 activates only a QL 1
                // Teleport Ring 284431, its template QL.
                int? instanceLevel = instances[i].TemplateIds.Length == 1
                    ? _spawnService.StaticTemplateQuality(instances[i].TemplateIds[0]) ?? level
                    : level;
                StaticDynel? dynel = _spawnService.SpawnStaticInstance(
                    instances[i],
                    position,
                    heading,
                    instanceLevel,
                    SpawnSource.HashSpawn,
                    instances[i].Hash);
                if (dynel == null)
                    continue;

                point.AddMember(dynel);
                _pointBySpawned[dynel] = point;
                LogSpawn("spawn", point, dynel.Identity.Instance, dynel.Template.Name, position);
                spawned++;
            }

            return spawned;
        }

        private bool HasSpawnableMob(string hash, int level)
        {
            if (!_gameData.CanResolveMobHash(hash))
                return false;

            var templates = new List<MobTemplate>();
            _gameData.CollectMobSpawns(hash, level, templates);
            for (int i = 0; i < templates.Count; i++)
            {
                if (NpcTemplateValidation.CanSpawn(templates[i]))
                    return true;
            }

            return false;
        }

        private void DespawnForSleep(HashSpawnPoint point)
        {
            Dynel[] members = point.MembersSnapshot();
            if (members.Length == 0)
                return;

            // The point sleeps with its own cell, wherever its members are. A member serving a player as a charmed pet
            // is out with its owner, not idle here: the point stays awake until the charm ends and it comes home.
            // Nor is a living scripted or Persistent NPC: it keeps running with no player near (its own cell stays Cold,
            // see CellHeatScheduler), so it stays even when it has wandered out of the point's cell.
            // Nor is a living member that is fighting or walking home from a fight: a rotation spawn can stand cells away
            // from its primary point (Remur-Nefer REMU in 4311 fights at (256, 642), four cells from (215, 495)), and
            // the home cell going to sleep took it out of the world mid-fight (bug #433).
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] is NpcCharacter { Pet: not null } or NpcCharacter { Scripts: not null, IsDead: false })
                    return;
                if (members[i] is NpcCharacter { IsDead: false } persistent && persistent.HasFeature(NpcFeature.Persistent))
                    return;
                if (members[i] is NpcCharacter { IsDead: false } npc && (npc.IsInCombat || npc.IsEvading))
                    return;
                // Nor is a member standing in a cell that is still awake: a rotation spawn can roll a site cells from
                // its point, right next to a player, and vanished when the point's own empty cell slept (Mooching
                // Dryad MODY in 4872 at (1702, 2928), point at (1750, 2835)). It sleeps with the cell it stands in.
                if (members[i] is not NpcCharacter { IsDead: true } && members[i].Cell is { } cell
                    && _locality?.IsCellAwake(cell.Id) == true)
                    return;
            }

            // Stay Alive — sleep-despawned; respawn on next awake tick with the same roll (its survivors only), not a new one.
            point.State = HashSpawnState.Alive;
            if (point.FrozenTemplates == null)
            {
                MobTemplate[] survivors = members.OfType<NpcCharacter>().Select(npc => npc.MobTemplate)
                    .OfType<MobTemplate>().ToArray();
                point.HeldTemplates = survivors.Length > 0 ? survivors : null;
                point.HoldsRoll = point.IsStatic ? point.RolledStatics != null : point.HeldTemplates != null;
            }

            for (int i = 0; i < members.Length; i++)
            {
                Dynel spawned = members[i];
                _pointBySpawned.Remove(spawned);
                point.RemoveMember(spawned);
                int instance = spawned.Identity.Instance;
                string? name = spawned switch
                {
                    NpcCharacter npc => npc.Name,
                    StaticDynel staticDynel => staticDynel.Template.Name,
                    _ => null
                };
                Vector3 position = spawned.Position;
                if (spawned is NpcCharacter character)
                {
                    character.Died -= OnSpawnedDied;
                    _spawnService.DespawnNpc(character);
                }
                else if (spawned is StaticDynel dynel)
                {
                    _spawnService.DespawnStatic(dynel);
                }

                LogSpawn("sleep-despawn", point, instance, name, position);
            }
        }

        private void OnSpawnedDied(Character character)
        {
            if (character is not NpcCharacter npc)
                return;
            if (!_pointBySpawned.TryGetValue(npc, out HashSpawnPoint? point))
                return;

            npc.Died -= OnSpawnedDied;
            _pointBySpawned.Remove(npc);
            point.RemoveMember(npc);
            int instance = npc.Identity.Instance;
            string? name = npc.Name;

            // Death removes the live dynel from the world; sleep path uses DespawnNpc explicitly.
            if (npc.Playfield != null)
                _spawnService.DespawnNpc(npc);

            MarkMemberDead(point, instance, name);
        }

        /// <summary>
        /// Removes a spawn point's static member as if it had died (GM <c>.despawn</c>): the point respawns on its
        /// normal timer once every member is gone, or retires when it does not respawn after death. False when this
        /// static is not owned by a spawn point.
        /// </summary>
        public bool TryDespawnStaticAsDeath(StaticDynel dynel)
        {
            ArgumentNullException.ThrowIfNull(dynel);
            if (!_pointBySpawned.TryGetValue(dynel, out HashSpawnPoint? point))
                return false;

            _pointBySpawned.Remove(dynel);
            point.RemoveMember(dynel);
            int instance = dynel.Identity.Instance;
            string? name = dynel.Template.Name;
            if (dynel.Playfield != null)
                _spawnService.DespawnStatic(dynel);

            MarkMemberDead(point, instance, name);
            return true;
        }

        /// <summary>
        /// Removes <paramref name="npc"/>'s spawn point members as if they died (no corpse, loot or XP, as GM
        /// <c>.despawn</c>) and spawns the point again now, skipping its respawn timer and chance: GM <c>.npc respawn</c>
        /// and the module RespawnNpc action. The new NPCs are built from the current catalogs. False, with the NPC left
        /// in place, when it is dead or has no spawn point that respawns (GM-spawned, pets, content placements, quest
        /// objectives, one-shot points). Tick thread only.
        /// </summary>
        public bool TryRespawnNow(NpcCharacter npc)
        {
            ArgumentNullException.ThrowIfNull(npc);
            _playfield.RequireTick();
            if (npc.IsDead
                || !_pointBySpawned.TryGetValue(npc, out HashSpawnPoint? point)
                || !point.RespawnsAfterDeath
                || point.Retired)
                return false;

            foreach (Dynel member in point.MembersSnapshot())
            {
                switch (member)
                {
                    case NpcCharacter other when !other.IsDead:
                        other.DespawnAsDeath();
                        break;
                    case StaticDynel staticMember:
                        TryDespawnStaticAsDeath(staticMember);
                        break;
                }
            }

            if (TrySpawn(point, skipRespawnChance: true))
                return true;

            _logger.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "Respawn now failed hash={0} cell={1} playfield={2}; the point waits out its respawn time",
                point.HashText,
                point.CellId,
                _playfield.Identity.Instance));
            return false;
        }

        /// <summary>A member died or was despawned as dead: once the point is empty it waits out its respawn time.</summary>
        void MarkMemberDead(HashSpawnPoint point, int instance, string? name)
        {
            if (point.MemberCount > 0)
                return;

            point.State = HashSpawnState.Dead;
            point.NextSpawnTime = DateTime.UtcNow.AddSeconds(point.RespawnTimeSeconds);
            if (!point.RespawnsAfterDeath)
                point.Retired = true;
            LogSpawnDeath(point, instance, name);
        }

        /// <summary>
        /// Clears ownership when a member was removed without raising <see cref="Character.Died"/>.
        /// The point respawns only after every member is gone.
        /// </summary>
        private void RecoverOrphanedSpawn(HashSpawnPoint point)
        {
            if (point.MemberCount == 0)
                return;

            Dynel[] members = point.MembersSnapshot();
            bool removed = false;
            for (int i = 0; i < members.Length; i++)
            {
                Dynel spawned = members[i];
                if (spawned.Playfield != null)
                    continue;

                if (spawned is NpcCharacter character)
                    character.Died -= OnSpawnedDied;
                _pointBySpawned.Remove(spawned);
                point.RemoveMember(spawned);
                removed = true;
            }

            if (!removed || point.MemberCount > 0)
                return;

            point.State = HashSpawnState.Dead;
            point.NextSpawnTime = DateTime.UtcNow.AddSeconds(point.RespawnTimeSeconds);
        }

        private void LogSpawn(
            string action,
            HashSpawnPoint point,
            int identityInstance,
            string? name,
            Vector3 position)
        {
            if (!LogUtil.HasDetail(DebugInfoDetail.Locality))
                return;

            LogUtil.Debug(
                DebugInfoDetail.Locality,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Playfield {0} hash-spawn {1} hash={2} cell={3} id={4} name={5} state={6} pos=({7:F1},{8:F1},{9:F1})",
                    _playfield.Identity.Instance,
                    action,
                    point.HashText,
                    point.CellId,
                    identityInstance,
                    name ?? string.Empty,
                    point.State,
                    position.xf,
                    position.yf,
                    position.zf));
        }

        private void LogSpawnDeath(HashSpawnPoint point, int identityInstance, string? name)
        {
            if (!LogUtil.HasDetail(DebugInfoDetail.Locality))
                return;

            LogUtil.Debug(
                DebugInfoDetail.Locality,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Playfield {0} hash-spawn death hash={1} cell={2} id={3} name={4} nextSpawn={5:o} respawnSec={6}",
                    _playfield.Identity.Instance,
                    point.HashText,
                    point.CellId,
                    identityInstance,
                    name ?? string.Empty,
                    point.NextSpawnTime,
                    point.RespawnTimeSeconds));
        }

    }
}
