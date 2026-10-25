namespace ZoneEngine_New.Core.Quests;

using System;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    internal MissionChestPopulation? CreateChestPopulation(int ownerId, string questId, long assigned,
        string frozenJson, QuestDungeonParameters parameters, MissionChestPolicy? policy)
    {
        if (_completionDao == null)
        {
            if (policy == null)
                return null;
            throw new InvalidOperationException("Configured mission chests require the existing mission DAO.");
        }
        return new MissionChestPopulation(_completionDao, ownerId, questId, assigned, frozenJson,
            parameters, policy, _items, _logger);
    }
}
