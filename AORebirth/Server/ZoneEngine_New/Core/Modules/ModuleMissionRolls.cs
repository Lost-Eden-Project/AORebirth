namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Security.Cryptography;
    using System.Threading;

    using AORebirth.Modules.Missions;
    using AORebirth.Modules.World;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Quests;
    using ZoneEngine_New.Core.Quests.Dungeons;

    /// <summary>
    /// Mission terminals, run by the module that registered an <see cref="IMissionRollHook"/> (module API 1.10). A roll
    /// asks the hook for offers; each is checked, its entrance and reward item ids are frozen, and the player's offers are
    /// kept here until the next roll. Accepting asks the hook again and gives what it decides; an offer is used once.
    /// The client only names the offer: everything given comes from the frozen offer or the hook. Without a hook,
    /// terminals are out of service. Runs on the player's playfield tick.
    /// </summary>
    public sealed class ModuleMissionRolls
    {
        internal const string OutOfService = "This mission terminal is out of service.";

        /// <summary>Same spacing as mission key and entrance requests: a roll runs module code and resolves entrances.</summary>
        static readonly TimeSpan RollCooldown = TimeSpan.FromMilliseconds(750);

        /// <summary>Longest offer description sent: five of them stay well inside one message.</summary>
        const int MaxDescriptionLength = 4000;

        /// <summary>Most entries an offer's ExperienceByLevel may have.</summary>
        const int MaxExperienceLevels = 1000;

        readonly ZoneModuleHost _modules;
        readonly QuestDungeonService _dungeons;
        readonly QuestService _quests;
        readonly HashItemMinter _minter;
        readonly IZoneLogger _logger;

        /// <summary>Per player, dropped with the player: the last roll time and the offers it showed.</summary>
        readonly ConditionalWeakTable<Player, RollState> _states = new();

        int _nextInstance = RandomNumberGenerator.GetInt32(1, 0x40000000);

        public ModuleMissionRolls(ZoneModuleHost modules, QuestDungeonService dungeons, QuestService quests, HashItemMinter minter,
            IZoneLogger logger)
        {
            _modules = modules ?? throw new ArgumentNullException(nameof(modules));
            _dungeons = dungeons ?? throw new ArgumentNullException(nameof(dungeons));
            _quests = quests ?? throw new ArgumentNullException(nameof(quests));
            _minter = minter ?? throw new ArgumentNullException(nameof(minter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>A roll at <paramref name="terminal"/>, already checked to be the player's and in range.</summary>
        public void Roll(Player player, Playfield playfield, MissionTerminal terminal, QuestAlternativeMessage request, IZoneSession session)
        {
            if (_modules.MissionRollHook is not { } hook)
            {
                Text(session, player, OutOfService);
                return;
            }

            RollState state = _states.GetValue(player, _ => new RollState());
            long now = Stopwatch.GetTimestamp();
            lock (state)
            {
                if (state.LastRoll != 0 && Stopwatch.GetElapsedTime(state.LastRoll, now) < RollCooldown)
                    return;
                state.LastRoll = now;
            }

            Teams.TeamSnapshot? team = playfield.GetService<Teams.TeamService>()?.GetImmediateTeam(player);
            var input = new MissionRollRequest
            {
                CharacterId = player.Identity.Instance,
                Name = player.Name,
                Level = player.Stats.GetOrZero(CharacterStat.Level),
                Profession = player.Stats.GetOrZero(CharacterStat.Profession),
                Breed = player.Stats.GetOrZero(CharacterStat.Breed),
                Side = player.Stats.GetOrZero(CharacterStat.Side),
                TeamId = team?.TeamId ?? 0,
                TeamMemberIds = team?.MemberIds.ToArray() ?? [],
                PlayfieldId = playfield.Identity.Instance,
                Terminal = ModuleHooks.Entity(terminal.Identity),
                TerminalPosition = new WorldPosition(terminal.Position.xf, terminal.Position.yf, terminal.Position.zf),
                Difficulty = request.LevelSlider,
                Sliders = new MissionSliders(Slider(request.GoodBadSlider), Slider(request.OrderChaosSlider),
                    Slider(request.OpenHiddenSlider), Slider(request.PhysicalMysticalSlider), Slider(request.HeadOnStealthSlider),
                    Slider(request.MoneyExperienceSlider)),
                Timestamp = RequestTime(now)
            };

            MissionRollResult? result;
            try
            {
                using (Metrics.TickStallWatch.Enter("mission.roll.module", player.Identity.Instance))
                    result = hook.Roll(input);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Module " + _modules.MissionRollHookOwner + " mission roll failed; no offers shown.");
                Text(session, player, "The mission terminal could not issue the roll.");
                return;
            }

            IReadOnlyList<MissionOffer> offered = result?.Offers ?? [];
            if (offered.Count > MissionRollResult.MaxOffers)
                _logger.Warn(string.Format(CultureInfo.InvariantCulture, "Module {0} rolled {1} mission offers; only the first {2} are shown.",
                    _modules.MissionRollHookOwner, offered.Count, MissionRollResult.MaxOffers));

            var infos = new List<QuestInfo>(MissionRollResult.MaxOffers);
            var offers = new Dictionary<int, FrozenOffer>(MissionRollResult.MaxOffers);
            DateTime nowUtc = DateTime.UtcNow;
            using (Metrics.TickStallWatch.Enter("mission.roll.freeze", player.Identity.Instance))
            {
                foreach (MissionOffer offer in offered.Take(MissionRollResult.MaxOffers))
                {
                    if (!TryFreeze(offer, out FrozenOffer? frozen, out string problem))
                    {
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture, "Module {0} mission offer '{1}' left out: {2}",
                            _modules.MissionRollHookOwner, offer?.Key, problem));
                        continue;
                    }

                    int instance = NextInstance();
                    DateTime expires = nowUtc.AddHours(frozen.LifetimeHours);
                    frozen.ExpiresUtc = expires;
                    QuestTemplate mission = frozen.Mission;
                    infos.Add(MissionOfferWire.Offer(player, instance, terminal.Identity, frozen.Type, mission.Title ?? mission.Name,
                        mission.Description ?? string.Empty, mission.Dungeon!.Quality ?? 1, mission.Reward?.Credits ?? 0,
                        mission.Reward?.XpFor(player.Stats.GetOrOne(CharacterStat.Level)) ?? 0, frozen.RewardItems, mission.IconId ?? 0, mission.Objective, frozen.Entrance,
                        frozen.WorldPosition, expires));
                    offers[instance] = frozen;
                }
            }

            lock (state)
                state.Batch = offers.Count == 0 ? null : new Batch(playfield.Identity.Instance, terminal.Identity, offers);

            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Mission roll char={0} module={1} pf={2} terminal={3:X8} difficulty={4} offered={5} shown={6}",
                player.Identity.Instance, _modules.MissionRollHookOwner, playfield.Identity.Instance,
                unchecked((uint)terminal.Identity.Instance), input.Difficulty, offered.Count, infos.Count));

            if (infos.Count == 0)
            {
                Text(session, player, result?.Message is { Length: > 0 } refusal ? refusal : "No missions are available here right now.");
                return;
            }

            session.Send(MissionOfferWire.Response(player, request, infos.ToArray()));
            if (result?.Message is { Length: > 0 } message)
                Text(session, player, message);
        }

        /// <summary>
        /// The player accepted the offer <paramref name="offerIdentity"/>. An identity that is not one of the player's
        /// current offers is ignored.
        /// </summary>
        public void Accept(Player player, Identity offerIdentity, IZoneSession session)
        {
            if (!_states.TryGetValue(player, out RollState? state))
                return;

            Batch? batch;
            FrozenOffer? offer;
            lock (state)
            {
                batch = state.Batch;
                if (batch == null || !batch.Offers.TryGetValue(offerIdentity.Instance, out offer) || offer.Accepting)
                    return;
                if (DateTime.UtcNow >= offer.ExpiresUtc)
                {
                    batch.Offers.Remove(offerIdentity.Instance);
                    Text(session, player, "This mission offer has expired.");
                    return;
                }

                offer.Accepting = true;
            }

            bool used = false;
            try
            {
                used = Give(player, session, batch, offer);
            }
            finally
            {
                lock (state)
                {
                    if (used)
                        batch.Offers.Remove(offerIdentity.Instance);
                    else
                        offer.Accepting = false;
                }
            }
        }

        /// <summary>Asks the hook and gives what it decides. True when the offer is used up.</summary>
        bool Give(Player player, IZoneSession session, Batch batch, FrozenOffer offer)
        {
            if (player.Playfield is not { } playfield || playfield.Identity.Instance != batch.PlayfieldId
                || !playfield.GetRequiredService<DynelRegistry>().TryGet(batch.Terminal, out Dynel? dynel)
                || dynel is not MissionTerminal terminal || terminal.GetEdgeDistanceTo(player) > LootableDynel.OpenRange)
            {
                Text(session, player, "You need to be at the mission terminal to accept a mission.");
                return false;
            }

            if (_modules.MissionRollHook is not { } hook)
            {
                Text(session, player, OutOfService);
                return false;
            }

            string owner = _modules.MissionRollHookOwner ?? "?";
            MissionAcceptDecision? decision;
            try
            {
                using (Metrics.TickStallWatch.Enter("mission.accept.module", player.Identity.Instance))
                    decision = hook.Accept(new MissionAcceptRequest
                    {
                        CharacterId = player.Identity.Instance,
                        PlayfieldId = playfield.Identity.Instance,
                        Terminal = ModuleHooks.Entity(batch.Terminal),
                        OfferKey = offer.Shown.Key,
                        Offer = offer.Shown,
                        Timestamp = RequestTime(Stopwatch.GetTimestamp())
                    });
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Module " + owner + " mission accept failed; nothing given.");
                Text(session, player, "The mission terminal could not accept the mission.");
                return false;
            }

            if (decision == null)
            {
                _logger.Warn("Module " + owner + " mission accept returned no decision; nothing given.");
                Text(session, player, "The mission terminal could not accept the mission.");
                return false;
            }

            string? refused;
            switch (decision.Kind)
            {
                case MissionAcceptKind.Refuse:
                    if (!string.IsNullOrEmpty(decision.Message))
                        Text(session, player, decision.Message);
                    return false;
                case MissionAcceptKind.GiveOffered:
                    refused = _dungeons.AssignRolledMission(player, offer.Mission);
                    break;
                case MissionAcceptKind.Give:
                    if (!TryFreeze(decision.Mission, out FrozenOffer? replacement, out string problem))
                    {
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture, "Module {0} mission '{1}' for offer '{2}' unusable: {3}",
                            owner, decision.Mission?.Key, offer.Shown.Key, problem));
                        refused = "That mission is unavailable.";
                    }
                    else
                        refused = _dungeons.AssignRolledMission(player, replacement.Mission);
                    break;
                case MissionAcceptKind.GiveQuest:
                    if (_quests.TryAssignTemplate(player, decision.QuestHash ?? string.Empty, out string result, decision.QuestQuality))
                        refused = null;
                    else
                    {
                        _logger.Warn("Module " + owner + " mission offer '" + offer.Shown.Key + "' quest not given: " + result);
                        refused = "That mission could not be given.";
                    }
                    break;
                case MissionAcceptKind.Handled:
                    refused = null;
                    break;
                default:
                    refused = "That mission is unavailable.";
                    break;
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Mission accept char={0} module={1} offer='{2}' decision={3} result={4}",
                player.Identity.Instance, owner, offer.Shown.Key, decision.Kind, refused ?? "given"));
            if (refused != null)
            {
                if (!player.IsPersistenceQuarantined)
                    Text(session, player, refused);
                return false;
            }

            if (!string.IsNullOrEmpty(decision.Message))
                Text(session, player, decision.Message);
            return true;
        }

        /// <summary>
        /// Checks <paramref name="offer"/> and freezes it: the entrance it shows (and accepting uses), the reward item ids
        /// and the generated quest it gives. False with the reason when it is unusable.
        /// </summary>
        bool TryFreeze(MissionOffer? offer, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FrozenOffer? frozen, out string problem)
        {
            frozen = null;
            problem = string.Empty;
            if (offer == null)
                return Fail("no offer", out problem);
            if (!Enum.IsDefined(offer.Kind))
                return Fail("unknown Kind " + (int)offer.Kind, out problem);
            if (string.IsNullOrWhiteSpace(offer.Title))
                return Fail("no Title", out problem);
            if ((offer.Description?.Length ?? 0) > MaxDescriptionLength)
                return Fail("Description is longer than " + MaxDescriptionLength + " characters", out problem);
            if (offer.Quality is < 1 or > QuestDungeonTemplate.MaxMissionQuality)
                return Fail("Quality must be 1-" + QuestDungeonTemplate.MaxMissionQuality, out problem);
            if (offer.Credits < 0 || offer.Experience < 0)
                return Fail("Credits and Experience cannot be negative", out problem);
            if (offer.ExperienceByLevel is { } byLevel && (byLevel.Count > MaxExperienceLevels || byLevel.Any(xp => xp < 0)))
                return Fail("ExperienceByLevel needs at most " + MaxExperienceLevels + " entries, none negative", out problem);
            if (offer.LifetimeHours is <= 0)
                return Fail("LifetimeHours must be positive", out problem);
            if (offer.Dungeon is not { } dungeon || dungeon.Playfield <= 0)
                return Fail("no Dungeon.Playfield", out problem);
            if (!_dungeons.TryResolveEntrance(dungeon.Playfield, dungeon.Entrance, out MissionEntrancePlacement entrance,
                    out MissionDestinationWorldPosition? worldPosition))
                return Fail(string.Format(CultureInfo.InvariantCulture, "no mission entrance {0} on playfield {1}",
                    dungeon.Entrance?.ToString(CultureInfo.InvariantCulture) ?? "(any)", dungeon.Playfield), out problem);

            var rewardItems = new List<QuestRewardItem>(offer.RewardItems?.Count ?? 0);
            foreach (MissionRewardItem item in offer.RewardItems ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Hash)
                    || !_minter.TryRollIds(item.Hash, Math.Max(1, item.Quality), out int lowId, out int highId, out int quality))
                    return Fail("reward item " + (item.Hash ?? "(none)") + " does not resolve", out problem);
                rewardItems.Add(new QuestRewardItem { Hash = item.Hash, Id = lowId, HighId = highId, Quality = quality });
            }

            var type = (MissionRollType)(int)offer.Kind;
            int iconId = offer.IconId is int icon and > 0 ? icon : DungeonMissionTypes.Icon(type);
            QuestTemplate mission = Template(offer, entrance.IdentityInstance, rewardItems, iconId);
            if (mission.Dungeon!.Problem(mission.Action) is string dungeonProblem)
                return Fail(dungeonProblem, out problem);

            double lifetimeHours = offer.LifetimeHours ?? Math.Max(1, _dungeons.AcceptedLifetimeSeconds / 3600.0);
            frozen = new FrozenOffer(WithEntrance(offer, entrance.IdentityInstance), mission, type, rewardItems, entrance, worldPosition,
                lifetimeHours);
            return true;
        }

        /// <summary>The log name of an offer's quest: the module and its key.</summary>
        string ModuleKey(MissionOffer offer) => (_modules.MissionRollHookOwner ?? "module") + ":" + offer.Key;

        /// <summary>The generated quest an offer gives: a Quests.json-shaped quest with its dungeon and its own reward.</summary>
        QuestTemplate Template(MissionOffer offer, uint entrance, List<QuestRewardItem> rewardItems, int iconId)
        {
            MissionDungeon dungeon = offer.Dungeon;
            MissionObjective objective = offer.Objective ?? new MissionObjective();
            string title = offer.Title.Trim();
            return new QuestTemplate
            {
                Hash = ModuleKey(offer),
                Name = title,
                Title = title,
                Description = offer.Description ?? string.Empty,
                IconId = iconId,
                Scope = offer.Team ? "Team" : "Solo",
                Action = offer.Kind switch
                {
                    MissionKind.KillPerson => QuestTemplate.KillOneAction,
                    MissionKind.FindPerson => QuestTemplate.TargetNpcAction,
                    MissionKind.FindItem => QuestTemplate.FindItemAction,
                    _ => QuestTemplate.RepairAction
                },
                Objective = new QuestObjective
                {
                    Npc = Blank(objective.Npc), WorldItem = Blank(objective.Item), SpecItem = Blank(objective.Tool)
                },
                ExpiresAfterHours = offer.LifetimeHours,
                Reward = new QuestReward
                {
                    Credits = offer.Credits,
                    Xp = offer.Experience,
                    XpByLevel = offer.ExperienceByLevel is { Count: > 0 } byLevel ? byLevel.ToArray() : null,
                    Records = rewardItems.Count == 0 ? null : [rewardItems]
                },
                Dungeon = new QuestDungeonTemplate
                {
                    Floors = dungeon.MultiFloor ? QuestDungeonTemplate.MultiFloor : QuestDungeonTemplate.SingleFloor,
                    Quality = offer.Quality,
                    Style = dungeon.Style,
                    Playfield = dungeon.Playfield,
                    Entrance = entrance,
                    MobLevelPercent = dungeon.MobLevelPercent,
                    ObjectiveLevelPercent = dungeon.ObjectiveLevelPercent,
                    Npcs = dungeon.Npcs?.ToArray(),
                    AwardsTokens = dungeon.AwardsTokens,
                    Chests = dungeon.Chests == null ? null : new QuestDungeonChests
                    {
                        Enabled = dungeon.Chests.Enabled,
                        RoomDensity = dungeon.Chests.RoomDensity,
                        CountVariationFraction = dungeon.Chests.CountVariationFraction,
                        LootTable = Rows(dungeon.Chests.LootTable)
                    },
                    MobLoot = Rows(dungeon.MobLoot)
                }
            };
        }

        static List<MobItemTableEntry>? Rows(IReadOnlyList<MissionLootRow>? rows)
            => rows?.Select(row => new MobItemTableEntry
            {
                Hash = row?.Hash ?? string.Empty,
                Repeats = row?.Repeats ?? 0,
                Chance = row?.Chance ?? 0,
                LevelMod = row?.LevelMod ?? 0,
                IgnoreLevel = row?.IgnoreLevel ?? false,
                QualityPercent = row?.QualityPercent ?? 0
            }).ToList();

        /// <summary>The offer as shown, with the entrance it showed.</summary>
        static MissionOffer WithEntrance(MissionOffer offer, uint entrance) => new()
        {
            Key = offer.Key,
            Kind = offer.Kind,
            Title = offer.Title,
            Description = offer.Description,
            Quality = offer.Quality,
            Credits = offer.Credits,
            Experience = offer.Experience,
            ExperienceByLevel = offer.ExperienceByLevel,
            RewardItems = offer.RewardItems,
            IconId = offer.IconId,
            Team = offer.Team,
            LifetimeHours = offer.LifetimeHours,
            Objective = offer.Objective,
            Dungeon = new MissionDungeon
            {
                Playfield = offer.Dungeon.Playfield,
                Entrance = entrance,
                MultiFloor = offer.Dungeon.MultiFloor,
                Style = offer.Dungeon.Style,
                MobLevelPercent = offer.Dungeon.MobLevelPercent,
                ObjectiveLevelPercent = offer.Dungeon.ObjectiveLevelPercent,
                Npcs = offer.Dungeon.Npcs,
                AwardsTokens = offer.Dungeon.AwardsTokens,
                Chests = offer.Dungeon.Chests,
                MobLoot = offer.Dungeon.MobLoot
            }
        };

        int NextInstance()
        {
            int instance;
            do
                instance = Interlocked.Increment(ref _nextInstance) & 0x7FFFFFFF;
            while (instance == 0);
            return instance;
        }

        static long RequestTime(long fallback) => ModuleRequest.Current is long received and > 0 ? received : fallback;

        static int Slider(byte value) => unchecked((sbyte)value);

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static bool Fail(string reason, out string problem)
        {
            problem = reason;
            return false;
        }

        static void Text(IZoneSession session, Player player, string text)
            => session.Send(new ChatTextMessage { Identity = player.Identity, Text = text });

        sealed class RollState
        {
            public long LastRoll;

            public Batch? Batch;
        }

        /// <summary>One roll's offers, by offer instance, and where they were made.</summary>
        sealed record Batch(int PlayfieldId, Identity Terminal, Dictionary<int, FrozenOffer> Offers);

        sealed class FrozenOffer(MissionOffer shown, QuestTemplate mission, MissionRollType type, List<QuestRewardItem> rewardItems,
            MissionEntrancePlacement entrance, MissionDestinationWorldPosition? worldPosition, double lifetimeHours)
        {
            public MissionOffer Shown { get; } = shown;

            public QuestTemplate Mission { get; } = mission;

            public MissionRollType Type { get; } = type;

            public List<QuestRewardItem> RewardItems { get; } = rewardItems;

            public MissionEntrancePlacement Entrance { get; } = entrance;

            public MissionDestinationWorldPosition? WorldPosition { get; } = worldPosition;

            public double LifetimeHours { get; } = lifetimeHours;

            public DateTime ExpiresUtc { get; set; } = DateTime.MaxValue;

            /// <summary>An accept is running: a second accept of the same offer is ignored until it ends.</summary>
            public bool Accepting { get; set; }
        }
    }
}
