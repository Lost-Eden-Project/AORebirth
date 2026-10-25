namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using SmokeLounge.AOtomation.Messaging.GameData;

    /// <summary>Editable, frozen appearance fields from a positioned ChestFullUpdate; never a loot definition.</summary>
    public sealed class ChestSpawnDefinition
    {
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
