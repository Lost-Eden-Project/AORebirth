namespace AORebirth.Modules.Missions
{
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>
    /// Decides what a mission terminal offers and what accepting an offer gives (module API 1.10). Register it with
    /// <see cref="IZoneModuleContext.RegisterMissionRollHook"/>; only one module may. Without a hook, terminals are out of
    /// service. Both methods run on the player's playfield tick thread and must return quickly: the playfield waits.
    /// A hook that throws refuses the roll or the accept (nothing is given and the offer stays).
    /// </summary>
    public interface IMissionRollHook
    {
        /// <summary>
        /// A player pressed Request Missions at a terminal. Return up to <see cref="MissionRollResult.MaxOffers"/> offers,
        /// or a refusal. The engine checks and freezes each offer (entrance, reward item ids) before it is shown; an offer
        /// it cannot use is left out with a warning in the log.
        /// </summary>
        MissionRollResult Roll(MissionRollRequest request);

        /// <summary>
        /// The player accepted one of the offers this hook made. The engine has already checked that it is that player's,
        /// still offered, and that the player stands at the terminal. The decision says what to give; the offer is used up
        /// unless the decision is <see cref="MissionAcceptKind.Refuse"/> or giving it fails.
        /// </summary>
        MissionAcceptDecision Accept(MissionAcceptRequest request);
    }

    /// <summary>The six terminal sliders, as signed percentages from -100 to 100.</summary>
    public readonly record struct MissionSliders(
        int GoodBad,
        int OrderChaos,
        int OpenHidden,
        int PhysicalMystical,
        int HeadOnStealth,
        int MoneyExperience);

    /// <summary>The inputs of one terminal roll.</summary>
    public sealed class MissionRollRequest
    {
        public int CharacterId { get; init; }

        public string Name { get; init; } = string.Empty;

        public int Level { get; init; }

        public int Profession { get; init; }

        public int Breed { get; init; }

        /// <summary>The character's Side stat (0 neutral, 1 clan, 2 omni).</summary>
        public int Side { get; init; }

        /// <summary>The character's team, or 0 when not in one.</summary>
        public int TeamId { get; init; }

        /// <summary>The team's members (character ids, the roller included); empty when not in a team.</summary>
        public IReadOnlyList<int> TeamMemberIds { get; init; } = [];

        public int PlayfieldId { get; init; }

        /// <summary>The terminal the roll was made at.</summary>
        public EntityRef Terminal { get; init; }

        public WorldPosition TerminalPosition { get; init; }

        /// <summary>The difficulty (level) slider exactly as the client sent it.</summary>
        public int Difficulty { get; init; }

        public MissionSliders Sliders { get; init; }

        /// <summary>When the roll arrived, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }
    }

    /// <summary>What a roll returns: offers, or a line of text telling the player why there are none.</summary>
    public sealed class MissionRollResult
    {
        /// <summary>Most offers one roll can show: the terminal window has five slots.</summary>
        public const int MaxOffers = 5;

        MissionRollResult(IReadOnlyList<MissionOffer> offers, string? message)
        {
            Offers = offers;
            Message = message;
        }

        public IReadOnlyList<MissionOffer> Offers { get; }

        /// <summary>A line of chat text for the player, or null. Shown instead of the window when there are no offers.</summary>
        public string? Message { get; }

        public static MissionRollResult Of(IEnumerable<MissionOffer> offers, string? message = null)
            => new(new List<MissionOffer>(offers ?? []), message);

        public static MissionRollResult Refuse(string message) => new([], message ?? string.Empty);
    }

    /// <summary>The objective a mission dungeon is built around.</summary>
    public enum MissionKind
    {
        /// <summary>Kill the objective NPC.</summary>
        KillPerson = 0,

        /// <summary>Find the objective NPC and select it.</summary>
        FindPerson = 1,

        /// <summary>Find the objective item and pick it up.</summary>
        FindItem = 2,

        /// <summary>Use the supplied tool on the objective machine.</summary>
        RepairMachine = 3
    }

    /// <summary>
    /// One mission a terminal shows, and the mission accepting it gives: a generated quest with its own ACG dungeon, the
    /// same as a Quests.json quest with a <c>Dungeon</c>. Hashes name GameData entries (NpcTemplates.json, ItemTemplates.json).
    /// </summary>
    public sealed class MissionOffer
    {
        /// <summary>The module's own id for this offer; handed back in <see cref="MissionAcceptRequest.OfferKey"/>.</summary>
        public string Key { get; init; } = string.Empty;

        public MissionKind Kind { get; init; }

        /// <summary>The offer and journal title. The client shows at most 31 characters; a longer one is cut with "...".</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>The offer text and journal description (client HTML).</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Mission quality level, 1 to 250: the dungeon's generation band and the base of its NPC levels.</summary>
        public int Quality { get; init; }

        public int Credits { get; init; }

        public int Experience { get; init; }

        /// <summary>
        /// Experience by the holder's level, in place of <see cref="Experience"/>: entry 0 is level 1, and a level past the
        /// end uses the last entry. The offer window, each holder's journal (the roller's and every teammate's on a team
        /// mission) and the payout use the holder's current level. Null: <see cref="Experience"/> for everyone.
        /// </summary>
        public IReadOnlyList<int>? ExperienceByLevel { get; init; }

        /// <summary>Items paid on completion, all of them.</summary>
        public IReadOnlyList<MissionRewardItem> RewardItems { get; init; } = [];

        /// <summary>Offer and journal icon; null takes the kind's icon from DungeonGeneration.json MissionTypes.</summary>
        public int? IconId { get; init; }

        /// <summary>A team mission: a roller in a team shares it with teammates on the same playfield.</summary>
        public bool Team { get; init; }

        /// <summary>Hours the accepted mission lasts; null takes DungeonGeneration.json's accepted lifetime.</summary>
        public int? LifetimeHours { get; init; }

        public MissionObjective Objective { get; init; } = new();

        public MissionDungeon Dungeon { get; init; } = new();
    }

    /// <summary>
    /// The objective's GameData hashes. Null takes one from DungeonGeneration.json's pools for the kind when the mission
    /// is accepted.
    /// </summary>
    public sealed class MissionObjective
    {
        /// <summary>KillPerson and FindPerson: the objective NPC's hash.</summary>
        public string? Npc { get; init; }

        /// <summary>FindItem: the item to find. RepairMachine: the machine to repair.</summary>
        public string? Item { get; init; }

        /// <summary>RepairMachine: the tool the player is handed.</summary>
        public string? Tool { get; init; }
    }

    /// <summary>The mission dungeon's inputs, as in a Quests.json quest's <c>Dungeon</c>.</summary>
    public sealed class MissionDungeon
    {
        /// <summary>Playfield of the entrance the dungeon is reached through.</summary>
        public int Playfield { get; init; }

        /// <summary>
        /// MissionEntrance instance on <see cref="Playfield"/>; null picks one at random when the offer is made. The offer
        /// shows that entrance and accepting it uses that entrance.
        /// </summary>
        public uint? Entrance { get; init; }

        /// <summary>Several floors ending in a boss floor, instead of one.</summary>
        public bool MultiFloor { get; init; }

        /// <summary>ACG building style; null picks one of the generation profile's styles.</summary>
        public int? Style { get; init; }

        /// <summary>Population level spread, 0 to 90 percent either side of the quality.</summary>
        public int MobLevelPercent { get; init; }

        /// <summary>Percent added to the quality for the objective NPC's level, -90 to 400.</summary>
        public int ObjectiveLevelPercent { get; init; }

        /// <summary>NPC hashes for the ordinary population; null takes DungeonGeneration.json's.</summary>
        public IReadOnlyList<string>? Npcs { get; init; }

        /// <summary>Whether kills pay DungeonGeneration.json's sided mission token reward.</summary>
        public bool AwardsTokens { get; init; } = true;

        /// <summary>The dungeon's chests; null: none.</summary>
        public MissionChests? Chests { get; init; }

        /// <summary>Loot rows added to every NPC that dies in the dungeon; null or empty: none.</summary>
        public IReadOnlyList<MissionLootRow>? MobLoot { get; init; }
    }

    /// <summary>A mission dungeon's chests, as in a Quests.json quest's <c>Dungeon.Chests</c>.</summary>
    public sealed class MissionChests
    {
        public bool Enabled { get; init; } = true;

        /// <summary>Chests per eligible room; null keeps the engine default.</summary>
        public double? RoomDensity { get; init; }

        /// <summary>How far the chest count varies either way, 0 to 1; null keeps the engine default.</summary>
        public double? CountVariationFraction { get; init; }

        /// <summary>What the chests hold, rolled at the mission quality. Null or empty: no chests.</summary>
        public IReadOnlyList<MissionLootRow>? LootTable { get; init; }
    }

    /// <summary>One NPC-style loot table row (NpcTemplates.json loot rows).</summary>
    public sealed class MissionLootRow
    {
        public string Hash { get; init; } = string.Empty;

        public int Repeats { get; init; } = 1;

        /// <summary>Percent, 1 to 100.</summary>
        public int Chance { get; init; } = 100;

        public int LevelMod { get; init; }

        public bool IgnoreLevel { get; init; }

        /// <summary>Percent of the mission quality the row's quality centres on, 1 to 1000.</summary>
        public int QualityPercent { get; init; } = 100;
    }

    /// <summary>A reward item, by ItemTemplates.json hash and quality.</summary>
    public readonly record struct MissionRewardItem(string Hash, int Quality);

    /// <summary>A player accepted an offer.</summary>
    public sealed class MissionAcceptRequest
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        public EntityRef Terminal { get; init; }

        public string OfferKey { get; init; } = string.Empty;

        /// <summary>The offer as it was shown, with <see cref="MissionDungeon.Entrance"/> set to the entrance it showed.</summary>
        public MissionOffer Offer { get; init; } = new();

        /// <summary>When the accept arrived, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }
    }

    public enum MissionAcceptKind
    {
        /// <summary>Give the offered mission as it was shown.</summary>
        GiveOffered,

        /// <summary>Give <see cref="MissionAcceptDecision.Mission"/> instead.</summary>
        Give,

        /// <summary>Give the Quests.json quest <see cref="MissionAcceptDecision.QuestHash"/>.</summary>
        GiveQuest,

        /// <summary>The module handled it itself; the engine gives nothing.</summary>
        Handled,

        /// <summary>Nothing is given and the offer stays.</summary>
        Refuse
    }

    /// <summary>What accepting an offer does. <see cref="Message"/>, when set, is shown to the player as chat text.</summary>
    public sealed class MissionAcceptDecision
    {
        MissionAcceptDecision(MissionAcceptKind kind, string? message, MissionOffer? mission = null, string? questHash = null,
            int? questQuality = null)
        {
            Kind = kind;
            Message = message;
            Mission = mission;
            QuestHash = questHash;
            QuestQuality = questQuality;
        }

        public MissionAcceptKind Kind { get; }

        public string? Message { get; }

        public MissionOffer? Mission { get; }

        public string? QuestHash { get; }

        /// <summary>The mission quality for a Quests.json dungeon quest; null takes the quest's own.</summary>
        public int? QuestQuality { get; }

        public static MissionAcceptDecision GiveOffered(string? message = null) => new(MissionAcceptKind.GiveOffered, message);

        /// <summary>
        /// Gives <paramref name="mission"/> instead of the offer. Its <see cref="MissionDungeon.Entrance"/> is used as
        /// given; null picks one at random.
        /// </summary>
        public static MissionAcceptDecision Give(MissionOffer mission, string? message = null)
            => new(MissionAcceptKind.Give, message, mission ?? throw new System.ArgumentNullException(nameof(mission)));

        public static MissionAcceptDecision GiveQuest(string questHash, int? quality = null, string? message = null)
            => new(MissionAcceptKind.GiveQuest, message, questHash: questHash ?? throw new System.ArgumentNullException(nameof(questHash)),
                questQuality: quality);

        public static MissionAcceptDecision Handled(string? message = null) => new(MissionAcceptKind.Handled, message);

        public static MissionAcceptDecision Refuse(string message) => new(MissionAcceptKind.Refuse, message ?? string.Empty);
    }
}
