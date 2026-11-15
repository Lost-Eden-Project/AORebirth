namespace ZoneEngine_New.Core.Quests
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One quest in GameData/Quests.json format. Template quests come from that file; generated quests
    /// use the same shape and are stored with the player.
    /// </summary>
    public sealed class QuestTemplate
    {
        /// <summary>Kill the objective NPC once.</summary>
        public const string KillOneAction = "KillOne";

        /// <summary>Kill <see cref="QuestObjective.Count"/> of the objective NPC.</summary>
        public const string KillMultipleAction = "KillMultiple";

        /// <summary>Select (target) the objective NPC.</summary>
        public const string TargetNpcAction = "TargetNpc";

        public const string FindItemAction = "FindItem";
        public const string ReturnItemAction = "FindItem2";
        public const string RepairAction = "UseItemOnItem";

        /// <summary>Finished by using the <see cref="QuestObjective.SpecItem"/> item on the objective NPC; the item is kept.</summary>
        public const string UseItemOnCharacterAction = "UseItemOnCharacter";

        /// <summary>Finished by a tradeskill combine that makes the <see cref="QuestObjective.SpecItem"/> item.</summary>
        public const string AssembleAction = "Assemble";

        /// <summary>
        /// Finished by equipping the <see cref="QuestObjective.SpecItem"/> item (Install the implant: the Leg Implant
        /// worn after the Surgery Clinic opened implant swapping).
        /// </summary>
        public const string EquipAction = "Equip";

        /// <summary>
        /// Finished by using the <see cref="QuestObjective.SpecItem"/> item, from the main inventory, on a world item of the
        /// <see cref="QuestObjective.WorldItem"/> hash within use range. Neither item is taken, so every holder of the
        /// step can use their own item on the same world item (Theresa Cameron's Encrypted Datacube).
        /// </summary>
        public const string EscortItemAction = "EscortItem";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public string Hash { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Journal title (retail ShortInfo); <see cref="Name"/> when absent.</summary>
        public string? Title { get; set; }

        /// <summary>
        /// The journal text as live sends it (QuestFullUpdate LongInfo): the title, two breaks, the story and the red
        /// "Mission Objective" block, with its HTML. When empty the journal builds it from Name and Summary.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>Journal icon; the action's retail icon when absent.</summary>
        public int? IconId { get; set; }

        public int QasInstance { get; set; }

        /// <summary>"Solo" or "Team".</summary>
        public string Scope { get; set; } = "Solo";

        /// <summary>
        /// Scope "Team": a dungeon quest given to a player in a team goes to their teammates on the same playfield too,
        /// as one shared mission (one dungeon, a key each, the objective and its reward chance shared).
        /// </summary>
        [JsonIgnore]
        public bool IsTeamScope => string.Equals(Scope, "Team", StringComparison.OrdinalIgnoreCase);

        public string Action { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public QuestObjective Objective { get; set; } = new();

        /// <summary>Comma list of NoXp, NoCash, Failed, NoDelete, or "None".</summary>
        public string Flags { get; set; } = "None";

        public int? MissionBit { get; set; }

        /// <summary>Single follow-up quest hash, assigned when this one completes.</summary>
        public string? NextStep { get; set; }

        /// <summary>
        /// Hours a template quest stays active after it is assigned, then it expires like a generated one, with the journal
        /// timer: the Perk-Reset Service Provider's 48 hour Full Perk Points Reset Service, or live's 18 hour DOJA cooldown
        /// mission ("You can only turn in one DOJA chip today.", 64800 s) that the chips' HasActiveQuest refuses on while
        /// it runs. Null or 0: never expires. Not read for generated quests, which carry their own expiry.
        /// </summary>
        public int? ExpiresAfterHours { get; set; }

        /// <summary>When a template quest assigned at <paramref name="assignedAtUtc"/> expires; null when it never does.</summary>
        public DateTime? ExpiresAt(DateTime assignedAtUtc)
            => ExpiresAfterHours is int hours and > 0 ? assignedAtUtc.AddHours(hours) : null;

        public QuestReward? Reward { get; set; }

        /// <summary>
        /// Items the player is handed when this step is assigned, however it starts (a previous step, Knubot
        /// <c>{givequest}</c>, <c>.quests</c>): live, Surveillance Uplink, the Rebuilt HC-12 SecTec Monitor arrives with the
        /// step. Not part of any reward box.
        /// </summary>
        public List<QuestRewardItem>? StartItems { get; set; }

        /// <summary>
        /// The journal compass waypoint live sends on the quest action: playfield and a target or position (y is 0). The
        /// action's protocol values come from <see cref="Action"/> (QuestJournal), never from the content.
        /// </summary>
        public QuestWaypoint? Waypoint { get; set; }

        /// <summary>
        /// The compass point as dumped from live's journal ([{Playfield, X, Y, Z}]). <see cref="QuestCatalog"/> folds the
        /// first into <see cref="Waypoint"/> when none is written, so an action with a journal layout gets its point.
        /// </summary>
        public List<QuestDumpWaypoint>? Waypoints { get; set; }

        /// <summary>
        /// Quests.json only: the quest runs in its own ACG mission dungeon. Each time it is given, the quest is copied
        /// into a generated quest and these inputs are frozen into that copy's dungeon (with a mission key).
        /// </summary>
        public QuestDungeonTemplate? Dungeon { get; set; }

        /// <summary>
        /// On a generated quest copied from a Quests.json quest (<see cref="Dungeon"/>): that quest's hash. Knubot and
        /// <c>{givequest}</c> find the copy by it. Null on terminal missions.
        /// </summary>
        public string? TemplateHash { get; set; }

        /// <summary>What did not resolve when Quests.json loaded (an item hash); null when nothing. The quest still runs.</summary>
        [JsonIgnore]
        public string? LoadProblem { get; set; }

        [JsonIgnore]
        public bool IsKill => string.Equals(Action, KillOneAction, StringComparison.Ordinal)
            || string.Equals(Action, KillMultipleAction, StringComparison.Ordinal);

        [JsonIgnore]
        public bool IsFindItem => string.Equals(Action, FindItemAction, StringComparison.Ordinal);

        [JsonIgnore]
        public bool IsReturnItem => string.Equals(Action, ReturnItemAction, StringComparison.Ordinal);

        [JsonIgnore]
        public bool IsRepair => string.Equals(Action, RepairAction, StringComparison.Ordinal);

        /// <summary>Flags "NoDelete": the player cannot remove it from the journal.</summary>
        [JsonIgnore]
        public bool IsNoDelete => Flags.Contains("NoDelete", StringComparison.Ordinal);

        /// <summary>Kills needed: Count for KillMultiple, otherwise one.</summary>
        [JsonIgnore]
        public int RequiredCount => string.Equals(Action, KillMultipleAction, StringComparison.Ordinal)
            ? Math.Max(1, Objective.Count ?? 1)
            : IsReturnItem ? 2 : 1;
    }

    public sealed class QuestObjective
    {
        /// <summary>Only KillMultiple: how many to kill.</summary>
        public int? Count { get; set; }

        /// <summary>NPC META (spawn) hash from the quest dump.</summary>
        public string? Npc { get; set; }

        /// <summary>
        /// Percent chance that each counted kill finishes the step early (Find a Bio Analyzing Computer: live it was
        /// found on the 2nd of up to 7 robots); <see cref="Count"/> kills always finish it.
        /// </summary>
        public int? FindChance { get; set; }

        /// <summary>
        /// The quest's specific item, as an item hash (ItemTemplates.json). For Assemble it is the item a tradeskill
        /// combine must make to finish the step; for Equip the item to wear.
        /// </summary>
        public string? SpecItem { get; set; }

        public string? WorldItem { get; set; }
    }

    /// <summary>
    /// Live's quest action waypoint (QuestActionInfo PlayfieldId and Position): the compass points at the
    /// <see cref="Target"/>'s position, or at <see cref="Position"/>, while the player is in <see cref="Playfield"/>.
    /// </summary>
    public sealed class QuestWaypoint
    {
        public int Playfield { get; set; }

        /// <summary>
        /// What the compass points at, looked up when the journal is sent: an NPC hash (the NPC standing on the playfield,
        /// else its Spawns.json record) or an ItemTemplates.json hash of a statel placed by Dynels.dat. When it does
        /// not resolve, <see cref="Position"/> is used.
        /// </summary>
        public string? Target { get; set; }

        /// <summary>[x, y, z]; live sends y as 0. The point itself, or the fallback for <see cref="Target"/>.</summary>
        public float[] Position { get; set; } = [];

    }

    /// <summary>
    /// A quest's dungeon (<see cref="QuestTemplate.Dungeon"/>): the mission it is (a GameData/Missions/Missions.json
    /// definition: entrances, lifetime, token reward and the dungeon) and its quality. The mission type follows the
    /// quest's Action.
    /// </summary>
    public sealed class QuestDungeonTemplate
    {
        /// <summary>The mission definition's name.</summary>
        public string Mission { get; set; } = string.Empty;

        /// <summary>
        /// Fixed mission quality level (lock difficulty, the base of the monster and objective levels). The dialog that
        /// gives the quest may set it instead (<c>{givequest HASH 150}</c>, <c>{givequest HASH level+12%}</c>); with
        /// neither, the player's level.
        /// </summary>
        public int? Quality { get; set; }

        /// <summary>The entrance's playfield, as a terminal offer showed it; 0: one of the mission's.</summary>
        public int Playfield { get; set; }

        /// <summary>The MissionEntrance instance, as a terminal offer showed it; null: the mission's, else a random one.</summary>
        public uint? Entrance { get; set; }

        /// <summary>The terminal sliders a rolled mission came with; the mission's slider rules apply them. Null: none.</summary>
        public Dungeons.MissionSliderSettings? Sliders { get; set; }

        /// <summary>Highest mission quality: the chests, items and monster levels go no further.</summary>
        public const int MaxMissionQuality = 250;

        /// <summary>
        /// The mission quality: what the giving dialog <paramref name="requested"/>, else the fixed <see cref="Quality"/>,
        /// else the player's level, at most <see cref="MaxMissionQuality"/>.
        /// </summary>
        public int QualityFor(int? requested, int playerLevel)
            => Math.Clamp(requested ?? Quality ?? playerLevel, 1, MaxMissionQuality);

        /// <summary>The terminal mission type for an Action a dungeon can run, else null.</summary>
        public static int? MissionTypeFor(string? action) => action switch
        {
            QuestTemplate.KillOneAction => 0,
            QuestTemplate.TargetNpcAction => 1,
            QuestTemplate.FindItemAction => 2,
            QuestTemplate.RepairAction => 3,
            _ => null
        };

        /// <summary>Why these inputs cannot make a dungeon for <paramref name="action"/>, or null (the mission resolves at acceptance).</summary>
        public string? Problem(string? action)
        {
            if (MissionTypeFor(action) == null)
                return "Dungeon needs Action KillOne, TargetNpc, FindItem or UseItemOnItem, not " + (action ?? "(none)");
            if (string.IsNullOrWhiteSpace(Mission))
                return "Dungeon.Mission is required";
            if (Playfield < 0)
                return "Dungeon.Playfield cannot be negative";
            if (Quality is <= 0)
                return "Dungeon.Quality must be positive";
            return null;
        }
    }

    /// <summary>One dumped compass point (<see cref="QuestTemplate.Waypoints"/>).</summary>
    public sealed class QuestDumpWaypoint
    {
        public int Playfield { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }
    }

    public sealed class QuestReward
    {
        public string Hash { get; set; } = string.Empty;

        public int Credits { get; set; }

        public int Xp { get; set; }

        /// <summary>
        /// XP by the holder's level in place of <see cref="Xp"/> (a mission roll module's): entry 0 is level 1, and a level
        /// past the end uses the last entry. Null: <see cref="Xp"/> for everyone.
        /// </summary>
        public int[]? XpByLevel { get; set; }

        /// <summary>The XP a holder at <paramref name="level"/> is shown and paid.</summary>
        public int XpFor(int level)
            => Math.Max(0, XpByLevel is { Length: > 0 } table ? table[Math.Clamp(level, 1, table.Length) - 1] : Xp);

        /// <summary>Alternative item sets; one record is given, all its items together.</summary>
        public List<List<QuestRewardItem>>? Records { get; set; }
    }

    /// <summary>
    /// An item a quest hands over. Quests.json names it by <see cref="Hash"/> (ItemTemplates.json) and
    /// <see cref="Quality"/>; <see cref="QuestCatalog"/> resolves that to <see cref="Id"/> and <see cref="HighId"/> at load.
    /// Generated quests store the ids themselves.
    /// </summary>
    public sealed class QuestRewardItem
    {
        public string? Hash { get; set; }

        public int Id { get; set; }

        public int? HighId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Quality { get; set; } = 1;

        /// <summary>The stack size to deliver (QuestRewards.json <c>Count</c>), or null for the item's own mint count.</summary>
        public int? Count { get; set; }
    }
}
