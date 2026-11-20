namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Diagnostics;
    using System.Globalization;

    using AORebirth.Modules.Events;
    using AORebirth.Modules.World;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Module events, raised from the playfield tick. Without a loaded module the playfield has no
    /// <see cref="ZoneModuleEvents"/>; with no subscriber nothing is built, so a hook costs a service lookup and a field
    /// read. Every event carries <see cref="Stopwatch.GetTimestamp"/> as its Timestamp; one a client message caused carries
    /// <see cref="ModuleRequest.Current"/> as its RequestTimestamp unless the caller passes the request it remembered.
    /// </summary>
    internal static class ModuleHooks
    {
        // ---- 1.2 lifecycle ----

        public static void PlayerEntered(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerEnteredPlayfield.HasHandlers: true } events)
                events.PlayerEnteredPlayfield.Raise(new PlayerPlayfieldEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance, Timestamp = Now
                });
        }

        public static void PlayerLeft(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerLeftPlayfield.HasHandlers: true } events)
                events.PlayerLeftPlayfield.Raise(new PlayerPlayfieldEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance, Timestamp = Now
                });
        }

        public static void PlayerLoggedIn(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerLoggedIn.HasHandlers: true } events)
                events.PlayerLoggedIn.Raise(new PlayerSessionEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance, Timestamp = Now
                });
        }

        public static void PlayerLoggedOut(Playfield playfield, int characterId)
        {
            if (Events(playfield) is { PlayerLoggedOut.HasHandlers: true } events)
                events.PlayerLoggedOut.Raise(new PlayerSessionEvent
                {
                    CharacterId = characterId, PlayfieldId = playfield.Identity.Instance, Timestamp = Now
                });
        }

        public static void NpcSpawned(Playfield playfield, NpcCharacter npc)
        {
            if (Events(playfield) is { NpcSpawned.HasHandlers: true } events)
                events.NpcSpawned.Raise(Spawn(playfield, npc));
        }

        public static void NpcDespawned(Playfield playfield, NpcCharacter npc)
        {
            if (Events(playfield) is { NpcDespawned.HasHandlers: true } events)
                events.NpcDespawned.Raise(Spawn(playfield, npc));
        }

        public static void KnubotChatOpened(Playfield playfield, Player player, NpcCharacter npc, string scriptId)
        {
            if (Events(playfield) is { KnubotChatOpened.HasHandlers: true } events)
                events.KnubotChatOpened.Raise(new KnubotChatOpenedEvent
                {
                    PlayfieldId = playfield.Identity.Instance,
                    CharacterId = player.Identity.Instance,
                    NpcInstance = npc.Identity.Instance,
                    ScriptId = scriptId,
                    Timestamp = Now,
                    RequestTimestamp = ModuleRequest.Current
                });
        }

        // ---- 1.6 B: actions ----

        /// <summary>
        /// The engine decided on a player's request: Started, Queued or Refused. Call once per request, where the engine
        /// decides. <paramref name="requestTimestamp"/>: the request's receive time; -1 takes the message being handled.
        /// </summary>
        public static void ActionAttempted(Player player, PlayerActionKind kind, int abilityId, PlayerActionOutcome outcome,
            ActionRefusal refusal = ActionRefusal.None, Item? item = null, Identity target = default, CooldownKey? cooldown = null,
            TimeSpan readyIn = default, string detail = "", long requestTimestamp = -1)
        {
            // Lock, busy and queue refusals are the normal answer to a held key or button mash: not worth a line each.
            if (outcome == PlayerActionOutcome.Refused
                && refusal is not (ActionRefusal.Cooldown or ActionRefusal.Busy or ActionRefusal.QueueFull))
                Logging.PlayerActionLog.Write(player, "action REFUSED", string.Format(CultureInfo.InvariantCulture,
                    "kind={0} id={1} reason={2} item={3} target={4} detail=\"{5}\"",
                    kind, abilityId, refusal, item?.LowId ?? 0, target.Instance, detail));
            if (player.Playfield is Playfield playfield && Events(playfield) is { PlayerActionAttempted.HasHandlers: true } events)
                events.PlayerActionAttempted.Raise(Action(playfield, player, kind, abilityId, outcome, refusal, item, target,
                    cooldown, readyIn, detail, requestTimestamp));
        }

        /// <summary>
        /// A Started or Queued request ended. <paramref name="requestTimestamp"/>: the receive time remembered from the
        /// request (pass what <see cref="RequestNow"/> gave when it was accepted); -1 takes the message being handled.
        /// </summary>
        public static void ActionResolved(Player player, PlayerActionKind kind, int abilityId, PlayerActionOutcome outcome,
            ActionRefusal refusal = ActionRefusal.None, Item? item = null, Identity target = default, CooldownKey? cooldown = null,
            TimeSpan readyIn = default, string detail = "", long requestTimestamp = -1)
        {
            if (player.Playfield is Playfield playfield && Events(playfield) is { PlayerActionResolved.HasHandlers: true } events)
                events.PlayerActionResolved.Raise(Action(playfield, player, kind, abilityId, outcome, refusal, item, target,
                    cooldown, readyIn, detail, requestTimestamp));
        }

        /// <summary>The receive time of the message being handled, to keep with a queued or started action for its Resolved.</summary>
        public static long RequestNow => ModuleRequest.Current;

        // ---- 1.6 C: cooldowns ----

        /// <summary>A lock was set on a player. Restored locks (login, zone change) do not call this.</summary>
        public static void CooldownStarted(Character character, CooldownKind kind, int id, TimeSpan duration,
            PlayerActionKind sourceKind = PlayerActionKind.Other, int sourceId = 0, TimeSpan? clientDuration = null)
        {
            if (character is Player player && player.Playfield is Playfield playfield
                && Events(playfield) is { CooldownStarted.HasHandlers: true } events)
                events.CooldownStarted.Raise(new CooldownEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    Key = new CooldownKey(kind, id),
                    Duration = duration,
                    Deadline = ModuleClock.After(duration),
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    ClientDuration = clientDuration
                });
        }

        /// <summary>
        /// A lock ran out or was cleared. <paramref name="deadline"/>: when it was due to end, on the module clock (use
        /// <see cref="ModuleClock.At"/> for a DateTime deadline).
        /// </summary>
        public static void CooldownEnded(Character character, CooldownKind kind, int id, TimeSpan duration, long deadline,
            CooldownEndReason reason, bool clientNotified)
        {
            if (character is Player player && player.Playfield is Playfield playfield
                && Events(playfield) is { CooldownEnded.HasHandlers: true } events)
                events.CooldownEnded.Raise(new CooldownEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    Key = new CooldownKey(kind, id),
                    Duration = duration,
                    Deadline = deadline,
                    Reason = reason,
                    ClientNotified = clientNotified
                });
        }

        // ---- 1.6 E: movement ----

        public static void MovementCorrected(Character character, Vector3 reported, Vector3 kept, MovementCorrection reason,
            Movement.ClientMoveCheck check = default, int refusedMoves = 0)
        {
            if (character is Player player && player.Playfield is Playfield playfield
                && Events(playfield) is { PlayerMovementCorrected.HasHandlers: true } events)
                events.PlayerMovementCorrected.Raise(new PlayerMovementCorrectedEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    RequestTimestamp = ModuleRequest.Current,
                    Reported = Position(reported),
                    Kept = Position(kept),
                    Reason = reason,
                    Distance = check.Distance,
                    Allowed = check.Allowed,
                    MaxSpeed = check.MaxSpeed,
                    SinceLastAccepted = check.SinceLastAccepted,
                    RefusedMoves = refusedMoves
                });
        }

        // ---- 1.6 F: items ----

        /// <summary>
        /// An item changed place for <paramref name="player"/>. Not for moves between slots of one character's own
        /// inventory. <paramref name="count"/> 0 takes the item's stack count. <paramref name="source"/> (1.17): who was on
        /// the other side when the caller knows it; a GM command's or module operation's source wins over it, and without
        /// one the scope's source, else the Corpse, Shop, Trade or World place's owner, is used.
        /// </summary>
        public static void ItemMoved(Player player, Item item, ItemPlace from, ItemPlace to, int count = 0,
            StatChangeSource source = default)
        {
            if (player.Playfield is Playfield playfield && Events(playfield) is { ItemMoved.HasHandlers: true } events)
            {
                StatChangeReason reason = StatChangeScope.Current;
                if (reason is StatChangeReason.Gm or StatChangeReason.Module || source.IsEmpty)
                    source = StatChangeScope.CurrentSource;
                if (source.IsEmpty)
                    source = PlaceSource(playfield, from, to);
                events.ItemMoved.Raise(new ItemMovedEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    RequestTimestamp = ModuleRequest.Current,
                    Item = ItemRefOf(item),
                    Count = count > 0 ? count : Math.Max(1, item.StackCount),
                    From = from,
                    To = to,
                    Reason = reason,
                    Source = source
                });
            }
        }

        /// <summary>The owner of the first of <paramref name="from"/>, <paramref name="to"/> that is someone else's place, described.</summary>
        static StatChangeSource PlaceSource(Playfield playfield, ItemPlace from, ItemPlace to)
        {
            EntityRef owner = IsOthersPlace(from) ? from.Owner : IsOthersPlace(to) ? to.Owner : default;
            if (owner == default)
                return default;
            var identity = new Identity { Type = (IdentityType)owner.Type, Instance = owner.Instance };
            return playfield.GetService<DynelRegistry>() is { } registry && registry.TryGet(identity, out Dynel? dynel) && dynel != null
                ? SourceOf(dynel)
                : new StatChangeSource { Entity = owner };
        }

        static bool IsOthersPlace(ItemPlace place)
            => place.Owner != default
                && place.Kind is ItemPlaceKind.Corpse or ItemPlaceKind.Shop or ItemPlaceKind.Trade or ItemPlaceKind.World;

        /// <summary>Whether anyone listens for item moves on this playfield, to skip building places on a busy path.</summary>
        public static bool WantsItemMoves(Playfield? playfield)
            => playfield != null && Events(playfield) is { ItemMoved.HasHandlers: true };

        public static ItemPlace Place(ItemPlaceKind kind) => new(kind, default);

        public static ItemPlace Place(ItemPlaceKind kind, Identity owner)
            => new(kind, owner.Instance == 0 ? default : new EntityRef((int)owner.Type, owner.Instance));

        // ---- 1.6 G: chat ----

        public static void PlayerSpoke(Player player, ChatChannelKind channel, string? text)
        {
            if (player.Playfield is Playfield playfield && Events(playfield) is { PlayerSpoke.HasHandlers: true } events)
                events.PlayerSpoke.Raise(new PlayerSpokeEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    RequestTimestamp = ModuleRequest.Current,
                    Channel = channel,
                    Length = text?.Length ?? 0,
                    // Stripped for a module without read-chat-text (ZoneModuleHost's events view).
                    Text = text ?? string.Empty
                });
        }

        // ---- 1.6 H: combat ----

        /// <summary>
        /// A hit or miss was resolved. Raised only when a player is the attacker or the target (a pet counts as its
        /// owner's side but not as a player). <paramref name="damageType"/>: the AC stat (90-97), or 0.
        /// </summary>
        public static void CombatResolved(Character attacker, Character target, CombatSource source, int sourceId,
            CombatResult result, int amount, int damageType)
        {
            if (!attacker.IsPlayer && !target.IsPlayer)
                return;
            Playfield? playfield = target.Playfield ?? attacker.Playfield;
            if (playfield != null && Events(playfield) is { CombatResolved.HasHandlers: true } events)
                events.CombatResolved.Raise(new CombatResolvedEvent
                {
                    PlayfieldId = playfield.Identity.Instance,
                    Timestamp = Now,
                    Attacker = Entity(attacker.Identity),
                    Target = Entity(target.Identity),
                    AttackerOwner = PetOwner(attacker),
                    TargetOwner = PetOwner(target),
                    Source = source,
                    SourceId = sourceId,
                    Result = result,
                    Amount = Math.Max(0, amount),
                    DamageType = damageType
                });
        }

        // ---- 1.6 D: visibility ----

        /// <summary>
        /// <paramref name="viewer"/>'s client was told <paramref name="entity"/> exists (<paramref name="visible"/>) or is
        /// gone. Call where the spawn or despawn message is queued to that client.
        /// </summary>
        public static void Visibility(Playfield playfield, Player viewer, Dynel entity, bool visible, VisibilityReason reason)
        {
            if (Events(playfield) is { Visibility.HasObservers: true } events)
                events.Visibility.Publish(viewer.Identity.Instance, playfield.Identity.Instance, Entity(entity.Identity),
                    entity is NpcCharacter, visible, reason);
        }

        /// <summary>Whether anyone observes visibility on this playfield, to skip work on a busy path.</summary>
        public static bool WantsVisibility(Playfield? playfield)
            => playfield != null && Events(playfield) is { Visibility.HasObservers: true };

        // ---- 1.12: missions ----

        /// <summary>
        /// <paramref name="player"/> completed <paramref name="quest"/> and its reward is committed. Call after the
        /// engine's own payout, on the player's tick.
        /// </summary>
        public static void MissionCompleted(Playfield playfield, Player player, Quests.PlayerQuest quest, Quests.QuestTemplate template,
            Quests.QuestService.MissionShare? share, int credits, int experience, bool? rewardWon)
        {
            if (Events(playfield) is { MissionCompleted.HasHandlers: true } events)
                events.MissionCompleted.Raise(new MissionCompletedEvent
                {
                    CharacterId = player.Identity.Instance,
                    PlayfieldId = playfield.Identity.Instance,
                    QuestId = quest.QuestId,
                    Generated = quest.Source == Quests.QuestSource.Generated,
                    // The stored copy of a mission drops its Dungeon block; the accepted dungeon's parameters keep the name.
                    Mission = share?.Mission is { Length: > 0 } mission ? mission : template.Dungeon?.Mission ?? string.Empty,
                    Title = template.Title ?? template.Name,
                    Quality = share?.Quality ?? template.Dungeon?.Quality ?? 0,
                    Killed = share?.Killed ?? 0,
                    Total = share?.Total ?? 0,
                    Credits = Math.Max(0, credits),
                    Experience = Math.Max(0, experience),
                    RewardWon = rewardWon,
                    Timestamp = Now
                });
        }

        // ---- 1.16: audited stats ----

        /// <summary>The stats <see cref="PlayerStatChanged"/> reports: credits, experience, level, alien experience, alien level.</summary>
        public static bool IsAuditedStat(CharacterStat stat)
            => stat is CharacterStat.Cash or CharacterStat.XP or CharacterStat.Level
                or CharacterStat.AlienXP or CharacterStat.AlienLevel;

        /// <summary>
        /// The stored value of an audited stat of <paramref name="player"/> changed. Loading a character (before the
        /// server puts it in play) raises nothing. The reason is <see cref="StatChangeScope.Current"/> and the source
        /// <see cref="StatChangeScope.CurrentSource"/>.
        /// </summary>
        public static void PlayerStatChanged(Player player, CharacterStat stat, int before, int after)
        {
            if (player.InPlaySinceUtc == default || player.Playfield is not Playfield playfield
                || Events(playfield) is not { PlayerStatChanged.HasHandlers: true } events)
                return;
            before = StatCollection.Normalize(before);
            after = StatCollection.Normalize(after);
            if (before == after)
                return;
            events.PlayerStatChanged.Raise(new PlayerStatChangedEvent
            {
                CharacterId = player.Identity.Instance,
                PlayfieldId = playfield.Identity.Instance,
                Timestamp = Now,
                RequestTimestamp = ModuleRequest.Current,
                Stat = (int)stat,
                Before = before,
                After = after,
                Reason = StatChangeScope.Current,
                Source = StatChangeScope.CurrentSource
            });
        }

        // ---- 1.17: audit sources ----

        /// <summary>
        /// <paramref name="dynel"/> as a <see cref="StatChangeSource"/>: a corpse as who died, a vending terminal as
        /// <see cref="ShopSource"/>, an NPC with its template hash, a character with its name and level, a terminal with its
        /// item template, a door with its lock difficulty. Empty for null.
        /// </summary>
        public static StatChangeSource SourceOf(Dynel? dynel) => dynel switch
        {
            null => default,
            Corpse corpse => corpse.AuditSource,
            VendingMachine machine => ShopSource(machine),
            NpcCharacter npc => new StatChangeSource
            {
                Entity = Entity(npc.Identity),
                Hash = npc.MobTemplate?.Hash ?? npc.SpawnHash ?? string.Empty,
                Name = npc.Name ?? string.Empty,
                Level = npc.Stats.GetOrZero(CharacterStat.Level)
            },
            Character character => new StatChangeSource
            {
                Entity = Entity(character.Identity),
                Name = character.Name ?? string.Empty,
                Level = character.Stats.GetOrZero(CharacterStat.Level)
            },
            StaticDynel terminal => new StatChangeSource
            {
                Entity = Entity(terminal.Identity),
                TemplateId = terminal.ItemTemplateLowId,
                Name = terminal.RuntimeName ?? terminal.Template.Name ?? string.Empty
            },
            Door door => new StatChangeSource { Entity = Entity(door.Identity), Level = door.LockDifficulty },
            _ => new StatChangeSource { Entity = Entity(dynel.Identity) }
        };

        /// <summary>
        /// A shop as a source: the NPC that runs it, or the vending terminal itself; with the terminal's item template id
        /// and its vendor id.
        /// </summary>
        public static StatChangeSource ShopSource(VendingMachine machine)
        {
            StatChangeSource vendor = machine.OwnerNpc is NpcCharacter owner
                ? SourceOf(owner)
                : new StatChangeSource
                {
                    Entity = Entity(machine.Identity),
                    Hash = machine.DatabaseVendorTemplateHash,
                    Name = machine.RuntimeName ?? machine.Template.Name ?? string.Empty
                };
            return vendor with { TemplateId = machine.ItemTemplateLowId, DetailId = machine.DatabaseVendorId };
        }

        // ---- 1.16: GM commands ----

        /// <summary>
        /// <paramref name="player"/> ran <paramref name="command"/>. Call after it ran without throwing, with
        /// the playfield it was issued on (a teleport or logout moves the player away).
        /// </summary>
        public static void GmCommandUsed(Playfield? issuedOn, Player player, string command, string[] args, int gmLevel, int requiredGmLevel,
            GmCommandOutcome outcome)
        {
            if ((issuedOn ?? player.Playfield) is not Playfield playfield || Events(playfield) is not { GmCommandUsed.HasHandlers: true } events)
                return;
            Identity target = player.Target;
            events.GmCommandUsed.Raise(new GmCommandUsedEvent
            {
                CharacterId = player.Identity.Instance,
                PlayfieldId = playfield.Identity.Instance,
                Timestamp = Now,
                RequestTimestamp = ModuleRequest.Current,
                Command = command,
                // A copy: the dispatcher's array is the command's own.
                Args = args.Length == 0 ? Array.Empty<string>() : (string[])args.Clone(),
                GmLevel = gmLevel,
                RequiredGmLevel = requiredGmLevel,
                Target = target == player.Identity ? default : Entity(target),
                Outcome = outcome
            });
        }

        // ---- helpers ----

        /// <summary>The player owning <paramref name="character"/> when it is a pet; empty otherwise.</summary>
        static EntityRef PetOwner(Character character)
            => character is NpcCharacter { PetOwner: Player owner } ? Entity(owner.Identity) : default;

        public static ItemRef ItemRefOf(Item? item) => item == null ? default : new ItemRef(item.LowId, item.HighId, item.Quality);

        public static EntityRef Entity(Identity identity)
            => identity.Instance == 0 ? default : new EntityRef((int)identity.Type, identity.Instance);

        /// <summary>A spawn point's id for <see cref="NpcSpawnEvent.SpawnerId"/>: stable for the point across respawns and restarts.</summary>
        public static int SpawnerId(string? spawnPointKey)
        {
            if (string.IsNullOrEmpty(spawnPointKey))
                return 0;
            uint hash = 2166136261;
            foreach (char c in spawnPointKey)
                hash = (hash ^ c) * 16777619;
            int id = (int)(hash & 0x7FFFFFFF);
            return id == 0 ? 1 : id;
        }

        static long Now => Stopwatch.GetTimestamp();

        static ZoneModuleEvents? Events(Playfield playfield) => playfield.GetService<ZoneModuleEvents>();

        static WorldPosition Position(Vector3 at) => new(at.xf, at.yf, at.zf);

        static PlayerActionEvent Action(Playfield playfield, Player player, PlayerActionKind kind, int abilityId,
            PlayerActionOutcome outcome, ActionRefusal refusal, Item? item, Identity target, CooldownKey? cooldown,
            TimeSpan readyIn, string detail, long requestTimestamp) => new()
        {
            CharacterId = player.Identity.Instance,
            PlayfieldId = playfield.Identity.Instance,
            Timestamp = Now,
            RequestTimestamp = requestTimestamp >= 0 ? requestTimestamp : ModuleRequest.Current,
            Kind = kind,
            AbilityId = abilityId,
            Item = ItemRefOf(item),
            Target = Entity(target),
            Outcome = outcome,
            Refusal = refusal,
            Cooldown = cooldown,
            ReadyIn = readyIn < TimeSpan.Zero ? TimeSpan.Zero : readyIn,
            Detail = detail ?? string.Empty
        };

        static NpcSpawnEvent Spawn(Playfield playfield, NpcCharacter npc) => new()
        {
            PlayfieldId = playfield.Identity.Instance,
            NpcInstance = npc.Identity.Instance,
            NpcHash = npc.MobTemplate?.Hash ?? string.Empty,
            Timestamp = Now,
            Position = Position(npc.Position),
            Level = npc.Stats.GetOrZero(CharacterStat.Level),
            SpawnerId = SpawnerId(npc.SpawnPointKey)
        };
    }
}
