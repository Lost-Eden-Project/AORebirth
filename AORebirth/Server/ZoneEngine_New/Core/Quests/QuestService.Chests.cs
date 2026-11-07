namespace ZoneEngine_New.Core.Quests;

using System;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    internal MissionChestPopulation? CreateChestPopulation(int ownerId, string questId, long assigned,
        QuestDungeonParameters parameters, MissionChestPolicy? policy)
    {
        // DungeonGeneration.json Chests.Enabled=false, or a mission frozen without a chest item table: the dungeon has
        // no chest population, so arrival never runs placement, loot rolls or stored-chest reads.
        if (policy is { Enabled: false } || parameters.ChestLoot is not { Count: > 0 })
            return null;
        if (_completionDao == null)
        {
            if (policy == null)
                return null;
            throw new InvalidOperationException("Configured mission chests require the existing mission DAO.");
        }
        return new MissionChestPopulation(_completionDao, ownerId, questId, assigned, parameters, policy, _items, _logger);
    }
}
