namespace ZoneEngine_New.Core.Loot
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading;

    using AORebirth.Modules.Events;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Chat;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Teams;

    /// <summary>
    /// Need or Greed team loot for one playfield. When the corpse of an NPC a team in <see cref="TeamLootMode.NeedOrGreed"/>
    /// won drops, every item in it except Trash rarity is held (locked: nobody can take it from the corpse) and rolled for by the members on
    /// this playfield within Gameplay.json loot.needGreedRange of the corpse. Each answers once: Need, Greed or Pass
    /// (<see cref="LootRollChoiceMessage"/>, or /loot need|greed|pass [roll] from the stock client). The roll closes when
    /// all have answered or loot.needGreedRollSeconds pass (silence passes). Need beats Greed, the higher 1-100 roll wins,
    /// and the server puts the item straight into the winner's inventory; when nobody wants it, or no roller can take it
    /// (gone, inventory full, already holds the Unique), it is released in the corpse as ordinary loot. Credits are not
    /// rolled. Everything runs on the playfield tick.
    /// </summary>
    public sealed class LootRollService
    {
        /// <summary>Most items one corpse puts up for rolls; the rest stay ordinary loot.</summary>
        const int MaxRollsPerCorpse = 24;

        /// <summary>Rarity (stat 688) value the client names "Trash" (text category 2016, entry 1).</summary>
        const int TrashRarity = 1;

        /// <summary>Trash-rarity items (Battered Low-Tech Armor Pants) are never rolled; they stay ordinary loot.</summary>
        static bool IsTrash(Item item)
            => item.Definition.Stats.TryGetValue(CharacterStat.Rarity, out int rarity) && rarity == TrashRarity;

        /// <summary>Process-wide, so a roll id names one roll whichever playfield it is on.</summary>
        static int _nextRollId;

        sealed class Roll
        {
            public required int Id { get; init; }
            public required Corpse Corpse { get; init; }
            public required Item Item { get; init; }
            public required string ItemName { get; init; }
            public required DateTime ClosesAtUtc { get; init; }
            /// <summary>The members who may answer, in team order.</summary>
            public required List<int> Rollers { get; init; }
            public Dictionary<int, (LootRollChoice Choice, int Value)> Answers { get; } = new();
        }

        readonly ZoneEngine_New.Core.Playfield.Playfield _playfield;
        readonly DynelRegistry _registry;
        readonly InventoryFlushService _flush;
        readonly IZoneLogger _logger;
        readonly Dictionary<int, Roll> _rolls = new();
        readonly List<Roll> _due = new();

        public LootRollService(ZoneEngine_New.Core.Playfield.Playfield playfield, DynelRegistry registry, InventoryFlushService flush, IZoneLogger logger)
        {
            _playfield = playfield;
            _registry = registry;
            _flush = flush;
            _logger = logger;
        }

        Configuration.GameplayLoot Settings => _playfield.GetService<Configuration.GameplaySettings>()?.Loot ?? new Configuration.GameplayLoot();

        /// <summary>
        /// A corpse was registered with its loot resolved. When its loot winner's team rolls for loot and at least two
        /// members are there to roll, its items go up for rolls.
        /// </summary>
        public void OnCorpseSpawned(Corpse corpse)
        {
            ArgumentNullException.ThrowIfNull(corpse);
            if (corpse.LootWinner == Identity.None || corpse.Loot.Content.Count == 0
                || !_registry.TryGet(corpse.LootWinner, out Dynel? winnerDynel) || winnerDynel is not Player winner
                || _playfield.GetService<TeamService>()?.GetTeam(winner) is not { LootMode: TeamLootMode.NeedOrGreed } team)
                return;

            Configuration.GameplayLoot settings = Settings;
            var rollers = new List<int>(team.MemberIds.Count);
            var present = new List<Player>(team.MemberIds.Count);
            foreach (int memberId in team.MemberIds)
            {
                if (_registry.TryGet(new Identity { Type = IdentityType.SimpleChar, Instance = memberId }, out Dynel? dynel)
                    && dynel is Player member && ReferenceEquals(member.Playfield, _playfield) && member.Session != null
                    && member.GetEdgeDistanceTo(corpse) <= settings.NeedGreedRange)
                {
                    rollers.Add(memberId);
                    present.Add(member);
                }
            }
            // Alone there is nobody to roll against: the winner loots as usual.
            if (rollers.Count < 2)
                return;

            DateTime closes = DateTime.UtcNow.AddSeconds(settings.NeedGreedRollSeconds);
            int opened = 0;
            foreach (Item item in corpse.Loot.Content.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray())
            {
                if (opened == MaxRollsPerCorpse)
                    break;
                if (item.Locked || IsTrash(item))
                    continue;
                item.Locked = true;
                var roll = new Roll
                {
                    Id = Interlocked.Increment(ref _nextRollId) & int.MaxValue,
                    Corpse = corpse,
                    Item = item,
                    ItemName = string.IsNullOrEmpty(item.Name) ? "an item" : item.Name,
                    ClosesAtUtc = closes,
                    Rollers = rollers
                };
                _rolls[roll.Id] = roll;
                opened++;

                foreach (Player member in present)
                    if (member.SpeaksExtendedMessages)
                        member.Session?.Send(new LootRollStartMessage
                    {
                        Identity = member.Identity,
                        RollId = roll.Id,
                        Corpse = corpse.Identity,
                        LowId = item.LowId,
                        HighId = item.HighId,
                        Quality = item.Quality,
                        Count = item.StackCount,
                        Seconds = settings.NeedGreedRollSeconds,
                        ItemName = roll.ItemName
                    });
                Tell(roll.Rollers, string.Format(CultureInfo.InvariantCulture,
                    "Roll {0}: {1} (QL {2}). /loot need {0}, /loot greed {0} or /loot pass {0} within {3} seconds.",
                    roll.Id, roll.ItemName, item.Quality, settings.NeedGreedRollSeconds));
            }

            if (opened > 0)
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Need or Greed corpse={0} team={1} rolls={2} rollers={3}", corpse.Identity.Instance, team.TeamId, opened,
                    string.Join(",", rollers)));
        }

        /// <summary>
        /// /loot need|greed|pass [roll id] (the stock client sends it as a chat command). Without an id it answers the
        /// player's oldest open roll they have not answered.
        /// </summary>
        public void HandleCommand(Player player, string[] args)
        {
            ArgumentNullException.ThrowIfNull(player);
            LootRollChoice? choice = args.Length >= 2 ? args[1].ToLowerInvariant() switch
            {
                "need" => LootRollChoice.Need,
                "greed" => LootRollChoice.Greed,
                "pass" => LootRollChoice.Pass,
                _ => null
            } : null;
            if (choice == null || args.Length > 3)
            {
                RequirementFeedback.SendText(player, "Usage: /loot need [roll], /loot greed [roll] or /loot pass [roll].");
                return;
            }

            int rollId;
            if (args.Length == 3)
            {
                if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out rollId))
                {
                    RequirementFeedback.SendText(player, "That is not a roll number.");
                    return;
                }
            }
            else
            {
                int self = player.Identity.Instance;
                Roll? oldest = _rolls.Values.Where(r => r.Rollers.Contains(self) && !r.Answers.ContainsKey(self))
                    .OrderBy(r => r.Id).FirstOrDefault();
                if (oldest == null)
                {
                    RequirementFeedback.SendText(player, "You have no open loot roll.");
                    return;
                }
                rollId = oldest.Id;
            }

            Answer(player, rollId, choice.Value);
        }

        /// <summary>A member's answer (<see cref="LootRollChoiceMessage"/>); only their first answer to a roll counts.</summary>
        public void Answer(Player player, int rollId, LootRollChoice choice)
        {
            ArgumentNullException.ThrowIfNull(player);
            int self = player.Identity.Instance;
            if (choice is not (LootRollChoice.Pass or LootRollChoice.Need or LootRollChoice.Greed)
                || !_rolls.TryGetValue(rollId, out Roll? roll) || !roll.Rollers.Contains(self))
            {
                RequirementFeedback.SendText(player, "That loot roll is not open to you.");
                return;
            }
            if (roll.Answers.ContainsKey(self))
            {
                RequirementFeedback.SendText(player, "You already answered that roll.");
                return;
            }

            int value = choice == LootRollChoice.Pass ? 0 : RandomNumberGenerator.GetInt32(1, 101);
            roll.Answers[self] = (choice, value);
            Tell(roll.Rollers, choice == LootRollChoice.Pass
                ? string.Format(CultureInfo.InvariantCulture, "{0} passed on {1} (roll {2}).", player.Name, roll.ItemName, roll.Id)
                : string.Format(CultureInfo.InvariantCulture, "{0} selected {1} for {2} (roll {3}).", player.Name,
                    choice == LootRollChoice.Need ? "Need" : "Greed", roll.ItemName, roll.Id));

            if (roll.Answers.Count >= roll.Rollers.Count)
                Close(roll);
        }

        /// <summary>Closes rolls whose time ran out or whose corpse is gone.</summary>
        public void Tick()
        {
            if (_rolls.Count == 0)
                return;
            DateTime now = DateTime.UtcNow;
            _due.Clear();
            foreach (Roll roll in _rolls.Values)
            {
                if (now >= roll.ClosesAtUtc || !ReferenceEquals(roll.Corpse.Playfield, _playfield))
                    _due.Add(roll);
            }
            foreach (Roll roll in _due)
                Close(roll);
            _due.Clear();
        }

        void Close(Roll roll)
        {
            if (!_rolls.Remove(roll.Id))
                return;

            bool inCorpse = ReferenceEquals(roll.Corpse.Playfield, _playfield) && TryFindSlot(roll.Corpse, roll.Item, out _);
            var entries = roll.Rollers
                .Select(id => roll.Answers.TryGetValue(id, out var answer)
                    ? new LootRollEntry(id, answer.Choice, answer.Value)
                    : new LootRollEntry(id, LootRollChoice.Pass, 0))
                .ToList();

            Player? winner = null;
            LootRollEntry won = default;
            if (inCorpse)
            {
                // Need before Greed, then the higher roll; equal rolls go to whoever answered first.
                var ranked = entries.Where(e => e.Choice != LootRollChoice.Pass)
                    .OrderBy(e => e.Choice == LootRollChoice.Need ? 0 : 1)
                    .ThenByDescending(e => e.Roll)
                    .ThenBy(e => AnswerOrder(roll, e.CharacterId));
                foreach (LootRollEntry candidate in ranked)
                {
                    if (TryAward(roll, candidate.CharacterId, out Player? receiver))
                    {
                        winner = receiver;
                        won = candidate;
                        break;
                    }
                }
            }

            string text;
            if (!inCorpse)
                text = string.Format(CultureInfo.InvariantCulture, "Roll {0} for {1} ended: the remains are gone.", roll.Id, roll.ItemName);
            else if (winner != null)
                text = string.Format(CultureInfo.InvariantCulture, "{0} won {1} with {2} {3} (roll {4}).", winner.Name, roll.ItemName,
                    won.Choice == LootRollChoice.Need ? "Need" : "Greed", won.Roll, roll.Id);
            else
            {
                // Nobody wanted it, or nobody who wanted it could take it: it is ordinary loot in the corpse now.
                roll.Item.Locked = false;
                text = string.Format(CultureInfo.InvariantCulture, "Nobody won {0} (roll {1}); it is free to loot.", roll.ItemName, roll.Id);
            }

            var result = new LootRollResultMessage
            {
                RollId = roll.Id,
                Winner = winner?.Identity.Instance ?? 0,
                WinnerChoice = winner != null ? won.Choice : LootRollChoice.Pass,
                WinnerRoll = winner != null ? won.Roll : 0,
                WinnerName = winner?.Name ?? string.Empty,
                Entries = entries
            };
            foreach (int id in roll.Rollers)
            {
                if (TryGetLocalPlayer(id, out Player member) && member.SpeaksExtendedMessages)
                {
                    result.Identity = member.Identity;
                    member.Session?.Send(result);
                }
            }
            Tell(roll.Rollers, text);
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Need or Greed roll={0} corpse={1} item={2} winner={3} answers={4}", roll.Id, roll.Corpse.Identity.Instance,
                roll.Item.LowId, winner?.Identity.Instance ?? 0,
                string.Join(",", entries.Select(e => e.CharacterId + ":" + e.Choice + ":" + e.Roll))));
        }

        static int AnswerOrder(Roll roll, int characterId)
        {
            int order = 0;
            foreach (int id in roll.Answers.Keys)
            {
                if (id == characterId)
                    return order;
                order++;
            }
            return int.MaxValue;
        }

        /// <summary>
        /// Moves the rolled item out of the corpse into the winner's inventory: onto a stack it joins, else a free
        /// inventory slot (never overflow, which would trim). False leaves it in the corpse, locked, for the next roller.
        /// </summary>
        bool TryAward(Roll roll, int characterId, out Player receiver)
        {
            receiver = null!;
            if (!TryGetLocalPlayer(characterId, out Player player) || player.Session == null || player.IsPersistenceQuarantined
                || !player.Inventory.IsHydrated)
                return false;
            Item item = roll.Item;
            if (UniqueItems.WouldDuplicate(player, item, out string uniqueName))
            {
                ClientFeedback.SendFormatted(player, ClientFeedback.AlreadyHaveUniqueItem, uniqueName);
                return false;
            }
            bool auto = AutoConsumedItems.IsAutoConsumed(item);
            if (!auto && player.Inventory.Inventory.FindFreeSlot() < 0)
            {
                RequirementFeedback.SendText(player, "Your inventory is full: you could not take " + roll.ItemName + ".");
                return false;
            }
            Corpse corpse = roll.Corpse;
            if (!TryFindSlot(corpse, item, out int slot))
                return false;

            // The opener's loot window would still show the item: it closes, and they open the remains again.
            if (corpse.IsOpen)
                corpse.Close();
            corpse.Loot.Content.Remove(slot);
            item.Locked = false;
            corpse.NotifyLootChanged();

            ItemPlace from = ItemPlaces.Of(corpse.Loot, corpse);
            if (AutoConsumedItems.TryConsume(player, item))
            {
                ModuleHooks.ItemMoved(player, item, from, ModuleHooks.Place(ItemPlaceKind.Destroyed));
            }
            else if (!InventoryStacking.TryGrantOntoStack(player, item, _flush))
            {
                int free = player.Inventory.Inventory.FindFreeSlot();
                if (free < 0 || !player.Inventory.Inventory.Add(free, item))
                {
                    // Cannot happen after the check above on one tick; put it back rather than lose it.
                    corpse.Loot.Add(slot, item);
                    item.Locked = true;
                    return false;
                }
                player.Inventory.MarkDirty(item, player.Inventory.Inventory, free);
                _flush.NotifyDirty(player);
                ModuleHooks.ItemMoved(player, item, from, ModuleHooks.Place(ItemPlaceKind.Inventory));
                player.Session?.Send(new AddTemplateMessage
                {
                    Identity = player.Identity,
                    HighId = item.HighId,
                    LowId = item.LowId,
                    Quality = item.Quality,
                    Count = item.StackCount
                });
            }
            player.InvalidateOwnedItemCache();
            _playfield.GetService<VicinityChatRelay>()?.AnnounceLoot(player, corpse, item);
            receiver = player;
            return true;
        }

        static bool TryFindSlot(Corpse corpse, Item item, out int slot)
        {
            foreach ((int key, Item held) in corpse.Loot.Content)
            {
                if (ReferenceEquals(held, item))
                {
                    slot = key;
                    return true;
                }
            }
            slot = -1;
            return false;
        }

        bool TryGetLocalPlayer(int characterId, out Player player)
        {
            if (_registry.TryGet(new Identity { Type = IdentityType.SimpleChar, Instance = characterId }, out Dynel? dynel)
                && dynel is Player found && ReferenceEquals(found.Playfield, _playfield))
            {
                player = found;
                return true;
            }
            player = null!;
            return false;
        }

        /// <summary>
        /// One line to the rollers in the client's Team Loot tab: from the chat server, reaching them wherever they are;
        /// zone feedback to those on this playfield when it cannot be reached.
        /// </summary>
        void Tell(IReadOnlyList<int> rollers, string text)
        {
            if (_playfield.GetService<VicinityChatRelay>()?.Announce(text, rollers, VicinityChatRelay.TeamLootBlob) == true)
                return;
            foreach (int id in rollers)
            {
                if (TryGetLocalPlayer(id, out Player member))
                    RequirementFeedback.SendText(member, text);
            }
        }
    }
}
