namespace ZoneEngine_New.Core.Quests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using AORebirth.Interfaces.Persistence.Missions;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// Sided mission tokens for dungeon quests with AwardsTokens: the dungeon counts its kills in memory
/// (<see cref="DungeonKills"/>), checkpoints them as one mission flag when it unloads, and the quest's completion
/// transaction draws once with chance killed/total and keeps the draw as one more flag so a reward retry cannot redraw.
/// </summary>
public sealed partial class QuestService
{
    /// <summary>One completed quest's token draw.</summary>
    internal sealed record TokenRoll(int Killed, int Total, bool Won, int ItemId, int Quality)
    {
        internal string Serialize()
            => string.Join(":", Killed.ToString(CultureInfo.InvariantCulture), Total.ToString(CultureInfo.InvariantCulture),
                Won ? "1" : "0", ItemId.ToString(CultureInfo.InvariantCulture), Quality.ToString(CultureInfo.InvariantCulture));

        internal static TokenRoll? Parse(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            string[] parts = value.Split(':');
            if (parts.Length != 5 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int killed)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int total)
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int itemId)
                || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int quality))
                return null;
            return new TokenRoll(killed, total, parts[2] == "1", itemId, quality);
        }
    }

    static string TokenRewardFlag(long assigned) => "token-reward:" + assigned.ToString(CultureInfo.InvariantCulture);
    static string KillsFlag(long assigned) => "kills:" + assigned.ToString(CultureInfo.InvariantCulture);

    /// <summary>The dungeon's kill ledger, from its checkpoint when one was written; an untouched population otherwise.</summary>
    internal DungeonKills LoadDungeonKills(int ownerId, string questId, long assignedAtTicks, QuestDungeonParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string? checkpoint = null;
        if (_completionDao != null)
        {
            try
            {
                checkpoint = _completionDao.Execute(ownerId, tx => tx.GetFlag(new MissionKeyData(ownerId, questId), KillsFlag(assignedAtTicks))?.Value);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Dungeon kill checkpoint could not be read; the population starts alive quest=" + questId);
            }
        }
        return new DungeonKills(ownerId, questId, assignedAtTicks, parameters.TokenReward,
            hasObjective: parameters.ObjectiveNpc != null, objectiveCounts: parameters.MissionType == 0, checkpoint);
    }

    /// <summary>Writes the ledger's checkpoint when it changed since the last one. Runs when a dungeon unloads.</summary>
    internal void SaveDungeonKills(DungeonKills kills)
    {
        ArgumentNullException.ThrowIfNull(kills);
        if (_completionDao == null || !kills.Dirty)
            return;
        try
        {
            long now = _now().Ticks;
            _completionDao.Execute(kills.OwnerId, tx =>
            {
                UpsertFlag(tx, new MissionKeyData(kills.OwnerId, kills.QuestId), KillsFlag(kills.AssignedAtTicks), kills.Checkpoint(), now);
                return true;
            });
            kills.MarkSaved();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Dungeon kill checkpoint could not be saved quest=" + kills.QuestId);
        }
    }

    /// <summary>A kill in a mission dungeon: marks the slot dead and tells the owner the token chance so far.</summary>
    internal void RecordDungeonKill(QuestDungeonPlayfield dungeon, NpcCharacter npc, IReadOnlyList<Player> players)
    {
        if (dungeon.Kills is not { } kills || !kills.TryRecordKill(npc) || kills.Policy is not { } policy)
            return;
        Player? owner = null;
        for (int i = 0; i < players.Count; i++)
            if (players[i].Identity.Instance == kills.OwnerId)
                owner = players[i];
        if (owner == null)
            return;
        (int killed, int total) = kills.Chance();
        if (total <= 0 || killed < policy.FeedbackStartingKill
            || !policy.SidedItems.ContainsKey(owner.Stats.GetOrZero(CharacterStat.Side)))
            return;
        ClientFeedback.SendFormatted(owner, policy.FeedbackMessageId, (float)(100.0 * killed / total));
    }

    static void UpsertFlag(IMissionDaoTransaction tx, MissionKeyData key, string flagKey, string value, long now)
    {
        MissionFlagData? existing = tx.GetFlag(key, flagKey);
        if (existing != null)
        {
            existing.Value = value;
            existing.UpdatedAtUtcTicks = now;
            tx.SaveFlag(key, existing);
            return;
        }
        tx.SaveFlag(key, new MissionFlagData
        {
            CharacterId = key.CharacterId, QuestId = key.QuestId, FlagKey = flagKey, Value = value,
            CreatedAtUtcTicks = now, UpdatedAtUtcTicks = now
        });
    }

    CharacterQuestCompletionResult EarnWithToken(IMissionDaoTransaction transaction,
        ICharacterQuestCompletionTransaction completion, Player player, PlayerQuest quest, CharacterQuestEarnRequest request)
    {
        var result = completion.TryEarnCharacterQuest(request);
        if (result.Status == CharacterQuestCompletionStatus.Earned)
            RollToken(transaction, player, quest, result.Quest);
        return result;
    }

    /// <summary>One draw at earning, in the earning transaction, with the final kill checkpoint beside it.</summary>
    void RollToken(IMissionDaoTransaction transaction, Player player, PlayerQuest quest, CharacterQuestCompletionSnapshotData stored)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters)
            || parameters.TokenReward is not { } policy)
            return;
        var key = new MissionKeyData(player.Identity.Instance, quest.QuestId);
        string flagKey = TokenRewardFlag(stored.AssignedAtUtcTicks);
        if (transaction.GetFlag(key, flagKey) != null)
            return;
        long now = _now().Ticks;
        int killed, total;
        DungeonKills? kills = player.Playfield is QuestDungeonPlayfield dungeon
            && string.Equals(dungeon.QuestId, quest.QuestId, StringComparison.Ordinal) ? dungeon.Kills : null;
        if (kills != null)
        {
            (killed, total) = kills.Chance();
            UpsertFlag(transaction, key, KillsFlag(stored.AssignedAtUtcTicks), kills.Checkpoint(), now);
            kills.MarkSaved();
        }
        else
        {
            // Earning away from the dungeon (a bound objective committed after relogging): the last checkpoint decides.
            (killed, total) = DungeonKills.ChanceOf(parameters, transaction.GetFlag(key, KillsFlag(stored.AssignedAtUtcTicks))?.Value);
        }
        int side = player.Stats.GetOrZero(CharacterStat.Side);
        bool sided = policy.SidedItems.TryGetValue(side, out int itemId);
        bool won = sided && total > 0 && killed > 0 && RandomNumberGenerator.GetInt32(total) < killed;
        var roll = new TokenRoll(killed, total, won, won ? itemId : 0, won ? policy.ItemQuality : 0);
        UpsertFlag(transaction, key, flagKey, roll.Serialize(), now);
    }

    static TokenRoll? ReadTokenReward(IMissionDaoTransaction transaction, Player player,
        PlayerQuest quest, CharacterQuestCompletionSnapshotData stored)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters)
            || parameters.TokenReward == null)
            return null;
        return TokenRoll.Parse(transaction.GetFlag(new MissionKeyData(player.Identity.Instance, quest.QuestId),
            TokenRewardFlag(stored.AssignedAtUtcTicks))?.Value);
    }
}
