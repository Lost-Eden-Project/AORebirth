namespace ZoneEngine_New.Core.Quests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Missions;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Modules;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    /// <summary>A quest's reward as a module audit source (module API 1.17): its title, and its quest id as the detail.</summary>
    static AORebirth.Modules.Events.StatChangeSource AuditSource(PlayerQuest quest) => new()
    {
        Name = quest.Template.Title ?? quest.Template.Name,
        Detail = quest.QuestId
    };

    static bool RequiresBoundObjective(PlayerQuest quest) => quest.Source == QuestSource.Generated
        && (quest.Template.IsFindItem || quest.Template.IsReturnItem || quest.Template.IsRepair
            || QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                && parameters.ObjectiveNpc != null);

    /// <summary>
    /// True when <see cref="TryComplete"/> would complete the quest now: it is active and, for a generated mission
    /// with a bound objective, that objective is done. A dialog checks this before it takes anything for the quest.
    /// </summary>
    public bool CanComplete(Player player, string questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        return GetLog(player).TryFind(questId, out PlayerQuest? quest) && quest.IsActive
            && (!RequiresBoundObjective(quest) || quest.IsRewardPending);
    }

    /// <summary>Find Person completes when the holder selects the accepted actor (LookAt), in line of sight.</summary>
    void TryEarnTargetedNpcObjectives(Player player, NpcCharacter npc)
    {
        foreach (var quest in GetLog(player).Quests.Values)
            if (quest.IsActive && quest.Template.Action == QuestTemplate.TargetNpcAction && RequiresBoundObjective(quest))
                TryEarnNpcObjective(player, quest, npc, targeted: true);
    }

    void TryEarnNpcObjective(Player player, PlayerQuest quest, NpcCharacter npc, bool targeted)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null || !quest.IsActive || quest.IsRewardPending
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                || parameters.ObjectiveNpc is not { IsValid: true } binding
                || binding.MissionType != (targeted ? 1 : 0)
                // A team mission's target counts for every member holding it; the database binds the holder's own row.
                || (binding.OwnerId != player.Identity.Instance && !quest.Template.IsTeamScope)
                || binding.QuestId != quest.QuestId || npc.Identity != binding.Identity
                || npc.Playfield is not ZoneEngine_New.Core.Playfield.QuestDungeonPlayfield world
                || !ReferenceEquals(player.Playfield, world) || world.Identity.Instance != parameters.DungeonPlayfield
                || world.QuestId != quest.QuestId
                || !world.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>().TryGet(npc.Identity, out var registered)
                || !ReferenceEquals(registered, npc)
                // Find Person has no range of its own: the caller requires the NPC be spawned on this client
                // (PlayfieldLocality.IsVisibleTo); only line of sight is checked here.
                || targeted && !player.HasLineOfSightTo(npc))
                return;
            if (quest.IsCommitInFlight(_now().Ticks))
                return;
            BeginBoundObjectiveEarn(player, quest, parameters, removal: null, sharedChance: null, share: true,
                earned: null,
                failed: () =>
                {
                    if (!targeted && !player.IsPersistenceQuarantined)
                    {
                        quest.NpcObjectiveCommitPending = true;
                        quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
                    }
                });
        }
    }

    /// <summary>Claims mission-owned item interactions before generic item spells can handle them.</summary>
    public bool TryUseGeneratedMissionItem(Player player, Identity sourceSlot, Identity target, out bool handled)
    {
        handled = false;
        lock (player.PersistenceGate)
        {
            // A mission machine never falls through to its ordinary item-template spells,
            // including when a key holder has no ownership of this mission.
            if (player.Playfield is { } currentWorld
                && currentWorld.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>()
                    .TryGet(target, out var registeredTarget) && registeredTarget is MissionObjectiveItem)
                handled = true;
            if (!CanDeliver(player) || _completionDao == null)
                return false;
            player.Inventory.TryGetUseItem(sourceSlot, out Item source);
            foreach (var quest in GetLog(player).Quests.Values)
            {
                if (quest.Source != QuestSource.Generated
                    || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters))
                    continue;
                MissionItemObjectiveBinding? binding = quest.Template.IsRepair ? parameters.ObjectiveTool
                    : quest.Template.IsReturnItem ? parameters.ObjectiveItem : null;
                bool ownsTarget = quest.Template.IsRepair && parameters.ObjectiveItem?.Identity == target;
                if (binding?.IsValid != true || (source?.Identity != binding.Identity && !ownsTarget))
                    continue;
                handled = true;
                if (!quest.IsActive || quest.IsRewardPending || source == null || source.Locked || !source.IsPersisted
                    || binding.OwnerId != player.Identity.Instance || binding.QuestId != quest.QuestId
                    || source.Identity != binding.Identity || source.InstanceId != binding.Instance
                    || source.LowId != binding.LowId || source.HighId != binding.HighId || source.Quality != binding.Quality
                    || source.StackCount != 1)
                    return false;
                var world = player.Playfield!;
                if (!world.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>().TryGet(target, out var destination))
                    return false;
                if (quest.Template.IsRepair)
                {
                    if (world is not ZoneEngine_New.Core.Playfield.QuestDungeonPlayfield dungeon
                        || dungeon.QuestId != quest.QuestId || dungeon.Identity.Instance != parameters.DungeonPlayfield
                        || destination is not MissionObjectiveItem machine
                        || parameters.ObjectiveItem?.SameObjectiveAs(machine.Binding) != true
                        || player.GetEdgeDistanceTo(machine) > LootableDynel.OpenRange)
                        return false;
                }
                else
                    return false;

                if (sourceSlot.Type != IdentityType.Inventory)
                {
                    ClientFeedback.Send(player, "Move the mission item into your main inventory before using it.");
                    return false;
                }
                Container page = player.Inventory.Inventory;
                int placement = sourceSlot.Instance;
                if (quest.IsCommitInFlight(_now().Ticks))
                    return false;
                // Held still until the transaction lands: a locked item cannot be moved, traded or deleted meanwhile.
                source.Locked = true;
                Identity usedSlot = sourceSlot;
                BeginBoundObjectiveEarn(player, quest, parameters, ToMissionItem(source.ToRecord(page.Identity, placement, source.StackCount)),
                    sharedChance: null, share: true,
                    earned: () =>
                    {
                        source.Locked = false;
                        try
                        {
                            if (!ReferenceEquals(page.Remove(placement), source))
                                throw new InvalidOperationException("Committed mission source changed before publication.");
                            ModuleHooks.ItemMoved(player, source, ModuleHooks.Place(AORebirth.Modules.Events.ItemPlaceKind.Inventory),
                                ModuleHooks.Place(AORebirth.Modules.Events.ItemPlaceKind.Destroyed));
                            player.Session?.Send(new TemplateActionMessage
                            {
                                Identity = player.Identity, ItemLowId = source.LowId, ItemHighId = source.HighId,
                                Quality = source.Quality, Placement = usedSlot, Count = 1,
                                Action = TemplateActionType.Use, Unknown3 = (int)player.Identity.Type, Unknown4 = player.Identity.Instance
                            });
                            player.Session?.Send(new CharacterActionMessage
                            {
                                Identity = player.Identity, Action = CharacterActionType.DeleteItem, Target = usedSlot
                            });
                        }
                        catch (Exception exception)
                        {
                            CompletionFailure(player, exception, committed: true);
                        }
                    },
                    failed: () => source.Locked = false);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A team mission's objective was earned by one holder: the dungeon service passes it on to the other holders
    /// online and inside the mission's dungeon (<see cref="EarnShared"/>). Raised on the earner's playfield, inside their gate.
    /// </summary>
    internal event Action<Player, PlayerQuest>? TeamObjectiveEarned;

    /// <summary>
    /// A teammate earned this team mission's objective: this holder earns it too, rolling the reward with the team
    /// dungeon's kill chance (<paramref name="chance"/>; null: their own dungeon or last checkpoint decides). A holder
    /// who cannot take it now (dead, zoning) retries on later ticks while loaded.
    /// </summary>
    internal void EarnShared(Player player, string questId, (int Killed, int Total)? chance)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (player.PersistenceGate)
        {
            if (_completionDao == null || player.IsPersistenceQuarantined
                || !GetLog(player).Quests.TryGetValue(questId, out PlayerQuest? quest)
                || !quest.IsActive || quest.IsRewardPending || !quest.Template.IsTeamScope
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters))
                return;

            quest.SharedRewardChance = chance ?? quest.SharedRewardChance;
            if (!CanDeliver(player))
            {
                quest.SharedEarnPending = true;
                quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
                return;
            }
            if (quest.IsCommitInFlight(_now().Ticks))
                return;
            BeginBoundObjectiveEarn(player, quest, parameters, removal: null, quest.SharedRewardChance, share: false,
                earned: null,
                failed: () =>
                {
                    if (player.IsPersistenceQuarantined)
                        return;
                    quest.SharedEarnPending = true;
                    quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
                });
        }
    }

    /// <summary>How long a queued mission transaction may stay in flight before the retry tick may queue another.</summary>
    const int CommitInFlightSeconds = 30;

    /// <summary>
    /// Runs a mission transaction for <paramref name="quest"/> in the player's write-behind lane (behind everything
    /// their inventory already queued, like the hard flush that used to precede it) and brings the result back to the
    /// player's tick, under their gate: <paramref name="apply"/> with what the transaction returned, or
    /// <paramref name="failed"/> with the exception it threw. The quest shows the transaction as in flight meanwhile,
    /// so the retry tick does not queue it twice. An unknown commit outcome is left to the lane, which quarantines the
    /// player. The caller holds the gate.
    /// </summary>
    void RunMissionTransaction<T>(Player player, PlayerQuest quest, Func<IMissionDaoTransaction, T> transaction,
        Action<T> apply, Action<Exception> failed)
    {
        quest.CommitInFlightUntilUtcTicks = _now().AddSeconds(CommitInFlightSeconds).Ticks;
        int characterId = player.Identity.Instance;
        _flush.Enqueue(player, () =>
        {
            T? result = default;
            Exception? failure = null;
            try
            {
                result = _completionDao!.Execute(characterId, null!, transaction);
            }
            catch (Exception exception) when (exception is MissionCommitOutcomeUnknownException or DatabaseCommitOutcomeUnknownException)
            {
                lock (player.PersistenceGate)
                    quest.CommitInFlightUntilUtcTicks = 0;
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            ZoneEngine_New.Core.Playfield.Playfield? here = player.Playfield;
            if (here == null)
            {
                // The player left before the result could be applied: a stored earn is recovered at the next login.
                lock (player.PersistenceGate)
                    quest.CommitInFlightUntilUtcTicks = 0;
                return;
            }
            here.DispatchPlayerProjection(player, () =>
            {
                lock (player.PersistenceGate)
                {
                    quest.CommitInFlightUntilUtcTicks = 0;
                    try
                    {
                        if (failure != null)
                            failed(failure);
                        else
                            apply(result!);
                    }
                    catch (Exception exception)
                    {
                        CompletionFailure(player, exception, committed: true);
                    }
                }
            });
        });
    }

    /// <summary>Runs <paramref name="action"/> on the objective's playfield tick (now, when this is it).</summary>
    static void OnObjectiveTick(MissionObjectiveItem objective, Action action)
    {
        if (objective.Playfield is ZoneEngine_New.Core.Playfield.Playfield playfield)
            playfield.Dispatch(action);
        else
            action();
    }

    /// <summary>
    /// Queues a bound objective's earn (<see cref="RunMissionTransaction{T}"/>): the stored quest must still be the
    /// accepted one with the objective open. Once it lands, on the player's tick under their gate, the quest's
    /// progress is set and the objective shared with the team, then <paramref name="earned"/> runs; a refusal or
    /// failure runs <paramref name="failed"/>. The caller holds the gate and has checked the quest is not in flight.
    /// </summary>
    void BeginBoundObjectiveEarn(Player player, PlayerQuest quest, QuestDungeonParameters parameters, MissionItemInstanceData? removal,
        (int Killed, int Total)? sharedChance, bool share, Action? earned, Action? failed)
    {
        RunMissionTransaction(player, quest,
            tx =>
            {
                var completion = RequireCompletion(tx);
                var stored = completion.ReadCharacterQuest(quest.QuestId);
                RequireCurrent(stored, quest, player);
                if (stored.State != (int)QuestState.Active || stored.Progress >= stored.RequiredCount
                    || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var durable)
                    || durable.ToJson() != parameters.ToJson()
                    || quest.Template.IsReturnItem && stored.Progress != 1)
                    throw new InvalidOperationException("Mission objective is no longer collectible.");
                return EarnWithToken(tx, completion, player, quest, new()
                {
                    QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                    ExpectedDefinitionJson = stored.DefinitionJson,
                    ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson,
                    NowUtcTicks = _now().Ticks, Removals = removal == null ? [] : [removal]
                }, sharedChance);
            },
            apply: result =>
            {
                if (result.Status != CharacterQuestCompletionStatus.Earned)
                {
                    _logger.Warn("Mission objective not earned char=" + player.Identity.Instance + " quest=" + quest.QuestId
                        + " status=" + result.Status + " reason=" + result.Reason);
                    failed?.Invoke();
                    return;
                }
                quest.Progress = quest.RequiredCount;
                quest.NpcObjectiveCommitPending = false;
                quest.SharedEarnPending = false;
                quest.NextRewardRetryUtcTicks = 0;
                earned?.Invoke();
                if (share)
                    ShareObjective(player, quest);
            },
            failed: exception =>
            {
                CompletionFailure(player, exception, committed: false);
                failed?.Invoke();
            });
    }

    /// <summary>Passes a team mission's earned objective on to its other holders; a failing listener never undoes the earn.</summary>
    void ShareObjective(Player player, PlayerQuest quest)
    {
        if (!quest.Template.IsTeamScope || TeamObjectiveEarned == null)
            return;
        try
        {
            TeamObjectiveEarned(player, quest);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Sharing team mission objective failed quest=" + quest.QuestId);
        }
    }

    /// <summary>Only the registered owner-bound world objective can call this collection path.</summary>
    internal bool TryCollectFindItem(Player player, MissionObjectiveItem objective)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null
                || objective.Playfield is not ZoneEngine_New.Core.Playfield.QuestDungeonPlayfield world
                || !ReferenceEquals(player.Playfield, world)
                || player.Identity.Instance != objective.Binding.OwnerId
                || !GetLog(player).Quests.TryGetValue(objective.Binding.QuestId, out PlayerQuest? quest)
                || !quest.IsActive || quest.IsRewardPending || !(quest.Template.IsFindItem || quest.Template.IsReturnItem)
                || quest.Progress != 0
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                || parameters.DungeonPlayfield != world.Identity.Instance
                || parameters.ObjectiveItem?.SameObjectiveAs(objective.Binding) != true
                || player.GetEdgeDistanceTo(objective) > LootableDynel.OpenRange
                || !world.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>().TryGet(objective.Identity, out var current)
                || !ReferenceEquals(current, objective)
                || quest.IsCommitInFlight(_now().Ticks))
                return false;

            Item item = _items.Create(objective.LowId, objective.HighId, objective.Quality,
                ItemSource.Quest, 1, objective.Identity.Instance, objective.Identity);
            if (!InventoryGrantPlan.TryCreate(player, [item], out var grant))
            {
                ClientFeedback.Send(player, "Make inventory space before picking up the mission item.");
                return false;
            }
            bool returnItem = quest.Template.IsReturnItem;
            RunMissionTransaction(player, quest,
                tx =>
                {
                    var completion = RequireCompletion(tx);
                    var stored = completion.ReadCharacterQuest(quest.QuestId);
                    RequireCurrent(stored, quest, player);
                    // The world item is bound to the mission's owner: only they pick it up; teammates earn by sharing.
                    if (stored.Source != (int)QuestSource.Generated || stored.OwnerId != player.Identity.Instance
                        || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var durable)
                        || durable.ObjectiveItem?.SameObjectiveAs(objective.Binding) != true
                        || durable.DungeonPlayfield != world.Identity.Instance)
                        throw new InvalidOperationException("Objective does not match the accepted mission.");
                    var request = new CharacterQuestEarnRequest
                    {
                        QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                        ExpectedDefinitionJson = stored.DefinitionJson,
                        ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson,
                        NowUtcTicks = _now().Ticks, Grants = grant.Rows.Select(ToMissionItem).ToArray()
                    };
                    return returnItem
                        ? (tx as ICharacterQuestObjectiveCollectionTransaction
                            ?? throw new InvalidOperationException("Mission DAO cannot persist partial objective collection."))
                            .TryCollectCharacterQuestItem(request)
                        : EarnWithToken(tx, completion, player, quest, request);
                },
                apply: result =>
                {
                    bool collected = result.Status == (returnItem ? CharacterQuestCompletionStatus.Collected : CharacterQuestCompletionStatus.Earned);
                    if (!collected)
                    {
                        _logger.Warn("Mission item not collected char=" + player.Identity.Instance + " quest=" + quest.QuestId
                            + " status=" + result.Status + " reason=" + result.Reason);
                        OnObjectiveTick(objective, () => objective.FinishPickup(player, collected: false));
                        return;
                    }
                    try
                    {
                        grant.PublishAfterCommit(notify: false,
                            from: ModuleHooks.Place(AORebirth.Modules.Events.ItemPlaceKind.World, objective.Identity));
                        quest.Progress = returnItem ? 1 : quest.RequiredCount;
                        quest.NextRewardRetryUtcTicks = 0;
                        if (!returnItem)
                            ShareObjective(player, quest);
                    }
                    finally
                    {
                        // The objective dynel publishes the pickup acknowledgement and leaves the world.
                        // The player's next tick then delivers the independent terminal rewards.
                        OnObjectiveTick(objective, () => objective.FinishPickup(player, collected: true));
                    }
                },
                failed: exception =>
                {
                    CompletionFailure(player, exception, committed: false);
                    OnObjectiveTick(objective, () => objective.FinishPickup(player, collected: false));
                });
            return true;
        }
    }

    /// <summary>
    /// Find Item (not Return an item) completes when the mission's owner selects the world item (LookAt) in line of
    /// sight; nothing is picked up. Only the registered owner-bound world objective can call this.
    /// </summary>
    internal bool TryEarnTargetedFindItem(Player player, MissionObjectiveItem objective)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null
                || objective.Playfield is not ZoneEngine_New.Core.Playfield.QuestDungeonPlayfield world
                || !ReferenceEquals(player.Playfield, world)
                || player.Identity.Instance != objective.Binding.OwnerId
                || !GetLog(player).Quests.TryGetValue(objective.Binding.QuestId, out PlayerQuest? quest)
                || !quest.IsActive || quest.IsRewardPending || !quest.Template.IsFindItem || quest.Progress != 0
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                || parameters.DungeonPlayfield != world.Identity.Instance
                || parameters.ObjectiveItem?.SameObjectiveAs(objective.Binding) != true
                || !world.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>().TryGet(objective.Identity, out var current)
                || !ReferenceEquals(current, objective)
                || !player.HasLineOfSightTo(objective)
                || quest.IsCommitInFlight(_now().Ticks))
                return false;
            BeginBoundObjectiveEarn(player, quest, parameters, removal: null, sharedChance: null, share: true,
                earned: () => OnObjectiveTick(objective, () => objective.FinishFind(earned: true)),
                failed: () => OnObjectiveTick(objective, () => objective.FinishFind(earned: false)));
            return true;
        }
    }

    public void RetryPendingRewards(Player player, bool force = false)
    {
        if (!CanDeliver(player) || _completionDao == null)
            return;
        var log = player.QuestLog ??= Load(player);
        long now = _now().Ticks;
        foreach (var quest in log.Quests.Values.Where(q => q.IsActive && !q.IsRewardPending
            && (q.NpcObjectiveCommitPending || q.SharedEarnPending) && !q.IsCommitInFlight(now)
            && (force || q.NextRewardRetryUtcTicks <= now)).ToArray())
        {
            lock (player.PersistenceGate)
            {
                quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
                if (!QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                    || quest.IsCommitInFlight(now))
                    continue;
                // The pending flags stay set until the earn lands, so a refusal is retried at the next due time.
                if (quest.SharedEarnPending)
                    BeginBoundObjectiveEarn(player, quest, parameters, removal: null, quest.SharedRewardChance, share: false,
                        earned: null, failed: null);
                else if (parameters.ObjectiveNpc is { IsValid: true, MissionType: 0 })
                    BeginBoundObjectiveEarn(player, quest, parameters, removal: null, sharedChance: null, share: true,
                        earned: null, failed: null);
            }
        }
        foreach (var quest in log.Quests.Values.Where(q => q.IsRewardPending && !q.IsCommitInFlight(now)
            && (force || q.NextRewardRetryUtcTicks <= now)).ToArray())
            Complete(player, quest);
    }

    static bool CanDeliver(Player player) => !player.IsDead && !player.IsPersistenceQuarantined
        && player.Inventory.IsHydrated && player.Playfield != null
        && player.Session is { State: SessionState.InPlay } session && ReferenceEquals(session.Player, player);

    void Complete(Player player, PlayerQuest quest)
    {
        if (quest.Source == QuestSource.Template)
            CompleteTemplate(player, quest);
        else
            CompleteMission(player, quest);
    }

    /// <summary>
    /// A Quests.json quest's completion, decided and shown on the tick without waiting on the database: the reward is
    /// paid in memory and its earn and reward transactions are queued in the player's write-behind lane
    /// (<see cref="InventoryFlushService.Enqueue"/>), which stores them in order ahead of any later inventory or trade
    /// write, checkpoint, logout or zone change. A queued transaction that fails quarantines the player; storage then
    /// still holds the quest unpaid (or earned and pending), so a reward is never paid twice.
    /// </summary>
    void CompleteTemplate(Player player, PlayerQuest quest)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null || _snapshot == null || !quest.IsActive)
                return;
            quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
            bool queued = false;
            quest.Progress = quest.RequiredCount;
            try
            {
                QuestReward? reward = quest.Template.Reward;
                List<Item> items = MintRewardItems(player, quest, RewardItemsFor(player, quest, reward), out List<Item> used);
                if (!InventoryGrantPlan.TryCreate(player, items, out var grant, out GrantRefusal refusal, allowOverflow: true))
                {
                    QueueTemplateEarn(player, quest);
                    _logger.Warn("Quest reward pending char=" + player.Identity.Instance + " quest=" + quest.QuestId + " reason=" + refusal);
                    NotifyPending(player, quest, uniqueConflict: refusal is GrantRefusal.UniqueHeld or GrantRefusal.UniqueRepeated);
                    return;
                }
                int xpReward = reward?.XpFor(player.Stats.GetOrOne(CharacterStat.Level)) ?? 0;
                var values = new Dictionary<CharacterStat, int>();
                if (reward?.Credits > 0)
                    values[CharacterStat.Cash] = Characters.CashLimit.Clamp(
                        (long)player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base) + reward.Credits);
                if (quest.Template.MissionBit is int bit && MissionBits.IsValid(bit))
                {
                    var (stat, mask) = MissionBits.Locate(bit);
                    values[stat] = player.Stats.GetOrZero(stat, StatDetail.Base) | mask;
                }
                // What the tick holds now is what the reward transaction expects storage to hold; the commit stores it first.
                StatRecord[] before = values.Keys.Select(stat => new StatRecord
                {
                    StatId = (int)stat, StatValue = player.Stats.GetOrZero(stat, StatDetail.Base)
                }).ToArray();
                StatRecord[] paid = values.Select(pair => new StatRecord { StatId = (int)pair.Key, StatValue = pair.Value }).ToArray();
                MissionItemInstanceData[] grants = grant.ParkedRows().Select(ToMissionItem).ToArray();

                quest.State = QuestState.Completed;
                // The reward commit earns first itself (an earn already stored is accepted as it is).
                _flush.Enqueue(player, () => CommitTemplateReward(player, quest, grant, grants, before, paid));
                quest.EarnQueued = true;
                queued = true;
                try
                {
                    using (StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Quest, AuditSource(quest)))
                        grant.PublishBeforeCommit();
                }
                finally
                {
                    Ended(player, quest);
                }
                using (Modules.StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Quest, AuditSource(quest)))
                {
                    foreach (var pair in values)
                        player.Stats.Set(pair.Key, pair.Value, StatDetail.Base, dirty: true);
                    player.FlushDirtyStats();
                    if (xpReward > 0)
                        player.AwardXp(xpReward, XpSource.Quest);
                }
                if (used.Count > 0 && player.Playfield?.GetService<IInventoryRepository>() is IInventoryRepository inventory)
                    foreach (Item item in used)
                        item.Definition.ExecuteOnUseSpells(player, inventory, _items, source: player);
                QuestJournal.SendEnded(player, quest, CharacterActionType.MissionAccomplished);
                QuestJournal.Delete(player, quest);
                _logger.Info("Quest completed, rewards queued for commit char=" + player.Identity.Instance + " quest=" + quest.QuestId);
                if (!string.IsNullOrEmpty(quest.Template.NextStep) && _catalog.TryGet(quest.Template.NextStep, out _))
                    TryAssignTemplate(player, quest.Template.NextStep, out _);
            }
            catch (Exception exception)
            {
                // Once the reward is queued, memory already shows it: a failure past that point must reload from storage.
                CompletionFailure(player, exception, queued);
                if (!queued && !player.IsPersistenceQuarantined)
                {
                    // Delivery failed before anything was paid; the earn is still stored, as the quest shows it earned.
                    try
                    {
                        QueueTemplateEarn(player, quest);
                    }
                    catch (Exception earnFailure)
                    {
                        _logger.Error(earnFailure, "Queuing quest earn failed char=" + player.Identity.Instance + " quest=" + quest.QuestId);
                    }
                }
                if (quest.IsRewardPending && !player.IsPersistenceQuarantined)
                    NotifyPending(player, quest);
            }
        }
    }

    /// <summary>
    /// Queues the earn of a Quests.json quest the tick shows as earned and pending delivery, once per session: storage
    /// then keeps it pending across a relog, as a reward that does not fit yet must stay.
    /// </summary>
    void QueueTemplateEarn(Player player, PlayerQuest quest)
    {
        if (quest.EarnQueued)
            return;
        _flush.Enqueue(player, () => EarnTemplate(player, quest));
        quest.EarnQueued = true;
    }

    /// <summary>Write-behind lane: stores a Quests.json quest's earn (already shown as earned on the tick).</summary>
    CharacterQuestCompletionResult EarnTemplate(Player player, PlayerQuest quest)
    {
        var earned = _completionDao!.Execute(player.Identity.Instance, null!, tx =>
        {
            var completion = RequireCompletion(tx);
            var stored = completion.ReadCharacterQuest(quest.QuestId);
            RequireCurrent(stored, quest, player);
            return completion.TryEarnCharacterQuest(new()
            {
                QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                ExpectedDefinitionJson = stored.DefinitionJson,
                ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks
            });
        });
        if (earned.Status is not (CharacterQuestCompletionStatus.Earned or CharacterQuestCompletionStatus.AlreadyEarned))
            throw new InvalidOperationException("Quest " + quest.QuestId + " was not earned: " + earned.Status + " " + earned.Reason);
        return earned;
    }

    /// <summary>
    /// Write-behind lane: stores the reward the tick already paid. Items go in at their parked rows (the dirty
    /// inventory flush right after moves them into their slots); credits and the mission bit are set from the values
    /// the tick held when it decided the reward, which are stored first so the transaction's fail-closed check holds.
    /// </summary>
    void CommitTemplateReward(Player player, PlayerQuest quest, InventoryGrantPlan grant, MissionItemInstanceData[] grants,
        StatRecord[] before, StatRecord[] paid)
    {
        var earned = EarnTemplate(player, quest);
        _snapshot!.SyncStats(player, before);
        var result = _completionDao!.Execute(player.Identity.Instance, null!, tx => RequireCompletion(tx).TryCompleteCharacterQuest(new()
        {
            QuestId = quest.QuestId, AssignedAtUtcTicks = earned.Quest.AssignedAtUtcTicks,
            ExpectedDefinitionJson = earned.Quest.DefinitionJson,
            ExpectedAcgBuildingGeneratorJson = earned.Quest.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks,
            Grants = grants,
            ExpectedStatValues = before.Select(stat => new MissionStatValueData
            {
                StatIdentityType = (int)player.Identity.Type, StatId = stat.StatId, Value = stat.StatValue
            }).ToArray(),
            StatMutations = paid.Select(stat => new MissionStatMutationData
            {
                StatIdentityType = (int)player.Identity.Type, StatId = stat.StatId,
                Kind = MissionStatMutationKind.Set, Value = stat.StatValue,
                MinimumValue = int.MinValue, MaximumValue = int.MaxValue
            }).ToArray()
        }));
        if (result.Status != CharacterQuestCompletionStatus.Applied)
            throw new InvalidOperationException("Quest " + quest.QuestId + " reward was not applied: " + result.Status + " " + result.Reason);
        grant.MarkStored();
        lock (player.PersistenceGate)
        {
            // Retire any checkpoint captured before the reward, so it cannot overwrite paid stats; then take a fresh one.
            player.SaveState.MarkCommitted(player.SaveState.NextSequence(), paid, location: null);
            player.SaveState.RequestCheckpoint();
        }
        _logger.Info("Quest rewards committed char=" + player.Identity.Instance + " quest=" + quest.QuestId);
    }

    /// <summary>
    /// A generated mission's completion. Its earn, with the reward roll and kill share read from storage, runs in the
    /// player's write-behind lane; the reward is then decided and shown on the player's tick and its transaction queued
    /// behind the earn, as a Quests.json quest's is (<see cref="CompleteTemplate"/>). While the earn is in flight the
    /// quest is marked so and the retry tick leaves it alone.
    /// </summary>
    void CompleteMission(Player player, PlayerQuest quest)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null || _snapshot == null || !quest.IsActive
                || quest.IsCommitInFlight(_now().Ticks))
                return;
            quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
            // Earning is durable even when capacity, content availability or delivery fails later.
            RunMissionTransaction(player, quest,
                tx =>
                {
                    var completion = RequireCompletion(tx);
                    var stored = completion.ReadCharacterQuest(quest.QuestId);
                    RequireCurrent(stored, quest, player);
                    if (RequiresBoundObjective(quest) && stored.Progress < stored.RequiredCount)
                        throw new InvalidOperationException("Mission completion requires its committed exact objective event.");
                    var result = EarnWithToken(tx, completion, player, quest, new()
                    {
                        QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                        ExpectedDefinitionJson = stored.DefinitionJson,
                        ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks
                    });
                    TokenRoll? tokenReward = null;
                    MissionShare? share = null;
                    if (result.Status is CharacterQuestCompletionStatus.Earned or CharacterQuestCompletionStatus.AlreadyEarned)
                    {
                        tokenReward = ReadTokenReward(tx, player, quest, result.Quest);
                        share = ShareFor(tx, player, quest, result.Quest, tokenReward);
                    }
                    return new MissionEarn(result, tokenReward, share);
                },
                apply: earn => DeliverMissionReward(player, quest, earn),
                failed: exception =>
                {
                    CompletionFailure(player, exception, committed: false);
                    if (quest.IsRewardPending && !player.IsPersistenceQuarantined)
                        NotifyPending(player, quest);
                });
        }
    }

    /// <summary>A mission's stored earn with the reward roll and kill share read beside it.</summary>
    sealed record MissionEarn(CharacterQuestCompletionResult Result, TokenRoll? TokenReward, MissionShare? Share);

    /// <summary>
    /// The earned mission's reward, on the player's tick under their gate: decided from the stored definition and roll,
    /// paid in memory and shown, with its transaction queued behind the earn in the player's lane (the items at parked
    /// rows, the stat values the tick held stored first so the transaction's fail-closed check holds).
    /// </summary>
    void DeliverMissionReward(Player player, PlayerQuest quest, MissionEarn earn)
    {
        CharacterQuestCompletionResult earned = earn.Result;
        if (earned.Status == CharacterQuestCompletionStatus.AlreadyApplied)
        {
            // A different/uncertain publication already paid; never overwrite its durable stats with this actor.
            player.QuarantinePersistence();
            player.Session?.Close();
            return;
        }
        if (earned.Status is not (CharacterQuestCompletionStatus.Earned or CharacterQuestCompletionStatus.AlreadyEarned))
        {
            _logger.Warn("Quest earn not applied char=" + player.Identity.Instance + " quest=" + quest.QuestId
                + " status=" + earned.Status + " reason=" + earned.Reason);
            return;
        }
        if (!CanDeliver(player) || !quest.IsActive)
            return;
        quest.Progress = quest.RequiredCount;
        TokenRoll? tokenReward = earn.TokenReward;
        MissionShare? share = earn.Share;
        bool queued = false;
        try
        {
            QuestTemplate rewardTemplate = quest.Source == QuestSource.Generated
                ? JsonSerializer.Deserialize<QuestTemplate>(earned.Quest.DefinitionJson, QuestTemplate.JsonOptions)
                    ?? throw new InvalidOperationException("Stored reward definition is unavailable.")
                : quest.Template;
            var reward = rewardTemplate.Reward;
            // A dungeon quest's reward roll gates its XP and items (and a sided token); credits pay on completion.
            bool rewardLost = tokenReward is { Won: false };
            List<QuestRewardItem> definitions = rewardLost ? new() : RewardItemsFor(player, quest, reward).ToList();
            if (tokenReward is { Won: true, ItemId: > 0 })
            {
                definitions.Add(new QuestRewardItem { Id = tokenReward.ItemId, HighId = tokenReward.ItemId, Quality = tokenReward.Quality });
            }
            List<Item> items = MintRewardItems(player, quest, definitions, out List<Item> used);
            if (!InventoryGrantPlan.TryCreate(player, items, out var grant, out GrantRefusal refusal, allowOverflow: true))
            {
                _logger.Warn("Quest reward pending char=" + player.Identity.Instance + " quest=" + quest.QuestId + " reason=" + refusal);
                NotifyPending(player, quest, uniqueConflict: refusal is GrantRefusal.UniqueHeld or GrantRefusal.UniqueRepeated);
                return;
            }
            // XP is awarded through the ordinary XP path (caps, research share, level-ups) and persists with the next
            // save; only cash and mission bits are durable parts of the reward transaction.
            // A mission with a reward split (Missions.json Rewards) pays its credits and XP by the share cleared, and
            // its XP no longer by the roll; one without pays as before.
            int fullXp = reward?.XpFor(player.Stats.GetOrOne(CharacterStat.Level)) ?? 0;
            double? rewardFactor = share?.Factor;
            int xpReward = rewardFactor is double xpShare
                ? (int)Math.Round(fullXp * xpShare, MidpointRounding.AwayFromZero)
                : rewardLost ? 0 : fullXp;
            int credits = reward?.Credits ?? 0;
            if (rewardFactor is double creditShare)
                credits = (int)Math.Round(credits * creditShare, MidpointRounding.AwayFromZero);
            var values = new Dictionary<CharacterStat, int>();
            if (credits > 0)
                values[CharacterStat.Cash] = Characters.CashLimit.Clamp(
                    (long)player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base) + credits);
            if (rewardTemplate.MissionBit is int bit && MissionBits.IsValid(bit))
            {
                var (stat, mask) = MissionBits.Locate(bit);
                values[stat] = player.Stats.GetOrZero(stat, StatDetail.Base) | mask;
            }
            // What the tick holds now is what the reward transaction expects storage to hold; the commit stores it first.
            StatRecord[] before = values.Keys.Select(stat => new StatRecord
            {
                StatId = (int)stat, StatValue = player.Stats.GetOrZero(stat, StatDetail.Base)
            }).ToArray();
            StatRecord[] paid = values.Select(pair => new StatRecord { StatId = (int)pair.Key, StatValue = pair.Value }).ToArray();
            MissionStatValueData[] expected = values.Keys.Select(stat => new MissionStatValueData
            {
                StatIdentityType = (int)player.Identity.Type, StatId = (int)stat,
                Value = player.Stats.GetOrZero(stat, StatDetail.Base)
            }).ToArray();
            MissionStatMutationData[] mutations = values.Select(pair => new MissionStatMutationData
            {
                StatIdentityType = (int)player.Identity.Type, StatId = (int)pair.Key,
                Kind = MissionStatMutationKind.Set, Value = pair.Value,
                MinimumValue = int.MinValue, MaximumValue = int.MaxValue
            }).ToArray();
            MissionItemInstanceData[] grants = grant.ParkedRows().Select(ToMissionItem).ToArray();
            CharacterQuestCompletionSnapshotData stored = earned.Quest;

            quest.State = QuestState.Completed;
            _flush.Enqueue(player, () => CommitMissionReward(player, quest, grant, stored, grants, before, paid, expected, mutations));
            queued = true;
            // Local playtest: AddTemplate fills the first free client slot, so publish before key deletion opens a hole.
            try
            {
                using (StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Quest, AuditSource(quest)))
                    grant.PublishBeforeCommit();
            }
            finally
            {
                // Durable lifecycle cleanup must still run if reward publication fails.
                Ended(player, quest);
            }
            using (Modules.StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Quest, AuditSource(quest)))
            {
                foreach (var pair in values)
                    player.Stats.Set(pair.Key, pair.Value, StatDetail.Base, dirty: true);
                player.FlushDirtyStats();
                if (xpReward > 0)
                    player.AwardXp(xpReward, XpSource.Quest);
            }
            if (used.Count > 0 && player.Playfield?.GetService<IInventoryRepository>() is IInventoryRepository inventory)
                foreach (Item item in used)
                    item.Definition.ExecuteOnUseSpells(player, inventory, _items, source: player);
            QuestJournal.SendEnded(player, quest, CharacterActionType.MissionAccomplished);
            QuestJournal.Delete(player, quest);
            _logger.Info("Quest completed, rewards queued for commit char=" + player.Identity.Instance + " quest=" + quest.QuestId
                + (tokenReward == null ? string.Empty : " rewardWon=" + tokenReward.Won + " tokenItem=" + tokenReward.ItemId
                    + " tokenKills=" + tokenReward.Killed + "/" + tokenReward.Total));
            // Modules hear of it once the reward is decided (module API 1.12): what is paid is settled, what they add is theirs.
            if (player.Playfield is ZoneEngine_New.Core.Playfield.Playfield completedOn)
                ModuleHooks.MissionCompleted(completedOn, player, quest, rewardTemplate, share, credits, xpReward, tokenReward?.Won);
            if (!string.IsNullOrEmpty(rewardTemplate.NextStep) && _catalog.TryGet(rewardTemplate.NextStep, out _))
                TryAssignTemplate(player, rewardTemplate.NextStep, out _);
        }
        catch (Exception exception)
        {
            // Once the reward is queued, memory already shows it: a failure past that point must reload from storage.
            CompletionFailure(player, exception, queued);
            if (quest.IsRewardPending && !player.IsPersistenceQuarantined)
                NotifyPending(player, quest);
        }
    }

    /// <summary>
    /// Write-behind lane: stores the mission reward the tick already paid. Items go in at their parked rows (the dirty
    /// inventory flush right after moves them into their slots); credits and the mission bit are set from the values
    /// the tick held when it decided the reward, which are stored first so the transaction's fail-closed check holds.
    /// </summary>
    void CommitMissionReward(Player player, PlayerQuest quest, InventoryGrantPlan grant, CharacterQuestCompletionSnapshotData stored,
        MissionItemInstanceData[] grants, StatRecord[] before, StatRecord[] paid, MissionStatValueData[] expected,
        MissionStatMutationData[] mutations)
    {
        _snapshot!.SyncStats(player, before);
        var result = _completionDao!.Execute(player.Identity.Instance, null!, tx => RequireCompletion(tx).TryCompleteCharacterQuest(new()
        {
            QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
            ExpectedDefinitionJson = stored.DefinitionJson,
            ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks,
            Grants = grants, ExpectedStatValues = expected, StatMutations = mutations
        }));
        if (result.Status != CharacterQuestCompletionStatus.Applied)
            throw new InvalidOperationException("Quest " + quest.QuestId + " reward was not applied: " + result.Status + " " + result.Reason);
        grant.MarkStored();
        lock (player.PersistenceGate)
        {
            // Retire any checkpoint captured before the reward, so it cannot overwrite paid stats; then take a fresh one.
            player.SaveState.MarkCommitted(player.SaveState.NextSequence(), paid, location: null);
            player.SaveState.RequestCheckpoint();
        }
        _logger.Info("Mission rewards committed char=" + player.Identity.Instance + " quest=" + quest.QuestId);
    }

    /// <summary>
    /// Mints the reward items for <paramref name="definitions"/>. Live's virtual reward items, used on the player rather
    /// than handed over, come back in <paramref name="used"/>; a Unique the character holds, or the reward gives twice,
    /// is left out. Throws on an invalid definition, which leaves delivery pending.
    /// </summary>
    List<Item> MintRewardItems(Player player, PlayerQuest quest, IEnumerable<QuestRewardItem> definitions, out List<Item> used)
    {
        var items = new List<Item>();
        var skippedUnique = new List<string>();
        used = new List<Item>();
        foreach (var definition in definitions)
        {
            if (definition.Id <= 0 || definition.HighId is <= 0 || definition.Quality <= 0)
                throw new InvalidOperationException("Reward item definition is invalid; delivery remains pending.");
            int highId = definition.HighId ?? definition.Id;
            // Live's virtual reward items (Daily Mission XP Reward, Token Add, Set Flag items) carry the Null
            // flag: they are used on the player, never placed in the inventory, so no instance is minted.
            if (IsUsedOnReward(definition.Id, highId, definition.Quality))
            {
                used.Add(_items.Create(definition.Id, highId, definition.Quality, ItemSource.Quest));
                continue;
            }
            // A Unique reward the character already holds (or this reward already gives) is left out: the quest
            // still completes on its dialog and pays the rest, rather than staying open until the held copy is
            // gone. Checked on the item exactly as it will be minted, with the grant plan's own Unique checks, so
            // the plan never refuses what this lets through; no instance is allocated for one left out.
            Item candidate = _items.Create(definition.Id, highId, definition.Quality, ItemSource.Quest);
            if (UniqueItems.WouldDuplicate(player, candidate, out _) || UniqueItems.HasRepeatedUnique([.. items, candidate], out _))
            {
                skippedUnique.Add(string.IsNullOrEmpty(definition.Name) ? definition.Id.ToString(CultureInfo.InvariantCulture) : definition.Name);
                continue;
            }
            Item minted = _items.CreateWithNewInstance(definition.Id, highId, definition.Quality, ItemSource.Quest);
            items.Add(minted);
            // A configured Count replaces a stackable template's mint count, as a daily login reward's does. A
            // non-stackable reward (sleeves, armor, bags) is given as that many separate items; a Unique once.
            if (definition.Count is int count)
            {
                if (InventoryStacking.IsStackable(minted))
                    minted.StackCount = count;
                else if (!ZoneEngine_New.Core.Trade.TradeRules.IsUnique(minted))
                    for (int copy = 1; copy < count; copy++)
                        items.Add(_items.CreateWithNewInstance(definition.Id, highId, definition.Quality, ItemSource.Quest));
            }
        }
        if (skippedUnique.Count > 0)
            _logger.Info("Quest reward unique items already held, left out char=" + player.Identity.Instance
                + " quest=" + quest.QuestId + " items=" + string.Join(", ", skippedUnique));
        return items;
    }

    /// <summary>
    /// The items a quest pays this player now. A Quests.json quest's come only from QuestRewards.json, by its reward
    /// hash: none when the hash has no entry or no entry matches. A generated quest's are the record stored with it.
    /// </summary>
    public IReadOnlyList<QuestRewardItem> RewardItemsFor(Player player, PlayerQuest quest, QuestReward? reward = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        reward ??= quest.Template.Reward;
        // A copy of a Quests.json dungeon quest pays like that quest: by its QuestRewards.json hash.
        if (quest.Source == QuestSource.Generated && string.IsNullOrEmpty(quest.Template.TemplateHash))
            return SelectRewardRecord(quest, reward);

        return _rewards != null && _rewards.TryResolve(reward?.Hash, player, quest, out IReadOnlyList<QuestRewardItem> mapped)
            ? mapped
            : [];
    }

    /// <summary>
    /// True when the quest pays this player items, from configuration alone (see <see cref="QuestRewardTable.HasItemsFor"/>):
    /// never resolves or mints, so it cannot throw on the tick, and a reward item that stopped resolving still counts.
    /// </summary>
    bool HasRewardItems(Player player, PlayerQuest quest)
    {
        QuestReward? reward = quest.Template.Reward;
        if (quest.Source == QuestSource.Generated && string.IsNullOrEmpty(quest.Template.TemplateHash))
            return SelectRewardRecord(quest, reward).Count > 0;
        return _rewards != null && _rewards.HasItemsFor(reward?.Hash, player);
    }

    /// <summary>A generated quest's stored record; records are alternatives, one chosen per accepted run.</summary>
    static IReadOnlyList<QuestRewardItem> SelectRewardRecord(PlayerQuest quest, QuestReward? reward)
    {
        if (reward?.Records is not { Count: > 0 }) return [];
        return reward.Records[QuestRewardTable.StableIndex(quest, reward.Records.Count)];
    }

    /// <summary>A reward item live uses on the player instead of handing over: its template has the Null flag.</summary>
    bool IsUsedOnReward(int lowId, int highId, int quality)
    {
        try
        {
            return (_items.CreateTemplate(lowId, highId, quality).Flags & (int)ItemFlags.Null) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static ICharacterQuestCompletionTransaction RequireCompletion(IMissionDaoTransaction transaction)
        => transaction as ICharacterQuestCompletionTransaction
            ?? throw new InvalidOperationException("Mission DAO does not support atomic character quest rewards.");

    static void NotifyPending(Player player, PlayerQuest quest, bool uniqueConflict = false)
    {
        if (quest.RewardPendingNotified) return;
        quest.RewardPendingNotified = true;
        ClientFeedback.SendFormatted(player, ClientFeedback.PlainText, uniqueConflict
            ? "Mission objective complete. Your reward is saved. Use or remove the matching Unique item you already hold and keep inventory space free; delivery retries automatically and after reconnect."
            : "Mission objective complete. Your reward is saved and pending delivery. Make inventory space; delivery retries automatically and after reconnect.");
    }

    static void RequireCurrent(CharacterQuestCompletionSnapshotData stored, PlayerQuest quest, Player player)
    {
        if (stored == null || stored.CharacterId != player.Identity.Instance || stored.Source != (int)quest.Source
            || stored.AssignedAtUtcTicks != quest.AssignedAtUtc.Ticks
            // A team-owned quest binds each member through their own characterquests row (read for this character).
            || (quest.Source == QuestSource.Generated && (stored.OwnerType switch
                {
                    (int)QuestOwnerType.Character => stored.OwnerId != player.Identity.Instance,
                    (int)QuestOwnerType.Team => stored.OwnerId <= 0,
                    _ => true
                } || stored.AcgBuildingGeneratorJson != quest.AcgBuildingGeneratorJson)))
            throw new InvalidOperationException("Accepted quest authority changed.");
    }

    static MissionItemInstanceData ToMissionItem(ItemInstanceRecord row) => new()
    {
        InstanceId = row.InstanceId, ContainerType = row.ContainerType, ContainerInstance = row.ContainerInstance,
        ContainerPlacement = row.ContainerPlacement, ItemType = row.ItemType, LowId = row.LowId, HighId = row.HighId,
        Quality = row.Quality, StackCount = row.StackCount, Source = (byte)row.Source
    };

    void CompletionFailure(Player player, Exception exception, bool committed)
    {
        if (committed || exception is MissionCommitOutcomeUnknownException or DatabaseCommitOutcomeUnknownException
            || player.IsPersistenceQuarantined)
        {
            player.QuarantinePersistence();
            player.Session?.Close();
        }
        _logger.Error(exception, "Quest collection/reward delivery failed; no uncommitted success published and earned rewards remain recoverable.");
    }
}
