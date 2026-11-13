namespace ZoneEngine_New.Core.Quests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using AORebirth.Interfaces.Persistence.Missions;
using SmokeLounge.AOtomation.Messaging.GameData;
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

    /// <summary>
    /// A kill in a mission dungeon: marks the slot dead and tells the owner, or the owner's whole team, the reward
    /// chance so far, whatever their side. The line comes from the chat server as an anonymous vicinity message
    /// (yellow, as "All team members can loot these remains."); zone Feedback to the recipients in this dungeon when
    /// the chat server cannot be reached. The owner only has to be in the dungeon, not near the kill.
    /// </summary>
    internal void RecordDungeonKill(QuestDungeonPlayfield dungeon, NpcCharacter npc, IReadOnlyList<Player> players)
    {
        if (dungeon.Kills is not { } kills)
            return;
        if (!kills.TryRecordKill(npc))
        {
            LogRewardChance(dungeon, npc, kills, "not-population");
            return;
        }
        if (kills.Policy is not { } policy)
        {
            LogRewardChance(dungeon, npc, kills, "no-reward-policy");
            return;
        }
        (int killed, int total) = kills.Chance();
        if (total <= 0 || killed <= 0)
        {
            LogRewardChance(dungeon, npc, kills, "no-kills");
            return;
        }
        if (policy.FormatFeedback((float)(100.0 * killed / total)) is not { } text)
        {
            LogRewardChance(dungeon, npc, kills, "no-feedback-text");
            return;
        }

        // The owner, or on a team mission any member inside still holding it (the owner may be outside or done).
        Player? owner = null;
        PlayerQuest? quest = null;
        foreach (Player player in dungeon.GetRequiredService<DynelRegistry>().PlayerEntities())
        {
            if (!GetLog(player).Quests.TryGetValue(kills.QuestId, out PlayerQuest? held))
                continue;
            if (player.Identity.Instance == kills.OwnerId)
            {
                (owner, quest) = (player, held);
                break;
            }
            if (owner == null && held.IsActive && held.Template.IsTeamScope)
                (owner, quest) = (player, held);
        }
        if (owner == null || quest == null)
        {
            LogRewardChance(dungeon, npc, kills, "owner-or-quest-missing");
            return;
        }
        if (!HasGatedReward(owner, quest, policy))
        {
            LogRewardChance(dungeon, npc, kills, "nothing-gated side=" + owner.Stats.GetOrZero(CharacterStat.Side));
            return;
        }

        IReadOnlyList<int> recipients = dungeon.GetService<Teams.TeamService>()?.GetTeam(owner)?.MemberIds
            ?? [owner.Identity.Instance];
        if (dungeon.GetService<Chat.VicinityChatRelay>()?.Announce(text, recipients) == true)
        {
            LogRewardChance(dungeon, npc, kills, "sent-chat recipients=" + recipients.Count);
            return;
        }

        foreach (Player player in dungeon.GetRequiredService<DynelRegistry>().PlayerEntities())
            if (recipients.Contains(player.Identity.Instance))
                ClientFeedback.SendFormatted(player, ClientFeedback.PlainText, text);
        LogRewardChance(dungeon, npc, kills, "sent-zone recipients=" + recipients.Count);
    }

    void LogRewardChance(QuestDungeonPlayfield dungeon, NpcCharacter npc, DungeonKills kills, string outcome)
    {
        (int killed, int total) = kills.Chance();
        _logger.Info("Mission reward chance pf=" + dungeon.Identity.Instance + " quest=" + kills.QuestId + " npc=" + npc.Name
            + " kills=" + killed + "/" + total + " outcome=" + outcome);
    }

    /// <summary>
    /// True when the reward roll has something to decide for <paramref name="player"/>: the quest's XP or items, or a
    /// token for their side. With nothing behind it the dungeon does not roll and sends no chance line.
    /// </summary>
    bool HasGatedReward(Player player, PlayerQuest quest, MissionTokenRewardPolicy policy)
        => (quest.Template.Reward?.XpFor(player.Stats.GetOrOne(CharacterStat.Level)) ?? 0) > 0
            || RewardItemsFor(player, quest).Count > 0
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
    /// One draw at earning, in the earning transaction, with the final kill checkpoint beside it. A team mission's
    /// holders share the chance: in the dungeon its one kill ledger decides; away from it, the chance a teammate earned
    /// the objective with (<paramref name="sharedChance"/>). Each holder still draws once for themselves.
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
            (killed, total) = kills.Chance();
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
            // Earning away from the dungeon (a bound objective committed after relogging): the last checkpoint decides.
            (killed, total) = DungeonKills.ChanceOf(parameters, transaction.GetFlag(key, KillsFlag(stored.AssignedAtUtcTicks))?.Value);
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
