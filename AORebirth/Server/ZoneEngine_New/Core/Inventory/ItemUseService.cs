namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    using ModuleHooks = ZoneEngine_New.Core.Modules.ModuleHooks;
    using ModuleCooldowns = ZoneEngine_New.Core.Modules.ModuleCooldowns;
    using CooldownKind = AORebirth.Modules.Events.CooldownKind;
    using CooldownKey = AORebirth.Modules.Events.CooldownKey;
    using PlayerActionKind = AORebirth.Modules.Events.PlayerActionKind;
    using PlayerActionOutcome = AORebirth.Modules.Events.PlayerActionOutcome;
    using ActionRefusal = AORebirth.Modules.Events.ActionRefusal;

    public enum ItemUseStart
    {
        Rejected,
        Started,
        Executed,
    }

    /// <summary>
    /// Authoritative item, world-object and perk action use with the template's AttackDelay. Items and perks share one
    /// queue per player: each use is checked in full (requirements, target, range, line of sight) when it is asked
    /// for, then waits its turn; its delay starts when it reaches the front. When the delay ends only what keeps it
    /// safe is checked again (still in play, the item still in its slot, the perk held and off cooldown, the target
    /// still alive and attackable). A queued inventory item stays locked until it runs or is cancelled. One per playfield.
    /// </summary>
    public sealed class ItemUseService
    {
        /// <summary>Upper bound on a template delay so a bad stat cannot lock an item indefinitely.</summary>
        public const int MaxDelayCentiseconds = 6000;

        /// <summary>
        /// Uses one player may have waiting, the running one included. A server bound against queue spam, not a
        /// value taken from live.
        /// </summary>
        public const int MaxQueuedUses = 4;

        const string FailedText = "You could not use that item.";

        private readonly object _gate = new();
        private readonly Dictionary<int, List<PendingItemUse>> _queues = new();
        private readonly Playfield _playfield;
        private readonly IZoneLogger _logger;
        private readonly InventoryMoveService _moves;
        private readonly IInventoryRepository _inventoryRepository;
        private readonly IItemBuilder _items;

        public ItemUseService(
            Playfield playfield,
            IZoneLogger logger,
            InventoryMoveService moves,
            IInventoryRepository inventoryRepository,
            IItemBuilder items)
        {
            _playfield = playfield ?? throw new ArgumentNullException(nameof(playfield));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _moves = moves ?? throw new ArgumentNullException(nameof(moves));
            _inventoryRepository = inventoryRepository ?? throw new ArgumentNullException(nameof(inventoryRepository));
            _items = items ?? throw new ArgumentNullException(nameof(items));
        }

        public static int ResolveDelayCentiseconds(Item item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return ClampDelay(item.GetStat(CharacterStat.AttackDelay));
        }

        public static int ResolveDelayCentiseconds(StaticDynel dynel)
        {
            ArgumentNullException.ThrowIfNull(dynel);
            return ClampDelay(dynel.Stats.GetOrZero(CharacterStat.AttackDelay));
        }

        static int ClampDelay(int attackDelay)
            => Math.Clamp(StatCollection.Normalize(attackDelay), 0, MaxDelayCentiseconds);

        /// <summary>True while the player has a use running or queued.</summary>
        public bool HasPending(int characterId)
        {
            lock (_gate)
                return _queues.ContainsKey(characterId);
        }

        /// <summary>True when the player's queue cannot take another use.</summary>
        public bool IsQueueFull(int characterId)
        {
            lock (_gate)
                return _queues.TryGetValue(characterId, out List<PendingItemUse>? queue) && queue.Count >= MaxQueuedUses;
        }

        public ItemUseStart TryBegin(Player player, Identity slot, Item item)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(item);

            // An item worn on the social page is used like any other: its own use requirements and OnUse effects apply.
            if (!ReferenceEquals(player.Playfield, _playfield)
                || IsQueueFull(player.Identity.Instance)
                || _moves.HasPending(player.Identity.Instance))
                return Refused(player, PlayerActionKind.ItemUse, item.LowId, item, BusyRefusal(player), "queue full or busy");

            if (item.IsBackpackUse)
            {
                ModuleHooks.ActionAttempted(player, PlayerActionKind.ItemUse, item.LowId, PlayerActionOutcome.Started, item: item);
                bool used;
                using (ModuleCooldowns.During(PlayerActionKind.ItemUse, item.LowId))
                    used = item.Use(player, slot, _inventoryRepository, _items);
                ModuleHooks.ActionResolved(player, PlayerActionKind.ItemUse, item.LowId,
                    used ? PlayerActionOutcome.Executed : PlayerActionOutcome.Failed,
                    used ? ActionRefusal.None : ActionRefusal.Other, item);
                return used ? ItemUseStart.Executed : ItemUseStart.Rejected;
            }

            if (RejectMissingSpawnHash(player, item.Definition))
                return Refused(player, PlayerActionKind.ItemUse, item.LowId, item, ActionRefusal.Other, "spawn hash not defined");
            if (RejectHeldUniqueSpawn(player, item.Definition))
                return Refused(player, PlayerActionKind.ItemUse, item.LowId, item, ActionRefusal.Requirements, "unique item held");

            player.SyncCharState();
            CanFlags can = UseTargetRules.CanOf(item.Definition);
            Character useTarget = player;
            if ((UseTargetRules.IsHostileTargeted(can) || UseTargetRules.AppliesToFriendly(can))
                && !UseTargetRules.TryResolve(player, can, out useTarget, out UseTargetRules.Failure targetFailure))
            {
                UseTargetRules.SendFailure(player, targetFailure, player.TryResolveFightingTarget());
                return Refused(player, PlayerActionKind.ItemUseOn, item.LowId, item, TargetRefusal(targetFailure), "target rules");
            }

            if (!ReferenceEquals(useTarget, player))
            {
                useTarget.SyncCharState();
            }

            // An item used on someone else is ItemUseOn for modules.
            PlayerActionKind kind = ReferenceEquals(useTarget, player) ? PlayerActionKind.ItemUse : PlayerActionKind.ItemUseOn;

            // A skill the use would lock that is still locked refuses the use outright, before its use time starts: no
            // timer, no success reply, only the "skill is locked" line.
            if (item.Definition.TryFindActiveSkillLock(EventType.OnUse, useTarget, player, DateTime.UtcNow,
                    out int lockedStat, out TimeSpan lockedFor))
            {
                player.SendSkillLocked(lockedStat, lockedFor);
                return Refused(player, kind, item.LowId, item, ActionRefusal.Cooldown, "skill locked", useTarget.Identity,
                    new CooldownKey(CooldownKind.SkillLock, lockedStat), lockedFor);
            }

            if (!item.CanBeginUse(player, useTarget))
            {
                bool noFightingTarget = item.UsesFightingTarget && player.TryResolveFightingTarget() == null;
                if (noFightingTarget)
                    ClientFeedback.Send(player, ClientFeedback.RequiresFightingTarget);
                else
                    RequirementFeedback.SendIfUnmet(player, item.Definition, ActionType.ToUse,
                        resolve: ItemTemplate.WithActionTarget(player.ResolvePerkRequirement, player, useTarget),
                        getTargetStat: stat => useTarget.Stats.Get(stat),
                        resolveTarget: ItemTemplate.WithActionTarget(useTarget.ResolveRequirement, player, useTarget));
                return Refused(player, kind, item.LowId, item,
                    noFightingTarget ? ActionRefusal.Target : ActionRefusal.Requirements, "use requirements", useTarget.Identity);
            }

            if (UseTargetRules.AppliesToFriendly(can)
                && UseTargetRules.CheckFriendlyReach(player, item.Definition, useTarget) is { } reachFailure)
            {
                UseTargetRules.SendFailure(player, reachFailure, useTarget);
                return Refused(player, kind, item.LowId, item, TargetRefusal(reachFailure), "friendly reach", useTarget.Identity);
            }

            // A hostile use aimed at a player, or a friendly one on a PvP-enabled player, flags the user for PvP.
            if (UseTargetRules.IsHostileTargeted(can) && !ReferenceEquals(useTarget, player))
                Helpers.PvpAttackerFlag.OnHostileAction(player, useTarget);
            else if (UseTargetRules.AppliesToFriendly(can) && !ReferenceEquals(useTarget, player))
                Helpers.PvpAttackerFlag.OnFriendlyAction(player, useTarget);

            int instanceId = item.InstanceId;
            var pending = new PendingItemUse(
                player,
                string.Format(CultureInfo.InvariantCulture, "slot={0}:{1} low={2} instanceId={3} target={4}",
                    slot.Type, slot.Instance, item.LowId, instanceId, useTarget.Identity.Instance),
                () => RevalidateInventory(player, slot, item, instanceId)
                    ?? UseTargetRules.Revalidate(player, can, useTarget) switch
                    {
                        null => null,
                        UseTargetRules.Failure.NoFightingTarget => UseTargetRules.LostFightingTarget,
                        { } failure => "target " + failure
                    },
                () => item.ExecuteUse(player, slot, _inventoryRepository, _items, useTarget),
                lockTarget: locked => item.Locked = locked,
                ResolveDelayCentiseconds(item))
            {
                Kind = kind, AbilityId = item.LowId, Item = item, Target = useTarget.Identity, Request = ModuleHooks.RequestNow
            };
            ItemUseStart itemStart = Enqueue(pending);
            // Queuing an item use gives a sneaking user away.
            if (itemStart != ItemUseStart.Rejected)
                player.BreakSneak();
            return itemStart;
        }

        /// <summary>
        /// World item / static dynel Use, queued like an inventory use. Reach and use requirements are checked when it
        /// is asked for; before OnUse runs, only that the object is still there and the user on its playfield.
        /// </summary>
        public ItemUseStart TryBegin(Player player, StaticDynel dynel)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(dynel);

            // Modules see a world object's use as Other: no inventory item, the object as target.
            int templateId = dynel.Template.Id;
            if (!ReferenceEquals(player.Playfield, _playfield)
                || IsQueueFull(player.Identity.Instance)
                || _moves.HasPending(player.Identity.Instance))
                return Refused(player, PlayerActionKind.Other, templateId, null, BusyRefusal(player), "queue full or busy", dynel.Identity);

            if (RejectMissingSpawnHash(player, dynel.Template))
                return Refused(player, PlayerActionKind.Other, templateId, null, ActionRefusal.Other, "spawn hash not defined", dynel.Identity);
            if (RejectHeldUniqueSpawn(player, dynel.Template))
                return Refused(player, PlayerActionKind.Other, templateId, null, ActionRefusal.Requirements, "unique item held", dynel.Identity);

            // As for an item: a skill the use would lock that is still locked refuses it before anything runs (a
            // Stationary Surgery Clinic has no use time, so the effect would otherwise play every click).
            if (dynel.Template.TryFindActiveSkillLock(EventType.OnUse, player, player, DateTime.UtcNow,
                    out int lockedStat, out TimeSpan lockedFor))
            {
                player.SendSkillLocked(lockedStat, lockedFor);
                return Refused(player, PlayerActionKind.Other, templateId, null, ActionRefusal.Cooldown, "skill locked", dynel.Identity,
                    new CooldownKey(CooldownKind.SkillLock, lockedStat), lockedFor);
            }

            if (!dynel.CanBeginUse(player))
            {
                RequirementFeedback.SendIfUnmet(player, dynel.Template, ActionType.ToUse, resolve: dynel.UseResolver(player));
                return Refused(player, PlayerActionKind.Other, templateId, null, ActionRefusal.Requirements, "use requirements", dynel.Identity);
            }

            var pending = new PendingItemUse(
                player,
                string.Format(CultureInfo.InvariantCulture, "dynel={0} template={1}", dynel.Identity, dynel.Template.Id),
                () => RevalidateDynel(player, dynel),
                () => dynel.ExecuteUse(player),
                lockTarget: null,
                ResolveDelayCentiseconds(dynel))
            {
                Kind = PlayerActionKind.Other, AbilityId = templateId, Target = dynel.Identity, Request = ModuleHooks.RequestNow
            };
            return Enqueue(pending);
        }

        /// <summary>
        /// UsePerk (Perk Actions button). Checked in full now (held, unlocked, requirements, target, range, line of sight)
        /// and attack vs defense rating decides at once: a miss is answered straight away (evade feedback and the OnFailure
        /// lock) with no QueuePerk, as in live capture 2026-10-02T13:02:33Z. A landing action is acknowledged straight away
        /// with QueuePerk (CharacterAction 0x50, Parameter1 2, Parameter2 the AttackDelay in centiseconds), even behind
        /// other queued uses, so the client shows it in its queue: live sent three in a row while one was still running
        /// (capture 2026-10-06T15:00:03Z). It is performed when it reaches the front and its delay runs out.
        /// </summary>
        public ItemUseStart TryBeginPerkAction(Player player, int hash)
        {
            ArgumentNullException.ThrowIfNull(player);

            // Module action events use the action's hash as the perk's ability id: it is what the request names.
            if (!ReferenceEquals(player.Playfield, _playfield) || _moves.HasPending(player.Identity.Instance))
                return Refused(player, PlayerActionKind.Perk, hash, null,
                    _moves.HasPending(player.Identity.Instance) ? ActionRefusal.Busy : ActionRefusal.State, "busy");

            if (IsQueued(player.Identity.Instance, hash))
            {
                ClientFeedback.Send(player, ClientFeedback.AlreadyRunningAction);
                return Refused(player, PlayerActionKind.Perk, hash, null, ActionRefusal.Busy, "already queued");
            }

            if (IsQueueFull(player.Identity.Instance))
                return Refused(player, PlayerActionKind.Perk, hash, null, ActionRefusal.QueueFull, "queue full");

            // TryPreparePerkAction raises the module refusal itself: it knows why.
            if (!player.TryPreparePerkAction(hash, out Player.PerkActionUse? use) || use == null)
                return ItemUseStart.Rejected;

            // A perk action gives a sneaking user away when it goes off: a miss now, a landing one once its wind-up ends.
            if (!player.PerkActionLands(use))
            {
                ModuleHooks.ActionAttempted(player, PlayerActionKind.Perk, hash, PlayerActionOutcome.Started, target: use.Target.Identity);
                player.BreakSneak();
                using (ModuleCooldowns.During(PlayerActionKind.Perk, hash))
                    player.FailPerkAction(use, _inventoryRepository);
                ModuleHooks.ActionResolved(player, PlayerActionKind.Perk, hash, PlayerActionOutcome.Missed, target: use.Target.Identity);
                return ItemUseStart.Executed;
            }

            int delay = ClampDelay(use.Template.Stats.GetValueOrDefault(CharacterStat.AttackDelay));
            var pending = new PendingItemUse(
                player,
                string.Format(CultureInfo.InvariantCulture, "perkAction={0:X8} template={1}", hash, use.Template.Id),
                () => player.RevalidatePerkAction(use, starting: false),
                () =>
                {
                    player.BreakSneak();
                    return player.ExecutePerkAction(use, _inventoryRepository);
                },
                lockTarget: null,
                delay,
                perkHash: hash)
            {
                Kind = PlayerActionKind.Perk, AbilityId = hash, Target = use.Target.Identity, Request = ModuleHooks.RequestNow
            };
            ItemUseStart start = Enqueue(pending);
            if (start != ItemUseStart.Rejected)
                player.Session?.Send(new CharacterActionMessage
                {
                    Identity = player.Identity,
                    Action = CharacterActionType.QueuePerk,
                    Target = Identity.None,
                    Parameter1 = 2,
                    Parameter2 = delay
                });
            return start;
        }

        bool IsQueued(int characterId, int perkHash)
        {
            lock (_gate)
                return _queues.TryGetValue(characterId, out List<PendingItemUse>? queue)
                    && queue.Exists(pending => pending.PerkHash == perkHash);
        }

        /// <summary>
        /// A use that would spawn an item or NPC hash the server has no data for is refused before anything runs, with
        /// a chat line naming the hash so the player can report it.
        /// </summary>
        /// <summary>
        /// A use whose SpawnItem can only make a Unique the player already holds is refused before anything runs:
        /// "You already have this unique item: %s".
        /// </summary>
        bool RejectHeldUniqueSpawn(Player player, ItemTemplate template)
        {
            if (!UniqueSpawnCheck.TryFindHeldUniqueOnly(template, player, _playfield.GetRequiredService<IGameData>(),
                    _playfield.GetRequiredService<IItemTemplateCatalog>(), out string name))
                return false;

            ClientFeedback.SendFormatted(player, ClientFeedback.AlreadyHaveUniqueItem, name);
            return true;
        }

        bool RejectMissingSpawnHash(Player player, ItemTemplate template)
        {
            if (!SpawnHashCheck.TryFindMissingHash(template, _playfield.GetRequiredService<IGameData>(), out string hash))
                return false;

            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                "Use blocked char={0} template={1}: spawn hash {2} is not defined", player.Identity.Instance, template.Id, hash));
            Tell(player, SpawnHashCheck.NotImplementedText(hash));
            return true;
        }

        /// <summary>
        /// Adds a checked use to the player's queue. At the front of an empty queue it starts at once: a use with no
        /// delay runs now (Executed or Rejected), anything else is Started.
        /// Behind other uses it waits (Started). Rejected when the queue is full.
        /// </summary>
        ItemUseStart Enqueue(PendingItemUse pending)
        {
            int id = pending.Player.Identity.Instance;
            bool full = false;
            bool waits = false;
            lock (_gate)
            {
                if (!_queues.TryGetValue(id, out List<PendingItemUse>? queue))
                    queue = [];
                if (queue.Count >= MaxQueuedUses)
                    full = true;
                else
                {
                    queue.Add(pending);
                    _queues[id] = queue;
                    pending.SetLocked(true);
                    waits = queue.Count > 1;
                }
            }

            // Module events are raised outside the gate: a handler must not run under it.
            if (full)
            {
                Attempted(pending, PlayerActionOutcome.Refused, ActionRefusal.QueueFull);
                return ItemUseStart.Rejected;
            }

            Attempted(pending, waits ? PlayerActionOutcome.Queued : PlayerActionOutcome.Started);
            if (waits)
                return ItemUseStart.Started;

            ItemUseStart result = StartFront(pending, immediate: true);
            AdvanceQueue(id);
            return result;
        }

        /// <summary>
        /// Starts the use at the front of its queue: Started while its delay runs; otherwise it finishes here and leaves
        /// the queue. <paramref name="immediate"/>: asked for just now with nothing ahead, so its gates were checked a
        /// moment ago and a failure is answered by the caller rather than with <see cref="FailedText"/>.
        /// </summary>
        ItemUseStart StartFront(PendingItemUse pending, bool immediate)
        {
            pending.Started = true;
            if (pending.DelayCentiseconds > 0)
            {
                pending.RemainingSeconds = pending.DelayCentiseconds * 0.01;
                return ItemUseStart.Started;
            }

            RemoveFront(pending);
            if (immediate)
            {
                pending.SetLocked(false);
                int lockNotices = pending.Player.SkillLockNotices;
                bool executed = pending.Execute();
                Resolved(pending, executed, failure: null, lockNotices);
                return executed ? ItemUseStart.Executed : ItemUseStart.Rejected;
            }

            return Complete(pending) ? ItemUseStart.Executed : ItemUseStart.Rejected;
        }

        void RemoveFront(PendingItemUse pending)
        {
            int id = pending.Player.Identity.Instance;
            lock (_gate)
            {
                if (_queues.TryGetValue(id, out List<PendingItemUse>? queue) && queue.Count > 0 && ReferenceEquals(queue[0], pending))
                {
                    queue.RemoveAt(0);
                    if (queue.Count == 0)
                        _queues.Remove(id);
                }
            }
        }

        /// <summary>Starts waiting uses in turn until one is running with a delay or the queue is empty.</summary>
        void AdvanceQueue(int characterId)
        {
            while (true)
            {
                PendingItemUse? next;
                lock (_gate)
                {
                    if (!_queues.TryGetValue(characterId, out List<PendingItemUse>? queue) || queue.Count == 0)
                        return;
                    next = queue[0];
                    if (next.Started)
                        return;
                }

                StartFront(next, immediate: false);
            }
        }

        /// <summary>Drops every running and queued use of the player, unlocking queued items.</summary>
        public void CancelPending(int characterId)
        {
            List<PendingItemUse>? queue;
            lock (_gate)
            {
                if (!_queues.Remove(characterId, out queue))
                    return;
            }

            foreach (PendingItemUse pending in queue)
            {
                pending.SetLocked(false);
                Cancelled(pending, "cancelled");
            }
        }

        public void Tick(double deltaTime)
        {
            if (deltaTime <= 0)
                return;

            List<PendingItemUse> due = [];
            List<PendingItemUse> stale = [];
            lock (_gate)
            {
                if (_queues.Count == 0)
                    return;

                List<int> remove = [];
                foreach (KeyValuePair<int, List<PendingItemUse>> pair in _queues)
                {
                    List<PendingItemUse> queue = pair.Value;
                    if (queue.Count == 0 || !ReferenceEquals(queue[0].Player.Playfield, _playfield))
                    {
                        stale.AddRange(queue);
                        remove.Add(pair.Key);
                        continue;
                    }

                    PendingItemUse front = queue[0];
                    if (!front.Started)
                        continue;

                    front.RemainingSeconds -= deltaTime;
                    if (front.RemainingSeconds > 0)
                        continue;

                    due.Add(front);
                    queue.RemoveAt(0);
                    if (queue.Count == 0)
                        remove.Add(pair.Key);
                }

                foreach (int id in remove)
                    _queues.Remove(id);
            }

            foreach (PendingItemUse pending in stale)
            {
                pending.SetLocked(false);
                Cancelled(pending, "left playfield");
            }

            foreach (PendingItemUse pending in due)
            {
                Complete(pending);
                AdvanceQueue(pending.Player.Identity.Instance);
            }
        }

        bool Complete(PendingItemUse pending)
        {
            // DestroyOne/ConsumeCharge refuse locked items.
            pending.SetLocked(false);

            Player player = pending.Player;
            string? failure = RevalidatePlayer(player) ?? pending.Revalidate();
            int lockNotices = player.SkillLockNotices;
            if (failure == null && pending.Execute())
            {
                Resolved(pending, executed: true, failure: null, lockNotices);
                return true;
            }

            Resolved(pending, executed: false, failure, lockNotices);

            _logger.Warn(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Delayed item use aborted char={0} {1}: {2}",
                    player.Identity.Instance,
                    pending.Description,
                    failure ?? "OnUse spells returned false"));
            if (failure == UseTargetRules.LostFightingTarget)
                ClientFeedback.Send(player, ClientFeedback.RequiresFightingTarget);
            else if (player.SkillLockNotices == lockNotices)
                // A skill lock already told the player when they can use it again.
                Tell(player, FailedText);
            return false;
        }

        string? RevalidatePlayer(Player player)
        {
            if (player.Session == null || player.Session.State != SessionState.InPlay)
                return "session not InPlay";
            if (player.IsDead || player.IsPersistenceQuarantined)
                return "player dead or quarantined";
            if (!ReferenceEquals(player.Playfield, _playfield))
                return "playfield changed";
            return null;
        }

        static string? RevalidateInventory(Player player, Identity slot, Item item, int instanceId)
        {
            if (!player.Inventory.IsHydrated)
                return "inventory not hydrated";
            if (!player.Inventory.TryGetUseItem(slot, out Item current)
                || !ReferenceEquals(current, item)
                || current.InstanceId != instanceId)
                return "slot changed";
            if (current.UsesFightingTarget && player.TryResolveFightingTarget() == null)
                return UseTargetRules.LostFightingTarget;
            if (!current.CanStillUse(player))
                return "use gates failed";
            return null;
        }

        string? RevalidateDynel(Player player, StaticDynel dynel)
        {
            if (!_playfield.GetRequiredService<DynelRegistry>().TryGet(dynel.Identity, out Dynel? current)
                || !ReferenceEquals(current, dynel))
                return "dynel gone";
            if (!dynel.CanStillUse(player))
                return "use gates failed";
            return null;
        }

        // ---- module API 1.6: action events ----

        /// <summary>A request refused before it was queued: raises Attempted Refused and returns Rejected.</summary>
        static ItemUseStart Refused(Player player, PlayerActionKind kind, int abilityId, Item? item, ActionRefusal refusal,
            string detail, Identity target = default, CooldownKey? cooldown = null, TimeSpan readyIn = default)
        {
            ModuleHooks.ActionAttempted(player, kind, abilityId, PlayerActionOutcome.Refused, refusal, item, target, cooldown,
                readyIn, detail);
            return ItemUseStart.Rejected;
        }

        ActionRefusal BusyRefusal(Player player)
            => IsQueueFull(player.Identity.Instance) ? ActionRefusal.QueueFull
                : _moves.HasPending(player.Identity.Instance) ? ActionRefusal.Busy
                : ActionRefusal.State;

        /// <summary>The module refusal (API 1.6) for a use-target failure.</summary>
        internal static ActionRefusal TargetRefusal(UseTargetRules.Failure failure) => failure switch
        {
            UseTargetRules.Failure.OutOfRange => ActionRefusal.Range,
            UseTargetRules.Failure.NoLineOfSight => ActionRefusal.LineOfSight,
            _ => ActionRefusal.Target
        };

        static void Attempted(PendingItemUse pending, PlayerActionOutcome outcome, ActionRefusal refusal = ActionRefusal.None)
            => ModuleHooks.ActionAttempted(pending.Player, pending.Kind, pending.AbilityId, outcome, refusal, pending.Item,
                pending.Target, requestTimestamp: pending.Request);

        static void Cancelled(PendingItemUse pending, string detail)
            => ModuleHooks.ActionResolved(pending.Player, pending.Kind, pending.AbilityId, PlayerActionOutcome.Cancelled,
                item: pending.Item, target: pending.Target, detail: detail, requestTimestamp: pending.Request);

        /// <summary>
        /// A use that reached its turn: Executed, or Failed with why. A failed execute that told the player a skill lock
        /// (<see cref="Character.SkillLockNotices"/> moved) was refused by that lock.
        /// </summary>
        static void Resolved(PendingItemUse pending, bool executed, string? failure, int lockNotices)
        {
            if (executed)
            {
                ModuleHooks.ActionResolved(pending.Player, pending.Kind, pending.AbilityId, PlayerActionOutcome.Executed,
                    item: pending.Item, target: pending.Target, requestTimestamp: pending.Request);
                return;
            }

            ActionRefusal refusal = failure != null ? FailureRefusal(failure)
                : pending.Player.SkillLockNotices != lockNotices ? ActionRefusal.Cooldown
                : ActionRefusal.Other;
            ModuleHooks.ActionResolved(pending.Player, pending.Kind, pending.AbilityId, PlayerActionOutcome.Failed, refusal,
                pending.Item, pending.Target, detail: failure ?? "use effects failed", requestTimestamp: pending.Request);
        }

        /// <summary>The module refusal for a revalidation failure (the strings the Revalidate functions return).</summary>
        static ActionRefusal FailureRefusal(string failure) => failure switch
        {
            "session not InPlay" or "player dead or quarantined" or "playfield changed" or "inventory not hydrated"
                => ActionRefusal.State,
            "slot changed" => ActionRefusal.Resources,
            "use gates failed" => ActionRefusal.Requirements,
            "dynel gone" or UseTargetRules.LostFightingTarget => ActionRefusal.Target,
            // An item's use-target failure comes back as "target " + the UseTargetRules.Failure name.
            "target OutOfRange" => ActionRefusal.Range,
            "target NoLineOfSight" => ActionRefusal.LineOfSight,
            "target NoHostileTarget" or "target NotAttackable" or "target NoFriendlyTarget" => ActionRefusal.Target,
            _ => Player.PerkFailureRefusal(failure)
        };

        static void Tell(Player player, string text)
        {
            player.Session?.Send(
                new ChatTextMessage
                {
                    Identity = player.Identity,
                    Text = text,
                    Unknown1 = 0,
                    Unknown2 = 0,
                    Unknown3 = 0
                });
        }

        /// <summary>
        /// One queued use; its delay starts when it reaches the front. <see cref="Revalidate"/> re-checks the used thing's own safety gates when the delay
        /// ends (null = still valid); the player gates are shared. Only inventory items lock while queued.
        /// </summary>
        sealed class PendingItemUse(
            Player player,
            string description,
            Func<string?> revalidate,
            Func<bool> execute,
            Action<bool>? lockTarget,
            int delayCentiseconds,
            int? perkHash = null)
        {
            public Player Player { get; } = player;

            public string Description { get; } = description;

            public int DelayCentiseconds { get; } = delayCentiseconds;

            /// <summary>The perk action hash, for refusing the same action twice; null for item uses.</summary>
            public int? PerkHash { get; } = perkHash;

            public bool Started { get; set; }

            public double RemainingSeconds { get; set; }

            // Module API 1.6: what the request was, for its action events.
            public PlayerActionKind Kind { get; init; }

            public int AbilityId { get; init; }

            public Item? Item { get; init; }

            public Identity Target { get; init; }

            /// <summary>The receive time of the message that asked for it (<see cref="ModuleHooks.RequestNow"/>).</summary>
            public long Request { get; init; }

            public string? Revalidate() => revalidate();

            /// <summary>Runs the use; a lock it sets names this action as its module source.</summary>
            public bool Execute()
            {
                using (ModuleCooldowns.During(Kind, AbilityId))
                    return execute();
            }

            public void SetLocked(bool locked) => lockTarget?.Invoke(locked);
        }
    }
}
