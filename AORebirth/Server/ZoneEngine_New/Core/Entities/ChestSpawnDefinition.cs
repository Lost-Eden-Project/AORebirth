namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;

    /// <summary>Appearance fields of a positioned ChestFullUpdate; never a loot definition.</summary>
    public sealed class ChestSpawnDefinition
    {
        /// <summary>
        /// The generated mission chest's spawn appearance. Retail mission-20261011-004128-b8e7bf2e, ordinal 480:
        /// state machine 1000015:7, chest template 162447, header stat bytes 111 and lockable marker 15. Only the
        /// verified Locked flag is cleared from the captured Flags (0x20001841) for an ordinary unlocked chest.
        /// <paramref name="template"/> is the chest item template the client draws (the Mothership boss room's cocoon,
        /// 10000022, is the same chest with a CATMesh body); everything else is the captured mission chest's.
        /// </summary>
        public static ChestSpawnDefinition MissionChest(int template = MissionChestTemplate)
        {
            if (template <= 0)
                throw new ArgumentOutOfRangeException(nameof(template));
            return new()
            {
                StateMachineType = 1000015,
                StateMachineInstance = 7,
                HeaderStatBytes = 111,
                LockableValue = 15,
                Stats =
                [
                    new() { StatId = (int)CharacterStat.Flags, Value = 0x20001801 },
                    new() { StatId = (int)CharacterStat.StaticInstance, Value = (uint)template },
                    new() { StatId = (int)CharacterStat.ACGItemLevel, Value = 1 },
                    new() { StatId = (int)CharacterStat.ACGItemTemplateID, Value = (uint)template },
                    new() { StatId = (int)CharacterStat.ACGItemTemplateID2, Value = (uint)template },
                    new() { StatId = (int)CharacterStat.MultipleCount, Value = 1 }
                ]
            };
        }

        /// <summary>The captured mission chest's item template (mesh 43621).</summary>
        public const int MissionChestTemplate = 162447;

        public int StateMachineType { get; set; }
        public int StateMachineInstance { get; set; }
        public short HeaderStatBytes { get; set; }
        public int LockableValue { get; set; }
        public ChestSpawnStat[] Stats { get; set; } = [];

        public void Validate()
        {
            if (StateMachineType <= 0 || Stats == null || Stats.Length == 0)
                throw new InvalidOperationException("A chest requires a configured FullUpdate appearance.");

            var found = new HashSet<int>();
            foreach (ChestSpawnStat stat in Stats)
                if (stat == null || stat.StatId < 0 || !found.Add(stat.StatId))
                    throw new InvalidOperationException("Chest appearance stats must be distinct nonnegative identifiers.");

            foreach (CharacterStat required in new[] { CharacterStat.Flags, CharacterStat.StaticInstance,
                CharacterStat.ACGItemLevel, CharacterStat.ACGItemTemplateID, CharacterStat.ACGItemTemplateID2,
                CharacterStat.MultipleCount })
                if (!found.Contains((int)required))
                    throw new InvalidOperationException("Chest appearance is missing a required captured FullUpdate stat.");
        }
    }

    public sealed class ChestSpawnStat
    {
        public int StatId { get; set; }
        public uint Value { get; set; }
    }
}
