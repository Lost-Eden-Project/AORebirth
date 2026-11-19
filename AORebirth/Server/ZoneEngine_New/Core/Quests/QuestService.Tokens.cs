namespace ZoneEngine_New.Core.Quests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using AORebirth.Interfaces.Persistence.Missions;
using LostEden.Protocol.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// The reward roll for dungeon quests with XP, items or a token behind it: the dungeon counts its kills in memory
/// (<see cref="DungeonKills"/>), checkpoints them as one mission flag when it unloads, and the quest's completion
/// transaction draws once with chance killed/total and keeps the draw as one more flag so a reward retry cannot redraw.
/// A won draw pays the quest's XP and items, plus a sided token for Clan and Omni; a lost one pays only the credits.
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
    static string KillCreditFlag(long assigned) => "kill-credit:" + assigned.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Loads <paramref name="playerId"/>'s saved kill credit into the ledger (one read per player per dungeon load).
    /// False when it could not be read: that kill credits them nothing, and the next one reads again, so a stored credit
    /// is never overwritten by an empty one.
    /// </summary>
    bool LoadCredit(DungeonKills kills, int playerId)
    {
        string? checkpoint = null;
        if (_completionDao != null)
        {
            try
            {
                checkpoint = _completionDao.Execute(playerId, tx =>
                    tx.GetFlag(new MissionKeyData(playerId, kills.QuestId), KillCreditFlag(kills.AssignedAtTicks))?.Value);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Kill credit could not be read; this kill is not credited char=" + playerId + " quest=" + kills.QuestId);
                return false;
            }
        }
        kills.LoadCredit(playerId, checkpoint);
        return true;
    }

    /// <summary>The same, inside <paramref name="player"/>'s own transaction.</summary>
    static void LoadCredit(IMissionDaoTransaction transaction, DungeonKills kills, MissionKeyData key)
    {
        if (!kills.HasCredit(key.CharacterId))
            kills.LoadCredit(key.CharacterId, transaction.GetFlag(key, KillCreditFlag(kills.AssignedAtTicks))?.Value);
    }

    /// <summary>
    /// A player's own kill share away from the dungeon: their saved credit, or on a mission checkpointed before credit
    /// was per player, the owner's old team-wide checkpoint.
    /// </summary>
    static (int Killed, int Total) SavedCredit(IMissionDaoTransaction transaction, MissionKeyData key,
        QuestDungeonParameters parameters, long assigned)
    {
        string? credit = transaction.GetFlag(key, KillCreditFlag(assigned))?.Value;
        if (string.IsNullOrEmpty(credit))
        {
            string? population = transaction.GetFlag(key, KillsFlag(assigned))?.Value;
            if (DungeonKills.IsLegacyCheckpoint(population))
                credit = population;
        }
        return DungeonKills.ChanceOf(parameters, credit);
    }

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

    /// <summary>
    /// Writes the ledger's checkpoint, and each player's changed kill credit under their own character, when they changed
    /// since the last save. Runs when a dungeon unloads.
    /// </summary>
    internal void SaveDungeonKills(DungeonKills kills)
    {
        ArgumentNullException.ThrowIfNull(kills);
        if (_completionDao == null)
            return;
        long now = _now().Ticks;
        if (kills.Dirty)
        {
            try
            {
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
        foreach (int playerId in kills.DirtyCredits.ToArray())
        {
            try
            {
                _completionDao.Execute(playerId, tx =>
                {
                    UpsertFlag(tx, new MissionKeyData(playerId, kills.QuestId), KillCreditFlag(kills.AssignedAtTicks),
                        kills.CreditCheckpoint(playerId), now);
                    return true;
                });
                kills.MarkCreditSaved(playerId);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Kill credit could not be saved char=" + playerId + " quest=" + kills.QuestId);
            }
        }
    }

    /// <summary>
    /// A kill in a mission dungeon: marks the slot dead and credits it to the players with kill credit for it (the
    /// killer and their nearby teammates, <paramref name="players"/>) who hold the mission. Each of them is told their
    /// own reward chance so far, whatever their side. A teammate outside the dungeon, or not near the kill, gets neither
    /// the credit nor the line. The line comes from the chat server as an anonymous vicinity message (yellow, as "All
    /// team members can loot these remains."); zone Feedback when the chat server cannot be reached.
    /// </summary>
    internal void RecordDungeonKill(QuestDungeonPlayfield dungeon, NpcCharacter npc, IReadOnlyList<Player> players)
    {
        if (dungeon.Kills is not { } kills)
            return;
        var credited = new List<(Player Player, PlayerQuest Quest)>(players.Count);
        var creditedIds = new List<int>(players.Count);
        foreach (Player player in players)
        {
            if (!ReferenceEquals(player.Playfield, dungeon)
                || !GetLog(player).Quests.TryGetValue(kills.QuestId, out PlayerQuest? held))
                continue;
            int id = player.Identity.Instance;
            if (!kills.HasCredit(id) && !LoadCredit(kills, id))
                continue;
            credited.Add((player, held));
            creditedIds.Add(id);
        }
        if (!kills.TryRecordKill(npc, creditedIds))
        {
            LogRewardChance(dungeon, npc, kills, "not-population");
            return;
        }
        if (kills.Policy is not { } policy)
        {
            LogRewardChance(dungeon, npc, kills, "no-reward-policy");
            return;
        }

        Chat.VicinityChatRelay? relay = dungeon.GetService<Chat.VicinityChatRelay>();
        int sent = 0;
        foreach ((Player player, PlayerQuest quest) in credited)
        {
            if (!quest.IsActive || !HasGatedReward(player, quest, policy))
                continue;
            (int killed, int total) = kills.Chance(player.Identity.Instance);
            if (total <= 0 || killed <= 0 || policy.FormatFeedback((float)(100.0 * killed / total)) is not { } text)
                continue;
            if (relay?.Announce(text, [player.Identity.Instance]) != true)
                ClientFeedback.SendFormatted(player, ClientFeedback.PlainText, text);
            sent++;
        }
        LogRewardChance(dungeon, npc, kills, "credited=" + credited.Count + " sent=" + sent);
    }

    void LogRewardChance(QuestDungeonPlayfield dungeon, NpcCharacter npc, DungeonKills kills, string outcome)
    {
        (int killed, int total) = kills.Chance();
        _logger.Info("Mission reward chance pf=" + dungeon.Identity.Instance + " quest=" + kills.QuestId + " npc=" + npc.Name
            + " kills=" + killed + "/" + total + " outcome=" + outcome);
    }

    /// <summary>
    /// True when the reward roll has something to decide for <paramref name="player"/>: the quest's XP or items, or a
    /// token for their side. With nothing behind it the dungeon does not roll and sends no chance line. Items count by
    /// configuration, not by resolving them: a reward item that no longer resolves neither throws on the kill tick nor
    /// skips the roll (which would pay the reward ungated once the data is fixed); its delivery stays pending instead.
    /// </summary>
    bool HasGatedReward(Player player, PlayerQuest quest, MissionTokenRewardPolicy policy)
        => (quest.Template.Reward?.XpFor(player.Stats.GetOrOne(CharacterStat.Level)) ?? 0) > 0
            || HasRewardItems(player, quest)
            || (policy.AwardsTokens && policy.SidedItems.ContainsKey(player.Stats.GetOrZero(CharacterStat.Side)));

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
        ICharacterQuestCompletionTransaction completion, Player player, PlayerQuest quest, CharacterQuestEarnRequest request,
        (int Killed, int Total)? sharedChance = null)
    {
        var result = completion.TryEarnCharacterQuest(request);
        if (result.Status == CharacterQuestCompletionStatus.Earned)
            RollToken(transaction, player, quest, result.Quest, sharedChance);
        return result;
    }

    /// <summary>
    /// One draw at earning, in the earning transaction, with the final kill checkpoint beside it. Each holder draws
    /// once with their own kill credit: in the dungeon the ledger's credit for them decides; away from it, a chance
    /// carried in (<paramref name="sharedChance"/>), else their saved credit.
    /// </summary>
    void RollToken(IMissionDaoTransaction transaction, Player player, PlayerQuest quest, CharacterQuestCompletionSnapshotData stored,
        (int Killed, int Total)? sharedChance = null)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters)
            || parameters.TokenReward is not { } policy || !HasGatedReward(player, quest, policy))
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
            LoadCredit(transaction, kills, key);
            (killed, total) = kills.Chance(player.Identity.Instance);
            // Written with the roll; left dirty, so the unload writes it again if this transaction does not commit.
            UpsertFlag(transaction, key, KillCreditFlag(stored.AssignedAtUtcTicks), kills.CreditCheckpoint(player.Identity.Instance), now);
            // The ledger's checkpoint lives under its owner (the dungeon reloads it from there); a teammate only reads it.
            if (kills.OwnerId == player.Identity.Instance)
            {
                UpsertFlag(transaction, key, KillsFlag(stored.AssignedAtUtcTicks), kills.Checkpoint(), now);
                kills.MarkSaved();
            }
        }
        else if (sharedChance is { } shared)
        {
            (killed, total) = shared;
        }
        else
        {
            // Earning away from the dungeon (a bound objective committed after relogging): their saved credit decides.
            (killed, total) = SavedCredit(transaction, key, parameters, stored.AssignedAtUtcTicks);
        }
        // Every side rolls for the reward; only Clan and Omni, on a token-awarding quest, have a token to add to a won roll.
        int side = player.Stats.GetOrZero(CharacterStat.Side);
        int itemId = 0;
        bool sided = policy.AwardsTokens && policy.SidedItems.TryGetValue(side, out itemId);
        bool won = total > 0 && killed > 0 && RandomNumberGenerator.GetInt32(total) < killed;
        bool token = won && sided;
        var roll = new TokenRoll(killed, total, won, token ? itemId : 0, token ? policy.ItemQuality : 0);
        UpsertFlag(transaction, key, flagKey, roll.Serialize(), now);
    }

    /// <summary>
    /// A generated mission's quality and kill share at completion, with the share of its advertised credits and experience
    /// it pays (its frozen <see cref="MissionRewardSplit"/>; null for a mission without a split, which pays as before).
    /// Null for a quest that is not a generated mission.
    /// </summary>
    internal sealed record MissionShare(int Quality, int Killed, int Total, double? Factor, string Mission);

    MissionShare? ShareFor(IMissionDaoTransaction transaction, Player player, PlayerQuest quest,
        CharacterQuestCompletionSnapshotData stored, TokenRoll? roll)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters))
            return null;
        (int killed, int total) = KillShare(transaction, player, quest, stored, parameters, roll);
        return new MissionShare(parameters.Quality, killed, total, parameters.Rewards?.Factor(killed, total), parameters.Mission);
    }

    /// <summary>
    /// The player's own kill share: the reward roll's when there is one, else their credit in the dungeon's ledger or
    /// their saved credit.
    /// </summary>
    (int Killed, int Total) KillShare(IMissionDaoTransaction transaction, Player player, PlayerQuest quest,
        CharacterQuestCompletionSnapshotData stored, QuestDungeonParameters parameters, TokenRoll? roll)
    {
        if (roll != null)
            return (roll.Killed, roll.Total);
        var key = new MissionKeyData(player.Identity.Instance, quest.QuestId);
        if (player.Playfield is QuestDungeonPlayfield dungeon
            && string.Equals(dungeon.QuestId, quest.QuestId, StringComparison.Ordinal) && dungeon.Kills is DungeonKills kills)
        {
            LoadCredit(transaction, kills, key);
            return kills.Chance(player.Identity.Instance);
        }
        return SavedCredit(transaction, key, parameters, stored.AssignedAtUtcTicks);
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
