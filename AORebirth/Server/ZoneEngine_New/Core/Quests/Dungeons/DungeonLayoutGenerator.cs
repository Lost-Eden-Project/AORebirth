namespace ZoneEngine_New.Core.Quests.Dungeons
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// A generated dungeon layout: the generator the client builds the building from and the ACG entrance identity it
    /// is sent under, the generator blob that keys caches, the arrival point, the seed it came from (door states roll
    /// from it), the style's doors (null when the style has none configured) and the mission's resolved population
    /// and objective.
    /// </summary>
    public sealed record DungeonLayout(AcgBuildingGenerator Generator, Identity Entrance, byte[] GeneratorPayload, Vector3 Spawn, int Seed, DungeonDoorStyle? Doors)
    {
        public IReadOnlyList<AORebirth.DungeonGenerator.DungeonFloorTransition> FloorTransitions { get; init; } = [];

        /// <summary>Arrival heading, facing into the entrance room away from the way-in door.</summary>
        public AORebirth.Core.Vector.Quaternion SpawnHeading { get; init; } = YawHeading(0f);

        internal static AORebirth.Core.Vector.Quaternion YawHeading(float yawRadians)
            => new(0, MathF.Sin(yawRadians * 0.5f), 0, MathF.Cos(yawRadians * 0.5f));
        public int? BossRoomIndex { get; init; }

        /// <summary>How many of the rooms farthest from the entrance the objective may stand in; 1: the farthest.</summary>
        public int ObjectiveRoomChoices { get; init; } = 1;

        public DungeonPopulationBlock Population { get; init; } = new();

        /// <summary>Rows every NPC corpse in the dungeon rolls besides its template's own table; empty: none.</summary>
        public IReadOnlyList<Mobs.MobItemTableEntry> MobLoot { get; init; } = [];
    }

    /// <summary>GameData/DungeonDoorStyles.json for one ACG style: door item templates and spawn-state chances.</summary>
    public sealed record DungeonDoorStyle(int DoorTemplate, int ExitTemplate, int OpenChancePercent, int LockedChancePercent);

    /// <summary>
    /// DungeonGeneration.json TokenReward as written: the sided token items by item hash (1 Clan, 2 Omni) and the
    /// reward-chance line. The generator resolves the hashes against ItemTemplates.json into a
    /// <see cref="MissionTokenRewardPolicy"/>.
    /// </summary>
    public sealed class MissionTokenRewardSetting
    {
        public Dictionary<int, string> SidedItems { get; set; } = [];
        public int ItemQuality { get; set; }

        /// <summary>The reward-chance line; {0} is the chance in percent (a composite format string).</summary>
        public string FeedbackText { get; set; } = string.Empty;

        /// <summary>The policy with each side's hash resolved to its single template id; null with the reason when it cannot be.</summary>
        internal MissionTokenRewardPolicy? Resolve(IGameData gameData, out string? problem)
        {
            problem = null;
            var items = new Dictionary<int, int>();
            foreach ((int side, string hash) in SidedItems ?? [])
            {
                HashInstance? family = gameData.ItemFamilies.FirstOrDefault(f => string.Equals(f.Hash, hash, StringComparison.Ordinal));
                if (family == null || family.TemplateIds.Length != 1)
                {
                    problem = "TokenReward.SidedItems[" + side + "] = " + hash + " is not a single-template item hash in ItemTemplates.json";
                    return null;
                }
                items[side] = family.TemplateIds[0];
            }
            var policy = new MissionTokenRewardPolicy
            {
                SidedItems = items, ItemQuality = ItemQuality,
                FeedbackText = FeedbackText
            };
            if (!policy.IsValid || policy.FormatFeedback(0) == null)
            {
                problem = "TokenReward needs one Clan (1) and one Omni (2) hash, a positive ItemQuality and a FeedbackText format with {0}";
                return null;
            }
            return policy;
        }
    }

    /// <summary>
    /// Mission reward-roll content frozen with each newly accepted dungeon: the sided token template ids and the
    /// reward-chance line.
    /// </summary>
    public sealed class MissionTokenRewardPolicy
    {
        public Dictionary<int, int> SidedItems { get; set; } = [];
        public int ItemQuality { get; set; }
        public string? FeedbackText { get; set; }

        /// <summary>Whether a won roll adds the sided token (the mission's AwardsTokens).</summary>
        public bool AwardsTokens { get; set; } = true;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsValid => SidedItems != null && SidedItems.Count == 2
            && SidedItems.TryGetValue((int)Side.Clan, out int clan) && clan > 0
            && SidedItems.TryGetValue((int)Side.OmniTek, out int omni) && omni > 0 && clan != omni
            && ItemQuality > 0;

        /// <summary>The reward-chance line for <paramref name="percent"/>; null when there is no usable FeedbackText.</summary>
        public string? FormatFeedback(float percent)
        {
            if (string.IsNullOrWhiteSpace(FeedbackText) || !FeedbackText.Contains("{0", StringComparison.Ordinal))
                return null;
            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, FeedbackText, percent);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public MissionTokenRewardPolicy Clone() => new()
        {
            SidedItems = new Dictionary<int, int>(SidedItems), ItemQuality = ItemQuality,
            FeedbackText = FeedbackText, AwardsTokens = AwardsTokens
        };
    }

    /// <summary>
    /// Builds a mission's dungeon from its <see cref="DungeonRequest"/>: at acceptance the request is resolved against
    /// GameData/Dungeons and frozen with the seed and the layout plan; every later build uses only what was frozen, so the
    /// same mission always gets the same dungeon. DungeonGeneration.json holds the mission rules (objective pools, journal
    /// types, token reward, lifetime), not the dungeon's.
    /// </summary>
    public sealed class DungeonLayoutGenerator
    {
        public const string DoorStylesFileName = "DungeonDoorStyles.json";

        /// <summary>
        /// Version stamped on new dungeons: a planned layout built from a frozen plan and a resolved
        /// <see cref="DungeonRequest"/>. Missions stored with an older version are not rebuilt.
        /// </summary>
        public const int CurrentVersion = 5;

        public const string GenerationFileName = "DungeonGeneration.json";

        /// <summary>Layouts tried at acceptance to land inside the request's room range.</summary>
        const int RoomSearchAttempts = 10;

        /// <summary>Building identity type of a quest dungeon (ACG building, as in the captured PlayfieldAnarchyF).</summary>
        public const int BuildingIdentityType = 0xC7A1;

        readonly Dictionary<int, DungeonDoorStyle> _doorStyles = new();
        readonly AORebirth.DungeonGenerator.DungeonGenerator? _generator;
        readonly IZoneLogger _logger;
        readonly DungeonPresets? _presets;
        readonly MissionDefinitions? _missions;
        GenerationPolicy? _policy;

        sealed class GenerationPolicy
        {
            /// <summary>How long an accepted dungeon quest lasts when its Quests.json template sets no ExpiresAfterHours.</summary>
            public int AcceptedLifetimeSeconds { get; set; }

            /// <summary>Journal action and icon per objective kind (KillPerson, FindPerson, FindItem, RepairMachine, FindItemReturn).</summary>
            public List<DungeonMissionTypeRow> MissionTypes { get; set; } = [];
            public DungeonFindItemPolicy? FindItem { get; set; }
            public DungeonFindItemPolicy? ReturnItem { get; set; }
            public DungeonRepairMachinePolicy? RepairMachine { get; set; }
            public DungeonNpcObjectivePolicy? NpcObjectives { get; set; }
            public MissionTokenRewardSetting? TokenReward { get; set; }
        }

        internal DungeonFindItemPolicy? FindItemPolicy => _policy?.FindItem;
        internal DungeonFindItemPolicy? ReturnItemPolicy => _policy?.ReturnItem;
        internal DungeonRepairMachinePolicy? RepairMachinePolicy => _policy?.RepairMachine;
        internal DungeonNpcObjectivePolicy? NpcObjectivePolicy => _policy?.NpcObjectives;

        sealed class DoorStylesFile
        {
            public int OpenChancePercent { get; set; }

            public int LockedChancePercent { get; set; }

            public List<DoorStyleRow> Styles { get; set; } = [];
        }

        sealed class DoorStyleRow
        {
            public int Style { get; set; }

            public int DoorTemplate { get; set; }

            public int ExitTemplate { get; set; }
        }

        /// <summary>The policy's token reward with its item hashes resolved; null when the policy pays none.</summary>
        MissionTokenRewardPolicy? _tokenReward;

        public DungeonLayoutGenerator(IGameData gameData, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;

            try
            {
                _generator = new AORebirth.DungeonGenerator.DungeonGenerator(gameData.RootPath);
            }
            catch (Exception exception)
            {
                _generator = null;
                logger.Error(exception, "Procedural dungeon resources failed to initialize");
            }

            LoadDoorStyles(Path.Combine(gameData.RootPath, DoorStylesFileName), logger);
            try
            {
                _presets = DungeonPresets.Load(gameData, logger);
                _missions = MissionDefinitions.Load(gameData.RootPath, _presets, logger);
            }
            catch (Exception exception)
            {
                _presets = null;
                _missions = null;
                logger.Error(exception, "Failed to load GameData/" + DungeonPresets.Folder + " or Missions/" + MissionDefinitions.FileName
                    + "; new mission acceptance is unavailable");
            }

            try
            {
                _policy = JsonSerializer.Deserialize<GenerationPolicy>(File.ReadAllText(Path.Combine(gameData.RootPath, GenerationFileName)),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (_policy == null || _policy.AcceptedLifetimeSeconds <= 0 || _policy.MissionTypes == null)
                    throw new InvalidDataException("Invalid mission policy.");
                if (_policy.TokenReward != null)
                {
                    _tokenReward = _policy.TokenReward.Resolve(gameData, out string? tokenProblem);
                    if (_tokenReward == null)
                        throw new InvalidDataException("Invalid mission policy: " + tokenProblem);
                }
                DungeonMissionTypes.Configure(_policy.MissionTypes);
                if (!DungeonMissionTypes.IsComplete)
                    throw new InvalidDataException(GenerationFileName + " MissionTypes must name all five objective kinds.");
            }
            catch (Exception exception)
            {
                _policy = null;
                logger.Error(exception, "Failed to load " + GenerationFileName + "; new mission acceptance is unavailable");
            }
        }

        void LoadDoorStyles(string path, IZoneLogger logger)
        {
            if (!File.Exists(path))
            {
                logger.Warn(DoorStylesFileName + " not found at " + path + "; quest dungeons will have no doors");
                return;
            }

            try
            {
                DoorStylesFile? file = JsonSerializer.Deserialize<DoorStylesFile>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                int open = Math.Clamp(file?.OpenChancePercent ?? 0, 0, 100);
                int locked = Math.Clamp(file?.LockedChancePercent ?? 0, 0, 100);
                foreach (DoorStyleRow row in file?.Styles ?? [])
                {
                    if (row.Style > 0 && row.DoorTemplate > 0 && row.ExitTemplate > 0)
                        _doorStyles[row.Style] = new DungeonDoorStyle(row.DoorTemplate, row.ExitTemplate, open, locked);
                }

                logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData dungeon door styles={0} from {1}", _doorStyles.Count, path));
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to load " + path);
            }
        }

        /// <summary>True when new dungeons can be generated.</summary>
        public bool IsAvailable => _generator != null && _policy != null && _presets != null && _missions != null;

        /// <summary>The DungeonDoorStyles.json row for an ACG <paramref name="style"/>; false when the style has no doors configured.</summary>
        public bool TryGetDoorStyle(int style, out DungeonDoorStyle doors)
            => _doorStyles.TryGetValue(style, out doors!);

        /// <summary>The named mission definition (GameData/Missions/Missions.json), or null.</summary>
        public MissionDefinition? Mission(string? name) => _missions?.Get(name);

        /// <summary>The policy's accepted-quest lifetime; 0 when no policy loaded.</summary>
        public int AcceptedLifetimeSeconds => _policy?.AcceptedLifetimeSeconds ?? 0;

        /// <summary>The journal icon for an objective kind, from the policy's MissionTypes.</summary>
        internal int Icon(MissionRollType type) => DungeonMissionTypes.Icon(type);

        /// <summary>The request resolved against GameData/Dungeons for a mission of <paramref name="quality"/>; null with the reason.</summary>
        public ResolvedDungeon? Resolve(DungeonRequest? request, int quality, out string? problem)
        {
            if (_presets == null)
            {
                problem = "dungeon presets are unavailable";
                return null;
            }
            return _presets.Resolve(request, quality, out problem);
        }

        /// <summary>
        /// Freezes a new mission's dungeon before acceptance commits: <paramref name="dungeon"/> (the request resolved by
        /// <see cref="Resolve"/>), its style picked by the seed, and a layout plan. With a room range the generator is asked
        /// for sizes, and the seed moves on, until a layout's rooms per floor land in the range; the seed and size that did
        /// are frozen. <paramref name="tokenReward"/>: whether a won reward roll adds the sided token.
        /// </summary>
        public bool ConfigureNewDungeon(QuestDungeonParameters parameters, ResolvedDungeon dungeon, bool tokenReward, int playerSide,
            out string? problem)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(dungeon);
            problem = null;
            if (_generator == null || _policy == null)
            {
                problem = "the generator is unavailable";
                return false;
            }

            try
            {
                // A population that picks its NPCs from the family buckets is frozen to the families it drew.
                if (dungeon.Population.Mix != null)
                {
                    DungeonPopulationBlock? picked = _presets!.Pick(dungeon.Population, parameters.Quality, playerSide,
                        new Random(unchecked(parameters.Seed * 92821 + 17)), out string? pickProblem);
                    if (picked == null)
                    {
                        problem = pickProblem;
                        return false;
                    }
                    dungeon.Population = picked;
                }

                DungeonLayoutBlock layout = dungeon.Layout;
                var random = new Random(parameters.Seed);
                int style = layout.Styles![random.Next(layout.Styles.Length)];
                int floors = layout.Floors!.Value;
                bool team = floors > 1;
                var styleRooms = _generator.LoadStyle(style);
                var plan = new DungeonLayoutPlan
                {
                    StyleId = style, FloorCount = floors, FloorSize = layout.Size ?? 12,
                    FloorHeight = layout.FloorHeight!.Value, BossFloor = team,
                    DescendingFloors = styleRooms.Transitions?.DescendingFloors ?? false,
                    LockedDoorChancePercent = layout.LockedDoorChancePercent!.Value
                };

                if (layout.Rooms is [int lowest, int highest])
                {
                    // Planned floors run about two rooms per size step; start there and walk toward the range. Rooms are
                    // counted over the floors that get rooms: a team dungeon's last floor is the boss room alone, and
                    // averaged in it would hold the count under the range however big the other floors were.
                    int size = Math.Clamp((lowest + highest) / 4, AORebirth.DungeonGenerator.DungeonGenerator.MinFloorSize,
                        AORebirth.DungeonGenerator.DungeonGenerator.MaxFloorSize);
                    int seed = parameters.Seed;
                    (int Seed, int Size, int Miss)? best = null;
                    for (int attempt = 0; attempt < RoomSearchAttempts; attempt++)
                    {
                        plan.FloorSize = size;
                        AORebirth.DungeonGenerator.DungeonGenerationResult generated = Generate(plan, seed, parameters.EntranceInstance);
                        int roomFloors = generated.BossRoomIndex == null ? floors : Math.Max(1, floors - 1);
                        int perFloor = generated.Rooms.Count / roomFloors;
                        int miss = perFloor < lowest ? lowest - perFloor : perFloor > highest ? perFloor - highest : 0;
                        if (best == null || miss < best.Value.Miss)
                            best = (seed, size, miss);
                        if (miss == 0)
                            break;
                        // Jump by the ratio of rooms wanted to rooms got, at least one size and at most eight (so an odd
                        // layout cannot fling the size). One size at a time is not enough: a style with big rooms keeps
                        // the same room count across many sizes (386 gives 6-8 a floor from size 5 to 12) and would spend
                        // every attempt on that plateau. At the smallest or largest size with the count still outside the
                        // range on that side, no further size can help.
                        int direction = perFloor < lowest ? 1 : -1;
                        if (size == (direction > 0 ? AORebirth.DungeonGenerator.DungeonGenerator.MaxFloorSize
                                : AORebirth.DungeonGenerator.DungeonGenerator.MinFloorSize))
                            break;
                        int next = perFloor > 0
                            ? (int)Math.Round(size * ((lowest + highest) / 2.0) / perFloor, MidpointRounding.AwayFromZero)
                            : size + direction;
                        next = direction > 0 ? Math.Clamp(next, size + 1, size + 8) : Math.Clamp(next, size - 8, size - 1);
                        size = Math.Clamp(next, AORebirth.DungeonGenerator.DungeonGenerator.MinFloorSize,
                            AORebirth.DungeonGenerator.DungeonGenerator.MaxFloorSize);
                        seed = unchecked(seed * 31 + 0x2545F491) & int.MaxValue;
                    }

                    parameters.Seed = best!.Value.Seed;
                    plan.FloorSize = best.Value.Size;
                    if (best.Value.Miss > 0)
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Dungeon rooms {0}-{1} not reached for style {2} in {3} layouts; closest is {4} off at size {5}",
                            lowest, highest, style, RoomSearchAttempts, best.Value.Miss, best.Value.Size));
                }

                parameters.GeneratorVersion = CurrentVersion;
                parameters.IsTeam = team;
                parameters.LayoutPlan = plan;
                parameters.Dungeon = dungeon;
                // Every dungeon carries the reward roll; a mission that declines tokens only adds none to a won roll.
                parameters.TokenReward = _tokenReward?.Clone();
                if (parameters.TokenReward != null)
                    parameters.TokenReward.AwardsTokens = tokenReward;
                if (!plan.IsValid)
                    problem = "the layout plan is invalid";
                return plan.IsValid;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Dungeon layout could not be planned");
                problem = "the layout could not be planned";
                return false;
            }
        }

        AORebirth.DungeonGenerator.DungeonGenerationResult Generate(DungeonLayoutPlan plan, int seed, int building)
            => _generator!.Generate(new AORebirth.DungeonGenerator.DungeonGenerationRequest
            {
                StyleId = plan.StyleId, Seed = seed, FloorCount = plan.FloorCount,
                FloorSize = plan.FloorSize, FloorHeight = plan.FloorHeight, BossFloor = plan.BossFloor,
                DescendingFloors = plan.DescendingFloors, UseFloorTransitions = true,
                LockedDoorChancePercent = plan.LockedDoorChancePercent, BuildingInstance = building,
                Layout = AORebirth.DungeonGenerator.DungeonLayoutKind.Planned
            });

        public bool TryGenerate(QuestDungeonParameters parameters, out DungeonLayout layout)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            layout = null!;
            DungeonLayoutPlan? plan = parameters.LayoutPlan;
            ResolvedDungeon? dungeon = parameters.Dungeon;
            if (parameters.GeneratorVersion != CurrentVersion || _generator == null || plan?.IsValid != true || dungeon == null
                || parameters.IsTeam != plan.BossFloor)
                return false;
            try
            {
                var result = Generate(plan, parameters.Seed, parameters.EntranceInstance);
                DungeonDoorStyle? doors = _doorStyles.TryGetValue(plan.StyleId, out var style)
                    ? style with { LockedChancePercent = plan.LockedDoorChancePercent } : null;
                layout = new DungeonLayout(result.Generator, result.Identity, result.GeneratorBlob(),
                    new Vector3(result.Spawn.X, result.Spawn.Y, result.Spawn.Z), parameters.Seed, doors)
                {
                    FloorTransitions = result.Transitions, BossRoomIndex = result.BossRoomIndex,
                    ObjectiveRoomChoices = dungeon.Objective.RoomChoices ?? 1,
                    SpawnHeading = DungeonLayout.YawHeading(result.SpawnYaw),
                    Population = dungeon.Population,
                    MobLoot = dungeon.Population.Loot?.Where(Mobs.MobItemTableEntry.IsWellFormed).ToArray() ?? []
                };
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Procedural dungeon generation failed for seed " + parameters.Seed.ToString(CultureInfo.InvariantCulture));
                return false;
            }
        }
    }
}
