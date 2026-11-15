namespace ZoneEngine_New.Core.Quests;

using System;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    internal MissionChestPopulation? CreateChestPopulation(int ownerId, string questId, long assigned,
        QuestDungeonParameters parameters)
    {
        // A mission whose frozen chest block places none (disabled, density 0 or nothing to hold) has no chest
        // population, so arrival never runs placement, loot rolls or stored-chest reads.
        if (parameters.Dungeon?.Chests is not { Places: true })
            return null;
        if (_completionDao == null)
            throw new InvalidOperationException("Mission chests require the existing mission DAO.");
        return new MissionChestPopulation(_completionDao, ownerId, questId, assigned, parameters, _items, _logger);
    }
}
