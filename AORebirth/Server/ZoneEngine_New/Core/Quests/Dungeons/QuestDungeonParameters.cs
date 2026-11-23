namespace ZoneEngine_New.Core.Quests.Dungeons
{
    using System.Text.Json;

    using LostEden.Protocol.GameData;

    /// <summary>
    /// What a quest stores to recreate its dungeon (generatedquests.AcgBuildingGeneratorJson): the layout seed and
    /// generator version and (from version 3) frozen generation choices, plus the entrance it is reached through.
    /// </summary>
    public sealed class QuestDungeonParameters
    {
        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public int Seed { get; set; }

        public int GeneratorVersion { get; set; }

        /// <summary>The GameData Missions/Missions.json mission it was accepted as; empty on quests stored before it was kept.</summary>
        public string Mission { get; set; } = string.Empty;

        public DungeonLayoutPlan? LayoutPlan { get; set; }

        /// <summary>The exact Find Item instance and placement selected before acceptance commits.</summary>
        public MissionItemObjectiveBinding? ObjectiveItem { get; set; }

        public MissionItemObjectiveBinding? ObjectiveTool { get; set; }

        public MissionNpcObjectiveBinding? ObjectiveNpc { get; set; }

        /// <summary>Absent on missions accepted before token rewards were enabled, and on quests that pay none.</summary>
        public MissionTokenRewardPolicy? TokenReward { get; set; }

        /// <summary>The mission's credits and experience split at acceptance (<see cref="MissionDefinition.Rewards"/>); null: none.</summary>
        public MissionRewardSplit? Rewards { get; set; }

        /// <summary>
        /// The dungeon request resolved to its full values at acceptance (<see cref="DungeonPresets.Resolve"/>): layout,
        /// population, chests and objective, so the dungeon rebuilds the same whatever the presets become later.
        /// </summary>
        public ResolvedDungeon? Dungeon { get; set; }

        public int OriginTerminalType { get; set; }
        public int OriginTerminalInstance { get; set; }
        public int OriginPlayfield { get; set; }
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float OriginZ { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public Identity OriginTerminal => new() { Type = (IdentityType)OriginTerminalType, Instance = OriginTerminalInstance };

        public bool IsTeam { get; set; }

        /// <summary>
        /// A team mission's roller (its owner) and the team (their own group in a raid) as it was when they rolled it:
        /// only these characters can be given it, at the roll or later through "Update team members". Absent (0, null)
        /// on every other quest.
        /// </summary>
        public int TeamOwnerId { get; set; }

        public int[]? TeamRoster { get; set; }

        /// <summary>The character holds this mission as a teammate's copy (journal flag 0x200), not as its roller.</summary>
        public bool IsTeamCopyFor(int characterId) => TeamOwnerId > 0 && TeamOwnerId != characterId;

        /// <summary>Playfield2 id of this quest's dungeon (<see cref="QuestDungeonIds"/>).</summary>
        public int DungeonPlayfield { get; set; }

        /// <summary>Defaults to the implicit MissionEntrance type used by older stored dungeon parameters.</summary>
        public int EntranceType { get; set; } = (int)IdentityType.ACGEntrance;

        /// <summary>MissionEntrance (0xDAC6) instance bits, stored in the protocol's signed integer representation.</summary>
        public int EntranceInstance { get; set; }

        public int EntrancePlayfield { get; set; }

        /// <summary>The entrance's name; the key is "Mission key to" this.</summary>
        public string EntranceName { get; set; } = string.Empty;

        public float EntranceX { get; set; }

        public float EntranceY { get; set; }

        public float EntranceZ { get; set; }

        /// <summary>Identity type of the entrance playfield in the offered quest action (journal waypoint).</summary>
        public int DestinationType { get; set; }

        /// <summary>WorldPos X/Z offsets carried by the offer; historical names retained for stored JSON.</summary>
        public int BuildingLowId { get; set; }

        public int BuildingHighId { get; set; }

        /// <summary>Terminal roll type: 0 kill, 1 find person, 2 find item, 3 repair, 4 find and return.</summary>
        public int MissionType { get; set; }

        public int MissionIconId { get; set; }

        /// <summary>The accepted offer's quality level; locked doors use it as their lock difficulty. 0 on older quests.</summary>
        public int Quality { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static bool TryParse(string? json, out QuestDungeonParameters parameters)
        {
            parameters = null!;
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                QuestDungeonParameters? parsed = JsonSerializer.Deserialize<QuestDungeonParameters>(json, JsonOptions);
                // Only the current version: missions stored before the dungeon request existed are not rebuilt.
                if (parsed == null || parsed.GeneratorVersion != DungeonLayoutGenerator.CurrentVersion
                    || parsed.Dungeon == null || parsed.Dungeon.Problem() != null || parsed.LayoutPlan?.IsValid != true
                    || parsed.IsTeam != parsed.LayoutPlan.BossFloor
                    || !QuestDungeonIds.IsQuestDungeonPlayfield(parsed.DungeonPlayfield)
                    || parsed.EntranceType != (int)IdentityType.ACGEntrance
                    || (parsed.ObjectiveItem != null && !parsed.ObjectiveItem.IsValid)
                    || (parsed.ObjectiveTool != null && (!parsed.ObjectiveTool.IsValid || !parsed.ObjectiveTool.IsSourceItem))
                    || (parsed.ObjectiveNpc != null && !parsed.ObjectiveNpc.IsValid)
                    || (parsed.TokenReward != null && !parsed.TokenReward.IsValid)
                    || (parsed.Rewards != null && !parsed.Rewards.IsValid)
                    || (parsed.ObjectiveItem != null && parsed.ObjectiveItem.MissionType != parsed.MissionType)
                    || (parsed.ObjectiveTool != null && parsed.MissionType != 3)
                    || (parsed.ObjectiveNpc != null && parsed.ObjectiveNpc.MissionType != parsed.MissionType))
                    return false;

                parameters = parsed;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
