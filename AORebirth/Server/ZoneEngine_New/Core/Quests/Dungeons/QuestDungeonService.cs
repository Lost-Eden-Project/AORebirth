namespace ZoneEngine_New.Core.Quests.Dungeons
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading;

    using AORebirth.Database.Domain.Quests;
    using AORebirth.Enums;
    using AORebirth.Interfaces.Persistence.Missions;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Terminal missions as generated quests with an ACG dungeon. Accepting an offer stores a generated quest (the
    /// offer's type, text and reward), a dungeon seed and the offer's entrance, and gives the owner a mission key,
    /// all in one transaction. Anyone holding a linked key enters through that entrance; the dungeon is generated
    /// from the seed on entry and released after it has been empty for ten minutes. When the quest ends every key
    /// link goes, the keys are retired and whoever is inside is sent back out.
    /// Every request is checked on the server against its own records; clients only name slots and targets.
    /// </summary>
    public sealed class QuestDungeonService : IDisposable
    {
        public const int KeyLowId = 28577;

        /// <summary>Mission key item identity type (0xC76D).</summary>
        public const int KeyItemType = 0xC76D;

        public const int DuplicatorLowId = 28564;

        /// <summary>Most keys one dungeon can have, copies included: bounds a duplicator bug or exploit.</summary>
        public const int MaxKeysPerQuest = 12;

        const string KeyNamePrefix = "Mission key to ";

        /// <summary>Shown when a key can not be copied: its dungeon is over or already has the most keys it can.</summary>
        const string KeyRefused = "This mission key can not be copied.";

        static readonly TimeSpan EmptyDungeonLifetime = TimeSpan.FromMinutes(10);

        static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

        /// <summary>How long an ended generated quest stays stored (lets a late login return to its entrance).</summary>
        static readonly TimeSpan EndedQuestRetention = TimeSpan.FromDays(1);

        /// <summary>Minimum time between one player's key or entrance requests: they cost a database read.</summary>
        static readonly TimeSpan RequestCooldown = TimeSpan.FromMilliseconds(750);

        const float EntryHorizontalRange = 10f;

        const float EntryVerticalRange = 14f;

        const int SweepBatch = 50;

        static readonly int[] OwnedContainerTypes =
        [
            (int)IdentityType.Inventory, (int)IdentityType.WeaponPage, (int)IdentityType.ArmorPage,
            (int)IdentityType.ImplantPage, (int)IdentityType.SocialPage, (int)IdentityType.BankByRef
        ];

        /// <summary>
        /// A quest's dungeon: its parameters, expiry, the NPC hash a kill-target quest wants (null otherwise), and whether
        /// the quest is over (completed): an ended dungeon keeps whoever is inside but can no longer be entered or rebuilt.
        /// </summary>
        sealed record CachedDungeon(QuestDungeonParameters Parameters, long ExpiresAtUtcTicks, string? TargetHash, bool Ended = false,
            int OwnerId = 0, long AssignedAtUtcTicks = 0, string FrozenJson = "");

        readonly ICharacterQuestStore _store;
        readonly QuestService _quests;
        readonly MissionDestinationCatalog _entrances;
        readonly DungeonLayoutGenerator _layouts;
        readonly IItemBuilder _items;
        readonly IItemInstanceIdAllocator _ids;
        readonly InventoryFlushService _flush;
        readonly InventoryActionService _inventoryActions;
        readonly Lazy<PlayfieldManager> _playfields;
        readonly IGameData _gameData;
        readonly HashItemMinter? _hashItems;
        readonly IZoneLogger _logger;
        readonly ZoneEngine_New.Core.World.WorldLink? _world;
        readonly ZoneEngine_New.Core.World.WorldCommands? _commands;

        /// <summary>Other zone instances end their part of a dungeon: keys their players hold, the dungeon if they run it.</summary>
        const string DungeonEndedCommand = "dungeon.ended";

        readonly ConcurrentDictionary<string, CachedDungeon> _dungeons = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<int, long> _lastRequest = new();
        readonly ConcurrentDictionary<int, string> _pendingRelease = new();
        /// <summary>Dungeon builds in flight, by dungeon playfield: every entrant waits on the one build.</summary>
        readonly ConcurrentDictionary<int, Lazy<System.Threading.Tasks.Task<bool>>> _pendingBuilds = new();
        readonly Timer _sweep;
        int _sweeping;

        public QuestDungeonService(ICharacterQuestStore store, QuestService quests, MissionDestinationCatalog entrances,
            DungeonLayoutGenerator layouts, IItemBuilder items, IItemInstanceIdAllocator ids,
            InventoryFlushService flush, InventoryActionService inventoryActions, Lazy<PlayfieldManager> playfields,
            IGameData gameData, IZoneLogger logger, HashItemMinter? hashItems = null,
            ZoneEngine_New.Core.World.WorldLink? world = null, ZoneEngine_New.Core.World.WorldCommands? commands = null)
        {
            _world = world;
            _commands = commands;
            _commands?.Register(DungeonEndedCommand, OnDungeonEndedElsewhere);
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _quests = quests ?? throw new ArgumentNullException(nameof(quests));
            _entrances = entrances ?? throw new ArgumentNullException(nameof(entrances));
            _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
            _inventoryActions = inventoryActions ?? throw new ArgumentNullException(nameof(inventoryActions));
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hashItems = hashItems;

            _quests.QuestEnded += OnQuestEnded;
            _quests.TeamObjectiveEarned += OnTeamObjectiveEarned;
            _quests.DungeonAssigner = AssignTemplate;
            _sweep = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        public void Dispose()
        {
            _quests.QuestEnded -= OnQuestEnded;
            _quests.TeamObjectiveEarned -= OnTeamObjectiveEarned;
            _quests.DungeonAssigner = null;
            _sweep.Dispose();
        }

        /// <summary>
        /// Accepts one of the player's current terminal offers as a dungeon quest. Returns null on success, else the
        /// reason to show. The offer is claimed in the same transaction as the quest and key, so a repeated or raced
        /// accept finds it taken and writes nothing.
        /// </summary>
        /// <summary>
        /// Gives a Quests.json quest with a <see cref="QuestTemplate.Dungeon"/>: the quest is copied into a generated
        /// quest and its dungeon inputs (floor mode, quality, style, entrance, population, mob and boss levels) are
        /// frozen into the copy's dungeon, with a mission key, in one transaction. Every give rolls a new dungeon.
        /// <paramref name="requestedQuality"/> is the mission quality the giving dialog named, if any. Returns null on
        /// success, else the reason.
        /// </summary>
        string? AssignTemplate(Player player, QuestTemplate source, int? requestedQuality)
            => Assign(player, source, requestedQuality, rolled: false);

        /// <summary>
        /// Gives a mission a module rolled at a terminal (<paramref name="mission"/> has a <see cref="QuestTemplate.Dungeon"/>
        /// with its entrance already chosen) exactly as a Quests.json dungeon quest is given, except that the copy keeps
        /// its own <see cref="QuestReward.Records"/> instead of paying by a QuestRewards.json hash. Returns null on
        /// success, else the reason.
        /// </summary>
        internal string? AssignRolledMission(Player player, QuestTemplate mission)
            => Assign(player, mission, requestedQuality: null, rolled: true);

        /// <summary>
        /// The entrance an offer shows and accepting it uses: <paramref name="entrance"/> on <paramref name="playfield"/>, or
        /// one of its entrances at random when null. False when there is none.
        /// </summary>
        internal bool TryResolveEntrance(int playfield, uint? entrance, out MissionEntrancePlacement placement,
            out MissionDestinationWorldPosition? worldPosition)
            => TryPickEntrance(new QuestDungeonTemplate { Playfield = playfield, Entrance = entrance }, out placement, out worldPosition);

        /// <summary>How long an accepted mission lasts when its quest names no lifetime (DungeonGeneration.json).</summary>
        internal int AcceptedLifetimeSeconds => _layouts.AcceptedLifetimeSeconds;

        string? Assign(Player player, QuestTemplate source, int? requestedQuality, bool rolled)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(source);

            // A Team quest rolled by a player in a team is one mission for every teammate on the same playfield: the
            // roller owns it (OwnerType Team) and each teammate joins it with their own row and key.
            // The roster is frozen with the mission: only these can be given it, now or by "Update team members".
            int[]? roster = source.IsTeamScope && player.Playfield?.GetService<Teams.TeamService>()?.GetImmediateTeam(player) is { } team
                && team.MemberIds.Count > 1 ? team.MemberIds.ToArray() : null;
            string? refused = AssignTemplateLocked(player, source, requestedQuality, roster, rolled, out string questId);
            if (refused != null)
                return refused;

            if (roster != null)
                GiveToTeammatesHere(player, questId, roster);
            return null;
        }

        /// <summary>
        /// Gives the team mission to the roller's current teammates on the roller's playfield who were on its roster.
        /// Each joins on their own projection, outside the roller's gate. Returns how many were asked to join.
        /// </summary>
        int GiveToTeammatesHere(Player roller, string questId, int[] roster)
        {
            if (roller.Playfield is not Playfield here
                || here.GetService<Teams.TeamService>()?.GetImmediateTeam(roller) is not { } team)
                return 0;

            int asked = 0;
            int rollerLevel = roller.Stats.GetOrOne(CharacterStat.Level);
            string rollerName = roller.Name;
            PlayfieldManager playfields = _playfields.Value;
            foreach (int id in team.MemberIds)
            {
                if (id == roller.Identity.Instance || Array.IndexOf(roster, id) < 0
                    || !playfields.FindPlayer(id, out Player member) || !ReferenceEquals(member.Playfield, here))
                    continue;
                // The team XP range (LevelEligibility): a member too low to share the roller's XP does not get the mission.
                if (Teams.TeamLevelEligibility.Current.IsTooLowForMember(rollerLevel, member.Stats.GetOrOne(CharacterStat.Level)))
                {
                    here.DispatchPlayerProjection(member, () => SendText(member, "You are too low level to receive this quest."));
                    continue;
                }
                here.DispatchPlayerProjection(member, () => JoinTeamQuest(member, questId, rollerName));
                asked++;
            }
            return asked;
        }

        /// <summary>
        /// "Update team members" on a team mission in the roller's journal: gives it to their teammates on the same
        /// playfield who were in the team when it was rolled and do not hold it now. Refused for a teammate's copy, a
        /// mission that is over, or anyone not on its roster.
        /// </summary>
        public void UpdateTeamMembers(Player player, int journalInstance)
        {
            ArgumentNullException.ThrowIfNull(player);
            PlayerQuest? quest = null;
            foreach (PlayerQuest held in _quests.GetLog(player).Quests.Values)
            {
                if (held.IsActive && held.Source == QuestSource.Generated && QuestJournal.JournalInstance(held.QuestId) == journalInstance)
                {
                    quest = held;
                    break;
                }
            }

            if (quest == null || !quest.Template.IsTeamScope || !TryBeginRequest(player)
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters)
                || parameters.TeamOwnerId != player.Identity.Instance || parameters.TeamRoster is not { Length: > 1 } roster
                || !TryGetDungeon(quest.QuestId, out CachedDungeon dungeon) || dungeon.Ended)
                return;

            int asked = GiveToTeammatesHere(player, quest.QuestId, roster);
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Team mission update char={0} quest={1} asked={2}",
                player.Identity.Instance, quest.QuestId, asked));
        }

        /// <summary>
        /// A teammate joins the team mission <paramref name="questId"/> another member just rolled: their quest row and own
        /// mission key in one transaction, then the key and the journal entry. Skipped when they already hold this quest
        /// (another copy of the same Quests.json quest), their inventory has no room for the key, or the mission is over.
        /// </summary>
        void JoinTeamQuest(Player member, string questId, string rollerName)
        {
            lock (member.PersistenceGate)
            {
                if (member.IsPersistenceQuarantined || member.Session == null || member.Playfield == null || !member.Inventory.IsHydrated)
                    return;

                GeneratedQuestRow? row = _store.LoadGenerated(questId);
                QuestTemplate? template = row == null ? null
                    : System.Text.Json.JsonSerializer.Deserialize<QuestTemplate>(row.DefinitionJson, QuestTemplate.JsonOptions);
                if (row == null || template == null || !TryGetDungeon(questId, out CachedDungeon dungeon) || dungeon.Ended
                    || row.OwnerType != (int)QuestOwnerType.Team
                    || !QuestDungeonParameters.TryParse(row.AcgBuildingGeneratorJson, out QuestDungeonParameters frozen)
                    || frozen.TeamRoster is not { } roster || Array.IndexOf(roster, member.Identity.Instance) < 0)
                    return;

                QuestLog log = _quests.GetLog(member);
                if ((log.Quests.TryGetValue(questId, out PlayerQuest? same) && same.IsActive)
                    || (template.TemplateHash is { Length: > 0 } hash && log.TryFind(hash, out PlayerQuest? held) && held.IsActive))
                {
                    SendText(member, "You already have this mission, so you were not given " + rollerName + "'s.");
                    return;
                }

                try
                {
                    _flush.HardFlush(member);
                    Item key = CreateKey();
                    if (!InventoryGrantPlan.TryCreate(member, [key], out InventoryGrantPlan plan))
                    {
                        SendText(member, "Your inventory is full, so you were not given " + rollerName + "'s team mission.");
                        return;
                    }

                    DateTime now = DateTime.UtcNow;
                    var characterRow = new CharacterQuestRow
                    {
                        CharacterId = member.Identity.Instance,
                        QuestId = questId,
                        Source = (int)QuestSource.Generated,
                        State = (int)QuestState.Active,
                        Progress = 0,
                        RequiredCount = template.RequiredCount,
                        AssignedAtUtcTicks = now.Ticks,
                        UpdatedAtUtcTicks = now.Ticks
                    };
                    if (!_store.TryJoinTeamDungeonQuest(characterRow, KeyRow(plan.Rows[0], now), MaxKeysPerQuest, now.Ticks))
                    {
                        _logger.Info(string.Format(CultureInfo.InvariantCulture, "Team mission join refused char={0} quest={1}",
                            member.Identity.Instance, questId));
                        return;
                    }

                    plan.PublishAfterCommit(notify: false);
                    SendKey(member, key, plan.Rows[0].ContainerPlacement, dungeon.Parameters.EntranceName);
                    _quests.AdoptGenerated(member, questId, template, now, new DateTime(row.ExpiresAtUtcTicks, DateTimeKind.Utc),
                        row.AcgBuildingGeneratorJson);
                    _logger.Info(string.Format(CultureInfo.InvariantCulture, "Team mission joined char={0} quest={1} owner={2}",
                        member.Identity.Instance, questId, row.OwnerId));
                }
                catch (DatabaseCommitOutcomeUnknownException exception)
                {
                    Quarantine(member, exception);
                }
                catch (Exception exception)
                {
                    // Nothing was published unless the join committed; a later "Update team members" can retry it.
                    _logger.Error(exception, string.Format(CultureInfo.InvariantCulture, "Team mission join failed char={0} quest={1}",
                        member.Identity.Instance, questId));
                }
            }
        }

        /// <summary>
        /// World entry: a team mission whose objective another holder earned while this player was offline or on another
        /// zone instance is earned now, with the team dungeon's kill chance (live here, else its owner's checkpoint).
        /// </summary>
        void CatchUpTeamMissions(Player player)
        {
            foreach (PlayerQuest quest in _quests.GetLog(player).Quests.Values.ToArray())
            {
                if (!quest.IsActive || quest.IsRewardPending || quest.Source != QuestSource.Generated || !quest.Template.IsTeamScope
                    || !TryGetDungeon(quest.QuestId, out CachedDungeon dungeon)
                    || !_store.HasOtherHolderEarned(quest.QuestId, player.Identity.Instance))
                    continue;

                (int Killed, int Total) chance = _playfields.Value.TryGetQuestDungeon(dungeon.Parameters.DungeonPlayfield, out QuestDungeonPlayfield live)
                    && live.Kills is { } kills
                    ? kills.Chance()
                    : _quests.LoadDungeonKills(dungeon.OwnerId, quest.QuestId, dungeon.AssignedAtUtcTicks, dungeon.Parameters).Chance();
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Team mission catch-up char={0} quest={1} chance={2}/{3}",
                    player.Identity.Instance, quest.QuestId, chance.Killed, chance.Total));
                _quests.EarnShared(player, quest.QuestId, chance);
            }
        }

        /// <summary>
        /// A team mission's objective was earned by one holder: every other holder online on this zone instance earns it
        /// on their own playfield, with the team dungeon's kill chance as the earner saw it.
        /// </summary>
        void OnTeamObjectiveEarned(Player earner, PlayerQuest quest)
        {
            (int Killed, int Total)? chance = earner.Playfield is QuestDungeonPlayfield dungeon
                && string.Equals(dungeon.QuestId, quest.QuestId, StringComparison.Ordinal) && dungeon.Kills is { } kills
                ? kills.Chance() : null;

            PlayfieldManager playfields = _playfields.Value;
            string questId = quest.QuestId;
            foreach (int holder in _store.LoadActiveQuestHolders(questId))
            {
                if (holder == earner.Identity.Instance || !playfields.FindPlayer(holder, out Player member)
                    || member.Playfield is not Playfield at)
                    continue;
                at.DispatchPlayerProjection(member, () => _quests.EarnShared(member, questId, chance));
            }
        }

        string? AssignTemplateLocked(Player player, QuestTemplate source, int? requestedQuality, int[]? teamRoster, bool rolled,
            out string questId)
        {
            QuestOwnerType ownerType = teamRoster != null ? QuestOwnerType.Team : QuestOwnerType.Character;
            questId = string.Empty;
            QuestDungeonTemplate input = source.Dungeon ?? throw new ArgumentException("The quest has no dungeon.", nameof(source));
            if (input.Problem(source.Action) is string problem)
                return problem;

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.Session == null || player.Playfield == null || !player.Inventory.IsHydrated)
                    return "Mission acceptance is unavailable right now.";
                if (!_layouts.IsAvailable)
                    return "Mission dungeons are unavailable.";
                if (!TryPickEntrance(input, out MissionEntrancePlacement entrance, out MissionDestinationWorldPosition? worldPosition))
                    return "The quest's dungeon entrance is unavailable.";
                if (!QuestDungeonIds.TryCreate(_ids.Allocate(), out questId, out int dungeonPlayfield))
                    return "Mission acceptance is unavailable right now.";

                int missionType = QuestDungeonTemplate.MissionTypeFor(source.Action)!.Value;
                int quality = input.QualityFor(requestedQuality, player.Stats.GetOrZero(CharacterStat.Level));
                string[]? population = ResolvePopulation(source.Hash, input.Npcs);
                var parameters = new QuestDungeonParameters
                {
                    Seed = RandomNumberGenerator.GetInt32(int.MaxValue),
                    GeneratorVersion = DungeonLayoutGenerator.CurrentVersion,
                    DungeonPlayfield = dungeonPlayfield,
                    EntranceType = unchecked((int)entrance.IdentityType),
                    EntranceInstance = unchecked((int)entrance.IdentityInstance),
                    EntrancePlayfield = entrance.PlayfieldId,
                    EntranceName = entrance.DisplayName,
                    EntranceX = entrance.LocalX,
                    EntranceY = entrance.LocalY,
                    EntranceZ = entrance.LocalZ,
                    // An entrance no terminal offer was ever captured at has no WorldPos; the journal then gets the
                    // playfield and offsets ordinary quest waypoints carry.
                    DestinationType = worldPosition == null ? QuestJournal.WaypointPlayfieldType : unchecked((int)worldPosition.PlayfieldIdentityType),
                    BuildingLowId = worldPosition?.WorldOffsetX ?? QuestJournal.WaypointUnknown,
                    BuildingHighId = worldPosition?.WorldOffsetZ ?? QuestJournal.WaypointUnknown,
                    MissionType = missionType,
                    Quality = quality,
                    ObjectiveRoomChoices = input.ObjectiveRoomChoices,
                    MissionIconId = source.IconId is int icon and > 0 ? icon : _layouts.Icon((MissionRollType)missionType),
                    TeamOwnerId = teamRoster != null ? player.Identity.Instance : 0,
                    TeamRoster = teamRoster
                };

                DateTime now = DateTime.UtcNow;
                DateTime expires = source.ExpiresAfterHours is int hours and > 0
                    ? now.AddHours(hours)
                    : now.AddSeconds(_layouts.AcceptedLifetimeSeconds);
                return Establish(player, questId, ownerType, parameters, CopyTemplate(source, questId, rolled), input.IsMultiFloor, input.Style,
                    source.Objective, input.ObjectiveLevelFor(quality), input.AwardsTokens, population, input.MobLevelPercent,
                    // The quest's own chests and tables; a quest naming none gets no chests and no extra NPC drops.
                    input.Chests, input.MobLoot ?? [], now, expires, (quest, characterRow, key, sourceItem) =>
                        _store.TryAssignDungeonQuest(player.Identity.Instance, quest, characterRow, key, sourceItem),
                    "Mission acceptance is unavailable right now.", (rolled ? "rolled=" : "template=") + source.Hash, entrance.DisplayName);
            }
        }

        /// <summary>
        /// One of the template's entrances: the named one, or any on its playfield, with its captured world position
        /// when a terminal offer was ever captured at it (null otherwise).
        /// </summary>
        bool TryPickEntrance(QuestDungeonTemplate input, out MissionEntrancePlacement entrance, out MissionDestinationWorldPosition? worldPosition)
        {
            entrance = null!;
            worldPosition = null;
            var candidates = new List<MissionEntrancePlacement>();
            foreach (MissionEntrancePlacement placement in _entrances.GetPlacementsForPlayfield(input.Playfield))
            {
                if (placement.IdentityType == (uint)IdentityType.MissionEntrance
                    && (input.Entrance == null || placement.IdentityInstance == input.Entrance))
                    candidates.Add(placement);
            }

            if (candidates.Count == 0)
                return false;
            entrance = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
            if (_entrances.TryGetWorldPosition(entrance.Identity, out MissionDestinationWorldPosition position))
                worldPosition = position;
            return true;
        }

        /// <summary>
        /// The quest's own ordinary population: the hashes with an NPC template, in order, each once. Null when the quest
        /// names none (the generation policy's population); empty when none of the named ones resolve, so the dungeon
        /// gets no stand-in population from another setting.
        /// </summary>
        string[]? ResolvePopulation(string questHash, string[]? authored)
        {
            if (authored is not { Length: > 0 })
                return null;
            var resolved = new List<string>(authored.Length);
            var missing = new List<string>();
            foreach (string hash in authored.Distinct(StringComparer.Ordinal))
                (_gameData.HasMobTemplate(hash) ? resolved : missing).Add(hash);
            if (missing.Count > 0)
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Quest {0} Dungeon.Npcs without an NPC template, left out: {1}{2}", questHash, string.Join(", ", missing),
                    resolved.Count == 0 ? " (no ordinary population will spawn)" : string.Empty));
            return resolved.ToArray();
        }

        /// <summary>
        /// The Quests.json quest as its generated copy: the copy's own id, the source hash, and no dungeon inputs, waypoint
        /// or objective of its own (the dungeon's entrance and bound objective replace them).
        /// </summary>
        /// <summary>
        /// The generated copy of <paramref name="source"/>. A Quests.json quest's copy keeps its hash in
        /// <see cref="QuestTemplate.TemplateHash"/> and pays by it; a <paramref name="rolled"/> mission has none and pays
        /// its own stored records.
        /// </summary>
        static QuestTemplate CopyTemplate(QuestTemplate source, string questId, bool rolled)
        {
            QuestTemplate copy = System.Text.Json.JsonSerializer.Deserialize<QuestTemplate>(
                System.Text.Json.JsonSerializer.Serialize(source, QuestTemplate.JsonOptions), QuestTemplate.JsonOptions)
                ?? throw new InvalidOperationException("Quest template could not be copied.");
            copy.Hash = questId;
            copy.TemplateHash = rolled ? null : source.Hash;
            copy.Dungeon = null;
            copy.Waypoint = null;
            copy.Waypoints = null;
            copy.Objective = new QuestObjective();
            return copy;
        }

        /// <summary>A copy of <paramref name="policy"/> limited to <paramref name="hash"/>, when a Quests.json quest names one.</summary>
        static DungeonFindItemPolicy? WithItem(DungeonFindItemPolicy? policy, string? hash)
            => policy == null || string.IsNullOrWhiteSpace(hash) ? policy : new DungeonFindItemPolicy
            {
                ItemHashes = [hash], Quality = policy.Quality, QuestInstanceStat = policy.QuestInstanceStat,
                WorldFlags = policy.WorldFlags, WorldScalePercent = policy.WorldScalePercent
            };

        /// <summary>
        /// Shared by terminal offers and Quests.json dungeon quests: freezes the layout, binds the exact objective
        /// (<paramref name="authored"/> names its NPC or item hashes, else the generation policy pools do; an NPC
        /// objective is resolved at <paramref name="objectiveQuality"/>), then commits quest, owner row, key and any
        /// mission tool through <paramref name="commit"/> and publishes them.
        /// </summary>
        string? Establish(Player player, string questId, QuestOwnerType ownerType, QuestDungeonParameters parameters, QuestTemplate template, bool team, int? style,
            QuestObjective? authored, int objectiveQuality, bool tokenReward, string[]? population, int populationLevelSpreadPercent,
            QuestDungeonChests? chests, IReadOnlyList<MobItemTableEntry>? mobLoot,
            DateTime now, DateTime expires,
            Func<GeneratedQuestRow, CharacterQuestRow, QuestDungeonKeyItemRow, QuestDungeonKeyItemRow?, bool> commit,
            string notCommitted, string source, string entranceName)
        {
            // Acceptance freezes only the seed and the generation choices. The layout, the geometry and the objective's
            // room all derive from that seed when the dungeon builds on first entry.
            using (ZoneEngine_New.Core.Metrics.TickStallWatch.Enter("mission.accept.layout", player.Identity.Instance))
            {
                if (!_layouts.ConfigureNewDungeon(parameters, team, style, tokenReward, population, populationLevelSpreadPercent, chests, mobLoot))
                    return "Mission dungeons are unavailable.";
            }

            int missionType = parameters.MissionType;
            if (template.IsFindItem || template.IsReturnItem)
            {
                DungeonFindItemPolicy? policy = WithItem(template.IsReturnItem ? _layouts.ReturnItemPolicy : _layouts.FindItemPolicy,
                    authored?.WorldItem);
                if (!TryPrepareObjective(player, questId, policy, missionType, false,
                        out var objective, out string objectiveHash))
                    return "Mission item objective data is unavailable; this offer was not accepted.";
                parameters.ObjectiveItem = objective;
                template.Objective.WorldItem = objectiveHash;
            }
            else if (template.IsRepair)
            {
                if (!TryPrepareObjective(player, questId, WithItem(_layouts.RepairMachinePolicy?.Target, authored?.WorldItem), 3, false,
                        out var objective, out string objectiveHash)
                    || !TryPrepareObjective(player, questId, WithItem(_layouts.RepairMachinePolicy?.Source, authored?.SpecItem), 3, true,
                        out var tool, out string toolHash))
                    return "Mission repair objective data is unavailable; this offer was not accepted.";
                parameters.ObjectiveItem = objective;
                parameters.ObjectiveTool = tool;
                template.Objective.WorldItem = objectiveHash;
                template.Objective.SpecItem = toolHash;
            }
            else if (missionType is 0 or 1)
            {
                if (!TryPrepareNpc(player, questId, missionType, objectiveQuality, authored?.Npc, out var objective))
                    return "Mission NPC objective data is unavailable; this offer was not accepted.";
                parameters.ObjectiveNpc = objective;
                template.Objective.Npc = objective.NpcHash;
            }
            else
                return "That mission objective is unavailable; this offer was not accepted.";
            string parametersJson = parameters.ToJson();

            try
            {
                using (ZoneEngine_New.Core.Metrics.TickStallWatch.Enter("mission.accept.flush", player.Identity.Instance))
                    _flush.HardFlush(player);
                Item key = CreateKey();
                MissionItemObjectiveBinding? sourceBinding = parameters.ObjectiveTool;
                Item? sourceItem = sourceBinding == null ? null : _items.Create(sourceBinding.LowId,
                    sourceBinding.HighId, sourceBinding.Quality, ItemSource.Quest, 1, sourceBinding.Instance, sourceBinding.Identity);
                Item[] grants = sourceItem == null ? [key] : [key, sourceItem];
                if (!InventoryGrantPlan.TryCreate(player, grants, out InventoryGrantPlan plan))
                    return "Your inventory is full.";

                var quest = new GeneratedQuestRow
                {
                    QuestId = questId,
                    OwnerType = (int)ownerType,
                    OwnerId = player.Identity.Instance,
                    DefinitionJson = System.Text.Json.JsonSerializer.Serialize(template, QuestTemplate.JsonOptions),
                    AcgBuildingGeneratorJson = parametersJson,
                    CreatedAtUtcTicks = now.Ticks,
                    ExpiresAtUtcTicks = expires.Ticks,
                    UpdatedAtUtcTicks = now.Ticks
                };
                var characterRow = new CharacterQuestRow
                {
                    CharacterId = player.Identity.Instance,
                    QuestId = questId,
                    Source = (int)QuestSource.Generated,
                    State = (int)QuestState.Active,
                    Progress = 0,
                    RequiredCount = template.RequiredCount,
                    AssignedAtUtcTicks = now.Ticks,
                    UpdatedAtUtcTicks = now.Ticks
                };

                bool accepted;
                using (ZoneEngine_New.Core.Metrics.TickStallWatch.Enter("mission.accept.commit", player.Identity.Instance))
                    accepted = commit(quest, characterRow, KeyRow(plan.Rows[0], now), sourceItem == null ? null : KeyRow(plan.Rows[1], now));
                if (!accepted)
                    return notCommitted;

                _dungeons[questId] = new CachedDungeon(parameters, expires.Ticks,
                    parameters.ObjectiveNpc == null ? KillTarget(template) : null,
                    OwnerId: player.Identity.Instance, AssignedAtUtcTicks: now.Ticks, FrozenJson: parametersJson);
                // The dungeon's navmesh bakes now, off the tick, so it is ready by the time the player reaches the entrance.
                _playfields.Value.DungeonNavMeshes.Prewarm(parameters, _layouts);
                plan.PublishAfterCommit(notify: false);
                SendKey(player, key, plan.Rows[0].ContainerPlacement, parameters.EntranceName);
                if (sourceItem != null)
                    SendGrantedMissionItem(player, sourceItem, plan.Rows[1].ContainerPlacement, sourceItem.Name);
                using (ZoneEngine_New.Core.Metrics.TickStallWatch.Enter("mission.accept.adopt", player.Identity.Instance))
                    _quests.AdoptGenerated(player, questId, template, now, expires, parametersJson);
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Dungeon quest accepted char={0} quest={1} {2} entrance={3} ({4}) dungeon={5} floors={6} style={7} ql={8} mobSpread={9}% objectiveQl={10}",
                    player.Identity.Instance, questId, source, parameters.EntranceInstance, entranceName, parameters.DungeonPlayfield,
                    parameters.LayoutPlan?.FloorCount, parameters.LayoutPlan?.StyleId, parameters.Quality,
                    parameters.PopulationLevelSpreadPercent, objectiveQuality));
                return null;
            }
            catch (DatabaseCommitOutcomeUnknownException exception)
            {
                Quarantine(player, exception);
                return "Mission acceptance needs to be reconciled; please reconnect.";
            }
        }

        /// <summary>
        /// The player used a MissionEntrance. When a key they carry opens a dungeon behind this entrance they are
        /// sent into it (created from its seed if needed). False when no carried key matches, so other entrance
        /// handling can take the use.
        /// </summary>
        public bool TryEnter(Player player, Identity target)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (target.Type != IdentityType.MissionEntrance
                || !_entrances.TryGetByIdentity(unchecked((uint)target.Type), unchecked((uint)target.Instance), out MissionEntrancePlacement entrance))
                return false;

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.IsDead || player.Session is not IZoneSession session
                    || player.Playfield is not Playfield playfield || playfield is QuestDungeonPlayfield
                    || playfield.Identity.Instance != entrance.PlayfieldId || !WithinEntrance(player, entrance)
                    || !TryBeginRequest(player))
                    return false;

                List<Item> keys = CarriedKeys(player);
                if (keys.Count == 0)
                    return false;

                IDictionary<int, string> links = _store.LoadKeyQuests(keys.ConvertAll(key => key.InstanceId));
                RemoveDeadKeys(player, keys.Where(key => !links.ContainsKey(key.InstanceId)));

                long now = DateTime.UtcNow.Ticks;
                foreach (string questId in links.Values.Distinct(StringComparer.Ordinal))
                {
                    if (!TryGetDungeon(questId, out CachedDungeon dungeon) || dungeon.Ended || dungeon.ExpiresAtUtcTicks <= now
                        || unchecked((uint)dungeon.Parameters.EntranceType) != entrance.IdentityType
                        || unchecked((uint)dungeon.Parameters.EntranceInstance) != entrance.IdentityInstance)
                        continue;

                    // Already live here: it was built with its layout, kills and chests, so the tick only hands the player
                    // over. Face into the entrance room, not the heading carried in from outside (back at the exit door).
                    int dungeonPlayfield = dungeon.Parameters.DungeonPlayfield;
                    if (_playfields.Value.TryReuseQuestDungeon(dungeonPlayfield, questId, out QuestDungeonPlayfield live))
                        return session.TransferToPlayfield(live, live.Layout.Spawn, live.Layout.SpawnHeading);

                    // World has not said where this dungeon runs: ask off the tick, then try the entrance again.
                    ZoneEngine_New.Core.World.WorldLink? link = _playfields.Value.World;
                    if (link != null && !link.HostsPlayfield(dungeonPlayfield))
                    {
                        QuestDungeonParameters parameters = dungeon.Parameters;
                        _ = link.ResolveAsync(dungeonPlayfield).ContinueWith(resolve =>
                        {
                            if (!resolve.IsCompletedSuccessfully || !resolve.Result.Accepted) return;
                            bool local = string.Equals(resolve.Result.Instance, link.Instance.Name, StringComparison.OrdinalIgnoreCase);
                            // Joining another instance lands on the layout's spawn: generate it here, still off the tick.
                            DungeonLayout? remote = null;
                            if (!local && !_layouts.TryGenerate(parameters, out remote)) return;
                            playfield.DispatchPlayerProjection(player, () =>
                            {
                                if (!ReferenceEquals(player.Playfield, playfield) || player.Session == null) return;
                                if (local) Reenter(player, target);
                                else _playfields.Value.CrossInstance?.Start(player, dungeonPlayfield, remote!.Spawn, remote.SpawnHeading,
                                    AORebirth.World.Contracts.V1.ArrivalKind.ArrivalDungeon);
                            });
                        }, System.Threading.Tasks.TaskScheduler.Default);
                        return true;
                    }

                    // Building a dungeon (layout generation, kill and objective reads, room geometry, collision world,
                    // spawns, chests) costs several tick budgets: do it on the thread pool, then try the entrance again
                    // once the dungeon is live. Everyone who uses the entrance meanwhile waits on that same build.
                    System.Threading.Tasks.Task<bool> build = _pendingBuilds.GetOrAdd(dungeonPlayfield,
                        _ => new Lazy<System.Threading.Tasks.Task<bool>>(() => System.Threading.Tasks.Task.Run(() =>
                            BuildDungeonOffTick(dungeonPlayfield, questId, entrance, dungeon)))).Value;
                    build.ContinueWith(done =>
                    {
                        if (!done.IsCompletedSuccessfully || !done.Result) return;
                        playfield.DispatchPlayerProjection(player, () =>
                        {
                            if (!ReferenceEquals(player.Playfield, playfield) || player.Session == null) return;
                            Reenter(player, target);
                        });
                    }, System.Threading.Tasks.TaskScheduler.Default);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Creates and builds the dungeon off any playfield tick. True when the entrance should be tried again: the
        /// dungeon is live here, or World placed it on another instance (the retry hands the player over). False
        /// when the build failed; that is logged and the player's next use of the entrance tries again.
        /// </summary>
        bool BuildDungeonOffTick(int dungeonPlayfield, string questId, MissionEntrancePlacement entrance, CachedDungeon dungeon)
        {
            try
            {
                if (!_layouts.TryGenerate(dungeon.Parameters, out DungeonLayout layout))
                {
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture, "Quest dungeon layout could not be generated playfield={0} quest={1}",
                        dungeonPlayfield, questId));
                    return false;
                }

                DungeonKills kills = _quests.LoadDungeonKills(dungeon.OwnerId, questId, dungeon.AssignedAtUtcTicks, dungeon.Parameters);
                MissionNpcObjectiveBinding? unfinishedNpc = UnfinishedNpc(dungeon.Parameters);
                MissionChestPopulation? chests = CreateChestPopulation(questId, dungeon);
                QuestDungeonPlayfield world = _playfields.Value.GetOrCreateQuestDungeon(dungeonPlayfield, questId, layout, entrance,
                    dungeon.TargetHash, dungeon.Parameters.Quality, UncollectedObjective(dungeon.Parameters), TryCollectObjective,
                    unfinishedNpc, kills,
                    spawnNpcObjective: unfinishedNpc != null, chestPopulation: chests);
                world.KillsCheckpoint ??= _quests.SaveDungeonKills;
                return true;
            }
            catch (ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException)
            {
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, string.Format(CultureInfo.InvariantCulture, "Quest dungeon build failed playfield={0} quest={1}",
                    dungeonPlayfield, questId));
                return false;
            }
            finally
            {
                _pendingBuilds.TryRemove(dungeonPlayfield, out _);
            }
        }

        /// <summary>Tries the entrance again after off-tick work finished for it, without the use cooldown in the way.</summary>
        void Reenter(Player player, Identity target)
        {
            _lastRequest.TryRemove(player.Identity.Instance, out _);
            TryEnter(player, target);
        }

        /// <summary>
        /// Item on item: a Mission Key Duplicator and a mission key, in either order, copy the key for the same
        /// dungeon. The duplicator is not used up. False when these are not a duplicator and a key.
        /// </summary>
        public bool TryDuplicate(Player player, Identity sourceSlot, Identity targetSlot)
        {
            ArgumentNullException.ThrowIfNull(player);
            // Overflow items can only be deleted or moved into the inventory.
            if (!player.Inventory.IsHydrated
                || sourceSlot.Type == IdentityType.OverflowWindow || targetSlot.Type == IdentityType.OverflowWindow
                || !player.Inventory.TryGetItem(sourceSlot.Type, sourceSlot.Instance, out Item source)
                || !player.Inventory.TryGetItem(targetSlot.Type, targetSlot.Instance, out Item target))
                return false;

            Item? key = source.LowId == KeyLowId ? source : target.LowId == KeyLowId ? target : null;
            Item? duplicator = source.LowId == DuplicatorLowId ? source : target.LowId == DuplicatorLowId ? target : null;
            if (key == null || duplicator == null || ReferenceEquals(key, duplicator))
                return false;

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.IsDead || player.Session == null)
                    return true;
                if (key.Locked || duplicator.Locked || !key.IsPersisted || !duplicator.IsPersisted || key.InstanceId <= 0
                    || !TryBeginRequest(player))
                    return true;

                try
                {
                    _flush.HardFlush(player);
                    Item copy = CreateKey();
                    if (!InventoryGrantPlan.TryCreate(player, [copy], out InventoryGrantPlan plan))
                    {
                        SendText(player, "Your inventory is full.");
                        return true;
                    }

                    DateTime now = DateTime.UtcNow;
                    string? questId = _store.TryAddDuplicateKey(key.InstanceId, KeyRow(plan.Rows[0], now), MaxKeysPerQuest, now.Ticks);
                    if (questId == null)
                    {
                        SendText(player, KeyRefused);
                        return true;
                    }

                    plan.PublishAfterCommit(notify: false);
                    string name = TryGetDungeon(questId, out CachedDungeon dungeon) ? dungeon.Parameters.EntranceName : string.Empty;
                    SendKey(player, copy, plan.Rows[0].ContainerPlacement, name);
                    _logger.Info(string.Format(CultureInfo.InvariantCulture, "Mission key copied char={0} quest={1} from={2} to={3}",
                        player.Identity.Instance, questId, key.InstanceId, copy.InstanceId));
                    return true;
                }
                catch (DatabaseCommitOutcomeUnknownException exception)
                {
                    Quarantine(player, exception);
                    return true;
                }
            }
        }

        /// <summary>
        /// Login inside a dungeon: the live dungeon when it still exists (the stored position stands); otherwise a
        /// new one from the quest's seed, arriving at its spawn; otherwise (quest over) its entrance, or the
        /// respawn point when the quest is gone.
        /// </summary>
        internal (Playfield Playfield, Vector3? Position, PlayfieldAdmission? Admission) ResolveLogin(int storedPlayfield)
        {
            PlayfieldManager playfields = _playfields.Value;
            if (!QuestDungeonIds.TryGetQuestId(storedPlayfield, out string questId))
                throw new ArgumentOutOfRangeException(nameof(storedPlayfield));

            if (playfields.TryGetQuestDungeon(storedPlayfield, out QuestDungeonPlayfield live))
            {
                return (live, null, null);
            }

            if (TryGetDungeon(questId, out CachedDungeon dungeon)
                && _entrances.TryGetByIdentity(unchecked((uint)dungeon.Parameters.EntranceType), unchecked((uint)dungeon.Parameters.EntranceInstance), out MissionEntrancePlacement entrance))
            {
                if (!dungeon.Ended && dungeon.ExpiresAtUtcTicks > DateTime.UtcNow.Ticks && dungeon.Parameters.DungeonPlayfield == storedPlayfield
                    && _layouts.TryGenerate(dungeon.Parameters, out DungeonLayout layout))
                {
                    DungeonKills kills = _quests.LoadDungeonKills(dungeon.OwnerId, questId, dungeon.AssignedAtUtcTicks, dungeon.Parameters);
                    MissionNpcObjectiveBinding? unfinishedNpc = UnfinishedNpc(dungeon.Parameters);
                    try
                    {
                        QuestDungeonPlayfield created = playfields.GetOrCreateQuestDungeon(storedPlayfield, questId, layout, entrance, dungeon.TargetHash,
                            dungeon.Parameters.Quality, UncollectedObjective(dungeon.Parameters), TryCollectObjective,
                            unfinishedNpc, kills,
                            spawnNpcObjective: unfinishedNpc != null, chestPopulation: CreateChestPopulation(questId, dungeon));
                        created.KillsCheckpoint ??= _quests.SaveDungeonKills;
                        return (created, layout.Spawn, null);
                    }
                    catch (ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException)
                    {
                        throw new ZoneEngine_New.Core.World.LoginRelocationException(storedPlayfield, layout.Spawn);
                    }
                }

                var entranceAt = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ);
                PlayfieldAdmission admission = AcquireForLogin(playfields, entrance.PlayfieldId, entranceAt);
                return (admission.Playfield, entranceAt, admission);
            }

            RespawnContentCatalog respawn = _gameData.RespawnContent;
            if (respawn.PlayfieldId <= 0 || respawn.Position is not { Length: 3 })
                throw new InvalidOperationException("No respawn destination is configured for a login in an ended dungeon.");
            var respawnAt = new Vector3(respawn.Position[0], respawn.Position[1], respawn.Position[2]);
            PlayfieldAdmission respawnAdmission = AcquireForLogin(playfields, respawn.PlayfieldId, respawnAt);
            return (respawnAdmission.Playfield, respawnAt, respawnAdmission);
        }

        /// <summary>A login moved out of a dungeon to a playfield another zone instance runs: it must log in there.</summary>
        static PlayfieldAdmission AcquireForLogin(PlayfieldManager playfields, int playfieldId, Vector3 position)
        {
            try
            {
                return playfields.AcquirePlayfield(playfieldId);
            }
            catch (ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException)
            {
                throw new ZoneEngine_New.Core.World.LoginRelocationException(playfieldId, position);
            }
        }

        /// <summary>
        /// World entry (login or zoning): re-sends each carried mission key with its dungeon's name, and retires
        /// any whose dungeon is gone. One indexed read for all of the player's keys. Also catches up team missions a
        /// teammate finished meanwhile.
        /// </summary>
        public void RestoreKeys(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.Session == null || !player.Inventory.IsHydrated)
                    return;

                try
                {
                    CatchUpTeamMissions(player);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Team mission catch-up failed char=" + player.Identity.Instance);
                }

                List<Item> keys = CarriedKeys(player);
                if (keys.Count == 0)
                    return;

                IDictionary<int, string> links = _store.LoadKeyQuests(keys.ConvertAll(key => key.InstanceId));
                RemoveDeadKeys(player, keys.Where(key => !links.ContainsKey(key.InstanceId)));
                foreach (KeyValuePair<int, Item> entry in player.Inventory.Inventory.Content)
                {
                    Item key = entry.Value;
                    if (key.LowId == KeyLowId && links.TryGetValue(key.InstanceId, out string? questId)
                        && TryGetDungeon(questId, out CachedDungeon dungeon))
                        SendKeyItem(player, key, dungeon.Parameters.EntranceName, entry.Key);
                }
            }
        }

        /// <summary>Debug view of a player's mission keys: where each is, and the quest its stored link opens.</summary>
        public IReadOnlyList<object> DescribeKeys(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            var held = new List<(string Page, int Slot, Item Item)>();
            if (player.Inventory.IsHydrated)
            {
                foreach ((IdentityType pageType, Container page) in DeletablePages(player))
                {
                    foreach (KeyValuePair<int, Item> entry in page.Content.ToArray())
                    {
                        if (entry.Value.LowId == KeyLowId || entry.Value.LowId == DuplicatorLowId)
                            held.Add((pageType.ToString(), entry.Key, entry.Value));
                    }
                }
            }

            IDictionary<int, string> links = _store.LoadKeyQuests(held.ConvertAll(entry => entry.Item.InstanceId));
            var rows = new List<object>(held.Count);
            foreach ((string page, int slot, Item item) in held)
            {
                string? questId = links.TryGetValue(item.InstanceId, out string? linked) ? linked : null;
                string? entrance = questId != null && TryGetDungeon(questId, out CachedDungeon dungeon) ? dungeon.Parameters.EntranceName : null;
                rows.Add(new
                {
                    page,
                    slot,
                    kind = item.LowId == KeyLowId ? "key" : "duplicator",
                    instance = item.InstanceId,
                    identity = string.Format(CultureInfo.InvariantCulture, "{0:X}:{1}", (int)item.Identity.Type, item.Identity.Instance),
                    persisted = item.IsPersisted,
                    locked = item.Locked,
                    questId,
                    name = entrance == null ? null : KeyNamePrefix + entrance
                });
            }

            return rows;
        }

        /// <summary>The stored dungeon parameters of a quest (seed, entrance, playfield), when the quest has a dungeon.</summary>
        public bool TryGetParameters(string questId, out QuestDungeonParameters parameters)
        {
            parameters = null!;
            if (string.IsNullOrEmpty(questId) || !TryGetDungeon(questId, out CachedDungeon dungeon))
                return false;
            parameters = dungeon.Parameters;
            return true;
        }

        /// <summary>How many mission keys for <paramref name="questId"/> the player carries on their pages. One database read.</summary>
        public int CountKeysFor(Player player, string questId)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (string.IsNullOrEmpty(questId) || !player.Inventory.IsHydrated)
                return 0;

            var keys = new List<int>();
            foreach ((IdentityType _, Container page) in DeletablePages(player))
            {
                foreach (Item item in page.Content.Values)
                {
                    if (item.LowId == KeyLowId)
                        keys.Add(item.InstanceId);
                }
            }

            if (keys.Count == 0)
                return 0;

            int count = 0;
            foreach (string linked in _store.LoadKeyQuests(keys).Values)
            {
                if (string.Equals(linked, questId, StringComparison.Ordinal))
                    count++;
            }

            return count;
        }

        /// <summary>The entrance a quest's dungeon is reached through, when the quest has one.</summary>
        public bool TryGetEntrance(string questId, out MissionEntrancePlacement entrance)
        {
            entrance = null!;
            return !string.IsNullOrEmpty(questId) && TryGetDungeon(questId, out CachedDungeon dungeon)
                && _entrances.TryGetByIdentity(unchecked((uint)dungeon.Parameters.EntranceType), unchecked((uint)dungeon.Parameters.EntranceInstance), out entrance);
        }

        /// <summary>
        /// Before a character's items load: retires their mission keys (pages, bank and bags) whose dungeon is gone,
        /// so dead keys never reach memory or the client.
        /// </summary>
        public void RetireDeadKeysOnLogin(int characterId)
        {
            int retired = _store.RetireDeadKeysForCharacter(characterId, KeyLowId, OwnedContainerTypes, (int)IdentityType.Container);
            if (retired > 0)
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Retired {0} dead mission keys for char={1}", retired, characterId));
        }

        void OnQuestEnded(Player player, PlayerQuest quest)
        {
            if (!QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters))
                return;

            try
            {
                // A team mission stays open while any member still holds it: one member finishing, leaving or deleting
                // it ends only their part. The last holder's end (or expiry, through the sweep) ends the dungeon.
                if (quest.Template.IsTeamScope && _store.LoadActiveQuestHolders(quest.QuestId).Count > 0)
                {
                    _logger.Info(string.Format(CultureInfo.InvariantCulture,
                        "Team mission part ended char={0} quest={1} state={2}; other members still hold it",
                        player.Identity.Instance, quest.QuestId, quest.State));
                    return;
                }

                // Completing the quest lets whoever is inside stay (and leave by the exit); expiry and abandoning empty it.
                EndDungeon(quest.QuestId, parameters, evict: quest.State != QuestState.Completed);
            }
            catch (Exception exception)
            {
                // The expiry sweep retries: the quest's key links are still there.
                _logger.Error(exception, "Ending dungeon for quest " + quest.QuestId + " failed; the sweep will retry");
            }
        }

        /// <summary>
        /// Removes every key link and key. With <paramref name="evict"/> everyone inside is sent back to the entrance and
        /// the dungeon released; without it (a completed quest) they stay, nobody can enter any more, and the dungeon is
        /// released once the last of them has left.
        /// </summary>
        void EndDungeon(string questId, QuestDungeonParameters parameters, bool evict, int ownerId = 0)
        {
            MissionItemObjectiveBinding? artifact = ArtifactOf(parameters, evict);
            QuestDungeonKeyItemRow? artifactRow = artifact is { IsValid: true } ? new QuestDungeonKeyItemRow
            {
                InstanceId = artifact.Instance, ItemType = artifact.IdentityType,
                LowId = artifact.LowId, HighId = artifact.HighId, Quality = artifact.Quality,
                Source = (int)ItemSource.Quest, ContainerInstance = artifact.OwnerId
            } : null;
            IList<RetiredDungeonKey> retired = _store.EndQuestKeys(questId, artifactRow);
            EndDungeonInMemory(questId, parameters, evict, retired);

            // Key holders, the dungeon itself and its owner may be on other zone instances.
            if (_commands != null)
            {
                var arguments = new List<string> { questId, evict ? "1" : "0", ownerId.ToString(CultureInfo.InvariantCulture) };
                foreach (RetiredDungeonKey key in retired)
                {
                    arguments.Add(key.KeyInstanceId.ToString(CultureInfo.InvariantCulture));
                    arguments.Add(key.ContainerType.ToString(CultureInfo.InvariantCulture));
                    arguments.Add(key.ContainerInstance.ToString(CultureInfo.InvariantCulture));
                    arguments.Add(key.ParentContainerType.ToString(CultureInfo.InvariantCulture));
                    arguments.Add(key.ParentContainerInstance.ToString(CultureInfo.InvariantCulture));
                }

                _ = _commands.BroadcastAsync(DungeonEndedCommand, arguments.ToArray());
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Dungeon quest ended quest={0} keys={1} evicted={2}", questId, retired.Count, evict));
        }

        static MissionItemObjectiveBinding? ArtifactOf(QuestDungeonParameters parameters, bool evict)
            => evict ? parameters.MissionType switch
            {
                3 => parameters.ObjectiveTool,
                4 => parameters.ObjectiveItem,
                _ => null
            } : null;

        /// <summary>Another zone instance ended a dungeon quest (its rows are already gone): do this instance's part.</summary>
        void OnDungeonEndedElsewhere(AORebirth.World.Contracts.V1.InstanceCommand command)
        {
            if (command.Arguments.Count < 3) return;
            string questId = command.Arguments[0];
            bool evict = command.Arguments[1] == "1";
            int ownerId = int.Parse(command.Arguments[2], CultureInfo.InvariantCulture);
            var retired = new List<RetiredDungeonKey>();
            for (int i = 3; i + 4 < command.Arguments.Count; i += 5)
                retired.Add(new RetiredDungeonKey
                {
                    KeyInstanceId = int.Parse(command.Arguments[i], CultureInfo.InvariantCulture),
                    ContainerType = int.Parse(command.Arguments[i + 1], CultureInfo.InvariantCulture),
                    ContainerInstance = int.Parse(command.Arguments[i + 2], CultureInfo.InvariantCulture),
                    ParentContainerType = int.Parse(command.Arguments[i + 3], CultureInfo.InvariantCulture),
                    ParentContainerInstance = int.Parse(command.Arguments[i + 4], CultureInfo.InvariantCulture)
                });

            GeneratedQuestRow? row = _store.LoadGenerated(questId);
            if (row != null && QuestDungeonParameters.TryParse(row.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters))
                EndDungeonInMemory(questId, parameters, evict, retired);
            else
                _dungeons.TryRemove(questId, out _);

            PlayfieldManager playfields = _playfields.Value;
            if (ownerId > 0 && playfields.FindPlayer(ownerId, out Player owner) && owner.Playfield is Playfield at)
                at.DispatchPlayerProjection(owner, () => _quests.GetLog(owner));
        }

        /// <summary>This instance's part of ending a dungeon: its cache, keys its players carry, the dungeon if it runs here.</summary>
        void EndDungeonInMemory(string questId, QuestDungeonParameters parameters, bool evict, IList<RetiredDungeonKey> retired)
        {
            if (evict)
                _dungeons.TryRemove(questId, out _);
            else
                _dungeons[questId] = _dungeons.TryGetValue(questId, out CachedDungeon? cached)
                    ? cached with { Ended = true }
                    : new CachedDungeon(parameters, 0, null, Ended: true);

            MissionItemObjectiveBinding? artifact = ArtifactOf(parameters, evict);
            PlayfieldManager playfields = _playfields.Value;
            foreach (RetiredDungeonKey key in retired)
            {
                int holder = HolderOf(key);
                if (holder > 0 && playfields.FindPlayer(holder, out Player online) && online.Playfield is Playfield at)
                {
                    int instance = key.KeyInstanceId;
                    if (artifact != null && instance == artifact.Instance)
                        at.DispatchPlayerProjection(online, () => RemoveMissionArtifactInMemory(online, artifact, key));
                    else
                        at.DispatchPlayerProjection(online, () => RemoveKeyInMemory(online, instance));
                }
            }

            if (playfields.TryGetQuestDungeon(parameters.DungeonPlayfield, out QuestDungeonPlayfield dungeon))
            {
                if (evict)
                {
                    dungeon.ChestPopulation?.Retire();
                    var landing = new Vector3(dungeon.Entrance.LocalX, dungeon.Entrance.LocalY, dungeon.Entrance.LocalZ);
                    foreach (Player inside in dungeon.GetRequiredService<DynelRegistry>().PlayerEntities().ToArray())
                        dungeon.DispatchPlayerProjection(inside, () =>
                        {
                            if (inside.Session == null)
                                return;
                            // Built (or resolved to another instance) off this tick.
                            playfields.WithPlayfield(dungeon.Entrance.PlayfieldId, inside,
                                destination => inside.Session?.TransferToPlayfield(destination, landing),
                                new ZoneEngine_New.Core.World.RemoteArrival(landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport));
                        });
                }

                // Occupied: the sweep releases it once empty.
                if (!playfields.TryReleaseQuestDungeon(parameters.DungeonPlayfield, questId))
                    _pendingRelease[parameters.DungeonPlayfield] = questId;
            }
        }

        /// <summary>Periodic work off every playfield thread: idle dungeons, expired quests, stored-quest cleanup.</summary>
        void Sweep()
        {
            if (Interlocked.Exchange(ref _sweeping, 1) == 1)
                return;

            try
            {
                DateTime now = DateTime.UtcNow;
                PlayfieldManager playfields = _playfields.Value;
                playfields.SweepQuestDungeons(now, EmptyDungeonLifetime);
                foreach (KeyValuePair<int, string> pending in _pendingRelease.ToArray())
                {
                    if (playfields.TryReleaseQuestDungeon(pending.Key, pending.Value))
                        _pendingRelease.TryRemove(pending.Key, out _);
                }

                long cutoff = now.Ticks - RequestCooldown.Ticks;
                foreach (KeyValuePair<int, long> request in _lastRequest.ToArray())
                {
                    if (request.Value < cutoff)
                        _lastRequest.TryRemove(request.Key, out _);
                }

                // The database-wide half runs on one zone instance only (World's duty holder); the others hear of
                // each ended dungeon through World.
                if (_world != null && !_world.IsDutyHolder)
                    return;

                foreach (string questId in _store.LoadExpiredQuestsWithKeys(now.Ticks, SweepBatch))
                {
                    GeneratedQuestRow? row = _store.LoadGenerated(questId);
                    if (row != null && QuestDungeonParameters.TryParse(row.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters))
                        EndDungeon(questId, parameters, evict: true, row.OwnerId);
                    else
                        _store.EndQuestKeys(questId);

                    _store.CloseCharacterQuests(questId, (int)QuestState.Expired, now.Ticks);

                    // An online owner's log closes it (journal entry, "quest expired") on their own playfield.
                    if (row != null && playfields.FindPlayer(row.OwnerId, out Player owner) && owner.Playfield is Playfield at)
                        at.DispatchPlayerProjection(owner, () => _quests.GetLog(owner));
                }

                _store.PurgeEndedGeneratedQuests(now.Ticks - EndedQuestRetention.Ticks, SweepBatch);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Quest dungeon sweep failed; it runs again next interval");
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }

        bool TryGetDungeon(string questId, out CachedDungeon dungeon)
        {
            // A reward transaction can finish before its publication (or return an unknown commit outcome).
            // Never let an active cached layout override the durable owner's closed mission state.
            if (_dungeons.TryGetValue(questId, out dungeon!) && dungeon.Ended)
                return true;

            GeneratedQuestRow? row = _store.LoadGenerated(questId);
            if (row == null || !QuestDungeonParameters.TryParse(row.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters))
                return false;

            QuestTemplate? template = null;
            try
            {
                template = System.Text.Json.JsonSerializer.Deserialize<QuestTemplate>(row.DefinitionJson ?? string.Empty, QuestTemplate.JsonOptions);
            }
            catch (System.Text.Json.JsonException exception)
            {
                _logger.Error(exception, "Quest " + questId + " has an unreadable definition; its dungeon gets no kill target");
            }

            bool ended = true;
            long assignedAtUtcTicks = 0;
            foreach (CharacterQuestRow owned in _store.Load(row.OwnerId))
            {
                if (string.Equals(owned.QuestId, questId, StringComparison.Ordinal))
                {
                    ended = owned.State != (int)QuestState.Active;
                    assignedAtUtcTicks = owned.AssignedAtUtcTicks;
                    break;
                }
            }

            dungeon = new CachedDungeon(parameters, row.ExpiresAtUtcTicks,
                parameters.ObjectiveNpc == null ? KillTarget(template) : null, ended, row.OwnerId, assignedAtUtcTicks,
                row.AcgBuildingGeneratorJson ?? string.Empty);
            if (row.ExpiresAtUtcTicks > DateTime.UtcNow.Ticks)
            {
                _dungeons[questId] = dungeon;
                // A dungeon first seen after a restart: start its bake as the accept would have.
                if (!ended && _playfields.IsValueCreated)
                    _playfields.Value.DungeonNavMeshes.Prewarm(parameters, _layouts);
            }

            return true;
        }

        MissionChestPopulation? CreateChestPopulation(string questId, CachedDungeon dungeon)
            => _quests.CreateChestPopulation(dungeon.OwnerId, questId, dungeon.AssignedAtUtcTicks, dungeon.Parameters, _layouts.ChestPolicy);

        /// <summary>The NPC hash a kill quest wants, or null for other quests.</summary>
        static string? KillTarget(QuestTemplate? template)
            => template != null && template.IsKill && !string.IsNullOrWhiteSpace(template.Objective?.Npc) ? template.Objective.Npc : null;

        /// <summary>
        /// Fixes the item's identity and catalog choice. Its room and position are not chosen here: the dungeon derives
        /// them from its seed when it builds.
        /// </summary>
        bool TryPrepareObjective(Player player, string questId, DungeonFindItemPolicy? policy,
            int missionType, bool sourceItem,
            out MissionItemObjectiveBinding objective, out string objectiveHash)
        {
            objective = null!;
            objectiveHash = string.Empty;
            if (_hashItems == null || policy?.ItemHashes is not { Length: > 0 } || policy.Quality <= 0
                || policy.QuestInstanceStat < 0 || policy.WorldScalePercent <= 0 || policy.ItemHashes.Any(string.IsNullOrWhiteSpace))
                return false;
            var candidates = new List<(string Hash, int Low, int High, int Quality)>();
            foreach (string hash in policy.ItemHashes.Distinct(StringComparer.Ordinal))
            {
                if (!_hashItems.TryRollIds(hash, policy.Quality, out int low, out int high, out int quality))
                    return false;
                try { _items.CreateTemplate(low, high, quality); }
                catch (Exception exception) { _logger.Error(exception, "Mission objective template is unavailable: " + hash); return false; }
                candidates.Add((hash, low, high, quality));
            }
            var selected = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
            objectiveHash = selected.Hash;
            objective = new MissionItemObjectiveBinding
            {
                QuestId = questId, OwnerId = player.Identity.Instance,
                IdentityType = (int)IdentityType.Terminal,
                Instance = _ids.Allocate(), LowId = selected.Low, HighId = selected.High, Quality = selected.Quality,
                QuestInstanceStat = policy.QuestInstanceStat, WorldFlags = policy.WorldFlags,
                WorldScalePercent = policy.WorldScalePercent,
                MissionType = missionType, IsSourceItem = sourceItem
            };
            return objective.IsValid;
        }

        /// <summary>
        /// Fixes the objective NPC's hash and identity. Its room and position are not chosen here: the dungeon derives
        /// them from its seed when it builds.
        /// </summary>
        bool TryPrepareNpc(Player player, string questId, int missionType, int quality, string? npcHash,
            out MissionNpcObjectiveBinding objective)
        {
            objective = null!;
            DungeonNpcObjectivePolicy? policy = _layouts.NpcObjectivePolicy;
            string[]? hashes = !string.IsNullOrWhiteSpace(npcHash) ? [npcHash]
                : missionType == (int)MissionRollType.FindPerson ? policy?.FindPersonNpcHashes : policy?.NpcHashes;
            // The objective policy (hash pool and kill-target enlargement) is for rolled missions. An authored quest names
            // its own NPC and keeps that NPC's catalog size.
            int? scalePercent = npcHash == null && missionType == (int)MissionRollType.KillPerson ? policy?.KillPersonScalePercent : null;
            if (quality <= 0 || scalePercent is <= 0 || hashes is not { Length: > 0 }
                || hashes.Any(string.IsNullOrWhiteSpace))
                return false;
            var candidates = new List<MobTemplate>();
            foreach (string hash in hashes.Distinct(StringComparer.Ordinal))
            {
                if (!_gameData.HasMobTemplate(hash) || !_gameData.TryResolveMobTemplate(hash, quality, out MobTemplate template)
                    || template.UnresolvedPlaceholder || !NpcTemplateValidation.CanSpawn(template)
                    || missionType == 0 && template.Has(NpcFeature.NoCombat))
                    return false;
                candidates.Add(template);
            }
            MobTemplate selected = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
            objective = new MissionNpcObjectiveBinding
            {
                QuestId = questId, OwnerId = player.Identity.Instance, Instance = _ids.Allocate(),
                NpcHash = selected.Hash, Quality = quality, ScalePercent = scalePercent, MissionType = missionType
            };
            return objective.IsValid;
        }

        MissionItemObjectiveBinding? UncollectedObjective(QuestDungeonParameters parameters)
        {
            MissionItemObjectiveBinding? binding = parameters.ObjectiveItem;
            if (binding?.IsValid != true)
                return null;
            // The layout cache never decides collection state: consult the durable owner row on reconstruction.
            CharacterQuestRow? row = _store.Load(binding.OwnerId).FirstOrDefault(q => q.QuestId == binding.QuestId);
            int collectionLimit = binding.MissionType == 4 ? 1 : row?.RequiredCount ?? 0;
            return row is { State: (int)QuestState.Active } && row.Progress < collectionLimit ? binding : null;
        }

        MissionNpcObjectiveBinding? UnfinishedNpc(QuestDungeonParameters parameters)
        {
            MissionNpcObjectiveBinding? binding = parameters.ObjectiveNpc;
            if (binding?.IsValid != true)
                return null;
            CharacterQuestRow? row = _store.Load(binding.OwnerId).FirstOrDefault(q => q.QuestId == binding.QuestId);
            return row is { State: (int)QuestState.Active } && row.Progress < row.RequiredCount ? binding : null;
        }

        bool TryCollectObjective(Player player, MissionObjectiveItem objective)
            => _quests.TryCollectFindItem(player, objective);

        public bool TryUseGeneratedMissionItem(Player player, Identity sourceSlot, Identity target, out bool handled)
            => _quests.TryUseGeneratedMissionItem(player, sourceSlot, target, out handled);

        /// <summary>Keys the player carries: main inventory and bags opened from it.</summary>
        static List<Item> CarriedKeys(Player player)
        {
            var keys = new List<Item>();
            foreach (Item item in player.Inventory.EnumerateHeldItems())
            {
                if (item.LowId == KeyLowId && item.InstanceId > 0 && item.IsPersisted && !item.Locked)
                    keys.Add(item);
                if (keys.Count >= MaxKeysPerQuest * 4)
                    break;
            }

            return keys;
        }

        void RemoveDeadKeys(Player player, IEnumerable<Item> dead)
        {
            var ids = dead.Select(item => item.InstanceId).ToList();
            if (ids.Count == 0)
                return;

            foreach (int instance in _store.RetireUnlinkedKeys(ids, KeyLowId))
                RemoveKeyInMemory(player, instance);
        }

        /// <summary>
        /// Drops a retired key from the holder's pages through the normal inventory delete (so no pending save can
        /// write it back) and tells the client. Keys inside bags are left until that bag next loads, where the
        /// retired row is no longer read.
        /// </summary>
        void RemoveKeyInMemory(Player player, int keyInstanceId)
        {
            if (!player.Inventory.IsHydrated)
                return;

            foreach ((IdentityType pageType, Container page) in DeletablePages(player))
            {
                foreach (KeyValuePair<int, Item> entry in page.Content)
                {
                    if (entry.Value.InstanceId != keyInstanceId || entry.Value.LowId != KeyLowId)
                        continue;

                    var slot = new Identity { Type = pageType, Instance = entry.Key };
                    if (_inventoryActions.TryDelete(player, slot, keyInstanceId))
                        player.Session?.Send(new CharacterActionMessage
                        {
                            Identity = player.Identity,
                            Action = CharacterActionType.DeleteItem,
                            Target = slot
                        });
                    return;
                }
            }
        }

        void RemoveMissionArtifactInMemory(Player player, MissionItemObjectiveBinding binding, RetiredDungeonKey retired)
        {
            lock (player.PersistenceGate)
            {
                if (!player.Inventory.IsHydrated)
                    return;
                var pages = DeletablePages(player).Select(entry => entry.Item2).ToList();
                if (retired.ContainerType == (int)IdentityType.Container
                    && player.Inventory.TryGetBackpackPage(new Identity
                        { Type = IdentityType.Container, Instance = retired.ContainerInstance }, out Container bag))
                    pages.Add(bag);
                foreach (Container page in pages)
                {
                    foreach (KeyValuePair<int, Item> entry in page.Content.ToArray())
                    {
                        Item item = entry.Value;
                        if (item.InstanceId != binding.Instance || item.Identity != binding.Identity
                            || item.LowId != binding.LowId || item.HighId != binding.HighId || item.Quality != binding.Quality)
                            continue;
                        // Override any queued location save with the same durable retirement already committed by EndQuestKeys.
                        player.Inventory.Discard(item, new Identity { Type = IdentityType.None, Instance = 0 });
                        page.Content.Remove(entry.Key);
                        _flush.NotifyDirty(player);
                        Modules.ModuleHooks.ItemMoved(player, item, ItemPlaces.Of(page),
                            Modules.ModuleHooks.Place(AORebirth.Modules.Events.ItemPlaceKind.Destroyed));
                        if (page.Identity.Type == IdentityType.Container)
                        {
                            if (page.IsOpen && page.InventoryHandle > 0)
                                player.Session?.Send(page.BuildInventoryUpdateMessage(player.Identity, page.Identity,
                                    page.InventoryHandle, unknown1: 3, unknown2: 1));
                        }
                        else
                            player.Session?.Send(new CharacterActionMessage
                            {
                                Identity = player.Identity, Action = CharacterActionType.DeleteItem,
                                Target = new Identity { Type = page.Identity.Type, Instance = entry.Key }
                            });
                        return;
                    }
                }
            }
        }

        static IEnumerable<(IdentityType, Container)> DeletablePages(Player player)
        {
            PlayerInventory inventory = player.Inventory;
            yield return (IdentityType.Inventory, inventory.Inventory);
            yield return (IdentityType.OverflowWindow, inventory.Overflow);
            yield return (IdentityType.WeaponPage, inventory.Equipment);
            yield return (IdentityType.ArmorPage, inventory.Armor);
            yield return (IdentityType.ImplantPage, inventory.Implant);
            yield return (IdentityType.SocialPage, inventory.Social);
            if (inventory.Bank.IsHydrated)
                yield return (IdentityType.BankByRef, inventory.Bank);
        }

        /// <summary>The character a retired key was stored with: directly on their pages or bank, or in a bag there.</summary>
        static int HolderOf(RetiredDungeonKey key)
        {
            if (key.ContainerType == (int)IdentityType.Container)
                return Array.IndexOf(OwnedContainerTypes, key.ParentContainerType) >= 0 ? key.ParentContainerInstance : 0;
            return Array.IndexOf(OwnedContainerTypes, key.ContainerType) >= 0 ? key.ContainerInstance : 0;
        }

        static bool WithinEntrance(Player player, MissionEntrancePlacement entrance)
        {
            double dx = player.Position.x - entrance.LocalX;
            double dz = player.Position.z - entrance.LocalZ;
            return (dx * dx) + (dz * dz) <= EntryHorizontalRange * EntryHorizontalRange
                && Math.Abs(player.Position.y - entrance.LocalY) <= EntryVerticalRange;
        }

        bool TryBeginRequest(Player player)
        {
            long now = DateTime.UtcNow.Ticks;
            int id = player.Identity.Instance;
            if (_lastRequest.TryGetValue(id, out long last) && now - last < RequestCooldown.Ticks)
                return false;
            _lastRequest[id] = now;
            return true;
        }

        Item CreateKey()
        {
            int id = _ids.Allocate();
            return _items.Create(KeyLowId, KeyLowId, 1, ItemSource.Quest, 1, id, new Identity { Type = (IdentityType)KeyItemType, Instance = id });
        }

        static QuestDungeonKeyItemRow KeyRow(ItemInstanceRecord row, DateTime now) => new()
        {
            InstanceId = row.InstanceId,
            ContainerType = row.ContainerType,
            ContainerInstance = row.ContainerInstance,
            ContainerPlacement = row.ContainerPlacement,
            ItemType = row.ItemType,
            LowId = row.LowId,
            HighId = row.HighId,
            Quality = row.Quality,
            Source = (int)row.Source,
            CreatedAtUtcTicks = now.Ticks
        };

        /// <summary>Overflow window slot the key is created in before it moves to its inventory slot.</summary>
        const int OverflowSlot = 0x6F;

        /// <summary>
        /// A new key, in the captured live order (capture 20260724-134055, the retired engine's
        /// MissionKeyGrantService): SimpleItemFullUpdate creates the named key in the overflow window,
        /// ContainerAddItem routes it through overflow, then TemplateAction Overflow and TemplateAction Use move it to
        /// its inventory slot. The client only places an item it already knows, so the item update comes first.
        /// </summary>
        static void SendKey(Player player, Item key, int slot, string entranceName)
            => SendGrantedMissionItem(player, key, slot, KeyNamePrefix + entranceName);

        static void SendGrantedMissionItem(Player player, Item item, int slot, string name)
        {
            if (player.Session == null || player.Playfield == null)
                return;

            SendNamedMissionItem(player, item, name, OverflowSlot);
            player.Session.Send(new ContainerAddItemMessage
            {
                Identity = player.Identity,
                Unknown = 0,
                SourceContainer = new Identity { Type = IdentityType.OverflowWindow, Instance = 0 },
                Target = new Identity { Type = IdentityType.OverflowWindow, Instance = player.Identity.Instance },
                TargetPlacement = OverflowSlot
            });
            player.Session.Send(KeyTemplateAction(player, item, TemplateActionType.Overflow,
                new Identity { Type = IdentityType.OverflowWindow, Instance = 0 }, 0, 0));
            player.Session.Send(KeyTemplateAction(player, item, TemplateActionType.Use,
                new Identity { Type = IdentityType.Inventory, Instance = slot }, (int)player.Identity.Type, player.Identity.Instance));
        }

        static TemplateActionMessage KeyTemplateAction(Player player, Item key, TemplateActionType action, Identity placement, int arg3, int arg4) => new()
        {
            Identity = player.Identity,
            Unknown = 0,
            ItemLowId = key.LowId,
            ItemHighId = key.HighId,
            Quality = key.Quality,
            Count = 1,
            Action = action,
            Placement = placement,
            Unknown3 = arg3,
            Unknown4 = arg4
        };

        /// <summary>
        /// The key's SimpleItemFullUpdate: its stats and its name, "Mission key to" the entrance, at
        /// <paramref name="placement"/> (the overflow slot for a new key, its inventory slot when renaming a held one).
        /// The name's length prefix counts its trailing NUL; the writer does not add one.
        /// </summary>
        static void SendKeyItem(Player player, Item key, string entranceName, int placement)
            => SendNamedMissionItem(player, key, KeyNamePrefix + entranceName, placement);

        static void SendNamedMissionItem(Player player, Item key, string name, int placement)
        {
            if (player.Session == null || player.Playfield == null)
                return;

            static GameTuple<CharacterStat, uint> Stat(CharacterStat id, uint value) => new() { Value1 = id, Value2 = value };
            player.Session.Send(new SimpleItemFullUpdateMessage
            {
                Identity = key.Identity,
                Unknown = 0,
                MsgVersion = 0x0B,
                Identitytype = (int)player.Identity.Type,
                Instance = player.Identity.Instance,
                Playfield = player.Playfield.ClientPlayfieldId,
                Unknown1 = new Identity { Type = (IdentityType)0xF424F, Instance = 0 },
                Unknown2 = 0x71,
                Unknown3 = (byte)placement,
                Stats =
                [
                    Stat(CharacterStat.Flags, unchecked((uint)key.GetStat(CharacterStat.Flags))),
                    Stat(CharacterStat.StaticInstance, (uint)key.LowId),
                    Stat(CharacterStat.ACGItemLevel, (uint)key.Quality),
                    Stat(CharacterStat.ACGItemTemplateID, (uint)key.LowId),
                    Stat(CharacterStat.ACGItemTemplateID2, (uint)key.HighId),
                    Stat(CharacterStat.MultipleCount, (uint)key.StackCount)
                ],
                Name = name + '\0'
            });
        }

        static void SendText(Player player, string text)
            => player.Session?.Send(new ChatTextMessage { Identity = player.Identity, Text = text });

        void Quarantine(Player player, Exception exception)
        {
            player.QuarantinePersistence();
            player.Session?.Close();
            _logger.Error(exception, "Dungeon quest commit outcome unknown; player persistence quarantined");
        }
    }
}
