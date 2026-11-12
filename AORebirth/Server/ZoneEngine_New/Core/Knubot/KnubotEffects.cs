namespace ZoneEngine_New.Core.Knubot;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AORebirth.Enums;
using AORebirth.Modules.Events;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Modules;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Trade;

/// <summary>Services a line's effects are applied through.</summary>
public sealed class KnubotEffectServices(QuestService quests, HashItemMinter minter, InventoryFlushService flush, TradeService trades,
    InventoryActionService inventoryActions)
{
    public QuestService Quests { get; } = quests;
    public HashItemMinter Minter { get; } = minter;
    public InventoryFlushService Flush { get; } = flush;
    public TradeService Trades { get; } = trades;
    public InventoryActionService InventoryActions { get; } = inventoryActions;
}

/// <summary>What a line's effects would do, built before anything is applied.</summary>
public sealed class KnubotPlan
{
    public Dictionary<string, QuestState?> Quests { get; } = new(StringComparer.Ordinal);
    public int SlotsNeeded { get; set; }

    /// <summary>How much of each carried item the line's takes have claimed so far, so two takes never count one stack twice.</summary>
    public Dictionary<Item, int> Reserved { get; } = new(ReferenceEqualityComparer.Instance);

    public QuestState? QuestState(KnubotContext context, string hash)
    {
        if (Quests.TryGetValue(hash, out QuestState? planned))
            return planned;

        return context.Quests.GetLog(context.Player).TryFind(hash, out PlayerQuest? quest) ? quest.State : null;
    }
}

/// <summary>When a line's effect is applied relative to the others (<see cref="KnubotEffect.ApplyAll"/>).</summary>
public enum KnubotEffectPhase
{
    /// <summary>
    /// Takes from the player. Its items are claimed before anything is handed over and removed after the grants,
    /// so a line that fails before any grant landed has taken nothing, and nothing ever has to be given back.
    /// </summary>
    Take,

    /// <summary>Hands something over (an item, a quest, its reward, a buff) and cannot be taken back.</summary>
    Grant,

    /// <summary>
    /// Opens a window; changes nothing the player holds. Not applied with the others: it opens when the line's
    /// text reaches it (<c>Hold on... {delay 3}{shop}</c>), as the script reads.
    /// </summary>
    Window
}

/// <summary>
/// A side effect written inline in a line's text. All of a line's effects are checked together against one
/// <see cref="KnubotPlan"/> before the line starts, so a line that cannot finish goes to its Fail line up front.
/// Its takes and grants are then all applied at once, under the player's persistence gate, before any of the text
/// is sent: the chat closing, the player walking off or logging out mid-line can never leave a line half applied.
/// A window opens where the text reaches it, since a chat that ends before then leaves nothing half done.
/// </summary>
public abstract record KnubotEffect
{
    public abstract bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services);

    /// <summary>Applies the effect; false when it could not be fully applied even though <see cref="Check"/> passed.</summary>
    public abstract bool Apply(KnubotContext context, KnubotEffectServices services);

    /// <summary>When this effect is applied relative to the line's others; a grant by default.</summary>
    public virtual KnubotEffectPhase Phase => KnubotEffectPhase.Grant;

    /// <summary>A window that takes the player away from the NPC: the chat closes as it applies and the rest of the line is dropped.</summary>
    public virtual bool EndsChat => false;

    public static bool CanApplyAll(KnubotEffect[] effects, KnubotContext context, KnubotEffectServices services)
    {
        if (effects.Length == 0)
            return true;

        Player player = context.Player;
        if (!player.Inventory.IsHydrated)
            return false;

        var plan = new KnubotPlan();
        foreach (KnubotEffect effect in effects)
        {
            if (!effect.Check(context, plan, services))
                return false;
        }

        return KnubotItems.FreeSlots(player) >= plan.SlotsNeeded;
    }

    /// <summary>
    /// Applies the line's takes and grants right after <see cref="CanApplyAll"/> passed and under the same gate;
    /// its windows open later, where the text reaches them (<see cref="KnubotEffectPhase.Window"/>).
    /// <para>
    /// The takes claim exactly which carried items they remove first, then the grants land in the order written,
    /// then the claimed items are removed. A taken item is retired in storage at once, so a take is never undone:
    /// handing back a copy would mint an item. Instead nothing is taken until the grants are in. A grant that fails
    /// before any grant landed returns false with nothing taken and nothing given: only the line's Fail line should
    /// be said. One that fails after a grant landed stops the remaining grants, still removes the claimed items
    /// (the player was paid for them) and returns true with <paramref name="failure"/> naming what did not apply.
    /// Claiming the items up front means a grant cannot change which ones go: an item the line hands over is never
    /// the one it takes, even when it is the same kind.
    /// </para>
    /// </summary>
    public static bool ApplyAll(KnubotEffect[] effects, KnubotContext context, KnubotEffectServices services, out string failure)
    {
        failure = string.Empty;
        var claims = new List<(KnubotTakeItem Take, List<(Item Item, int Count)> Items)>();
        var reserved = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        foreach (KnubotEffect effect in effects)
        {
            if (effect is not KnubotTakeItem take)
                continue;

            List<(Item Item, int Count)>? items = take.Claim(context.Player, reserved);
            if (items == null)
            {
                failure = take + " no longer finds enough to take";
                return false;
            }

            claims.Add((take, items));
        }

        bool changed = false;
        foreach (KnubotEffect effect in effects)
        {
            if (effect.Phase != KnubotEffectPhase.Grant)
                continue;

            if (effect.Apply(context, services))
            {
                changed = true;
                continue;
            }

            if (!changed)
            {
                failure = effect + " failed; nothing was taken";
                return false;
            }

            failure = effect + " failed after the line's grants landed";
            break;
        }

        foreach (var (take, items) in claims)
        {
            if (take.Remove(context.Player, services, items, out bool tookAny))
            {
                changed = true;
                continue;
            }

            changed |= tookAny;
            failure += (failure.Length == 0 ? string.Empty : "; ") + take + " came up short";
        }

        return changed || failure.Length == 0;
    }
}

/// <summary>
/// A dungeon quest's mission quality as a dialog names it: a fixed level, or the player's level moved by a percent
/// (<c>150</c>, <c>level</c>, <c>level+12%</c>, <c>level-25%</c>).
/// </summary>
public sealed record KnubotQuestQuality(int? Fixed, int Percent)
{
    public static bool TryParse(string word, out KnubotQuestQuality? quality)
    {
        quality = null;
        if (KnubotCondition.TryPositive(word, out int fixedLevel))
        {
            quality = new KnubotQuestQuality(fixedLevel, 0);
            return true;
        }

        if (!word.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            return false;
        string rest = word["level".Length..];
        int percent = 0;
        if (rest.Length > 0 && (rest[0] != '+' && rest[0] != '-' || !rest.EndsWith('%')
            || !int.TryParse(rest[..^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out percent)))
            return false;
        quality = new KnubotQuestQuality(null, percent);
        return true;
    }

    public int Resolve(Player player)
        => Fixed ?? Math.Max(1, (int)Math.Round(player.Stats.GetOrZero(CharacterStat.Level) * (100 + Percent) / 100.0, MidpointRounding.AwayFromZero));
}

/// <summary>
/// <c>{givequest HASH [QL]}</c>: gives a Quests.json quest. A quest the player already holds active is skipped,
/// so the rest of the line still plays. The optional quality is the mission quality of a dungeon quest.
/// </summary>
public sealed record KnubotAcceptQuest(string Hash, KnubotQuestQuality? Quality = null) : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
    {
        plan.Quests[Hash] = QuestState.Active;
        return true;
    }

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        if (context.Quests.GetLog(context.Player).TryFind(Hash, out PlayerQuest? quest) && quest.State == QuestState.Active)
            return true;

        return services.Quests.TryAssignTemplate(context.Player, Hash, out _, Quality?.Resolve(context.Player));
    }
}

/// <summary><c>{completequest HASH}</c>: completes an active quest, granting its reward and next step.</summary>
public sealed record KnubotCompleteQuest(string Hash) : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
    {
        if (plan.QuestState(context, Hash) != QuestState.Active)
            return false;

        // A quest the player already holds must be completable now (a bound mission objective done); one given
        // earlier on this line has nothing to complete yet beyond being active.
        if (!plan.Quests.ContainsKey(Hash) && !services.Quests.CanComplete(context.Player, Hash))
            return false;

        // No inventory space is reserved: the quest service delivers a reward that does not fit into overflow, so a
        // full inventory never stops the dialog that completes it.
        plan.Quests[Hash] = QuestState.Completed;
        return true;
    }

    /// <summary>
    /// Applied once the quest is earned: paid now, or earned by this line with its reward pending delivery (no room
    /// yet), which the quest service delivers later. That pending reward is not a failure: the line's takes still go,
    /// as the reward still arrives. A quest that was already earned and pending before the line and still cannot be
    /// paid did not apply: as the line's first grant, nothing is taken, so the same turn-in is never taken twice for
    /// one reward.
    /// </summary>
    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        QuestLog log = context.Quests.GetLog(context.Player);
        bool pendingBefore = log.TryFind(Hash, out PlayerQuest? before) && before.IsRewardPending;
        if (services.Quests.TryComplete(context.Player, Hash, out _))
            return true;

        return !pendingBefore && log.TryFind(Hash, out PlayerQuest? after) && after.IsRewardPending;
    }
}

/// <summary><c>{spawn ITEMHASH QL [count]}</c>: mints the item into the player's inventory.</summary>
public sealed record KnubotSpawnItem(string Hash, int? Quality, int Count) : KnubotEffect
{
    /// <summary>The written QL, or the player's level when <see cref="Quality"/> is null (<c>level</c>).</summary>
    int QualityFor(KnubotContext context) => Quality ?? context.Player.Stats.GetOrOne(CharacterStat.Level);

    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
    {
        if (!services.Minter.TryRollIds(Hash, QualityFor(context), out _, out _, out _))
            return false;

        plan.SlotsNeeded += Count;
        return true;
    }

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        for (int i = 0; i < Count; i++)
        {
            if (!services.Minter.TryMint(Hash, QualityFor(context), ItemSource.Quest, out Item item))
                continue;
            if (UniqueItems.WouldDuplicate(context.Player, item, out string uniqueName))
            {
                Helpers.ClientFeedback.SendFormatted(context.Player, Helpers.ClientFeedback.AlreadyHaveUniqueItem, uniqueName);
                continue;
            }

            KnubotItems.Give(context.Player, item, services.Flush);
        }

        return true;
    }
}

/// <summary>
/// <c>{take ITEMHASH [count]}</c>: takes carried main-inventory items minted from the hash, any QL (Windcaller Karrec's
/// "Give it to me!" takes the Bronto Burger and Maddy's Credit Card without a Give Item window). The line fails up
/// front when the player does not carry enough that can be taken: a locked item (mid-use or mid-move) does not
/// count, as it would not be taken, nor does a stack an earlier take of the line already claimed. The items are
/// claimed before the line's grants and removed after them, the very items claimed; a line whose grant fails before
/// any grant lands takes nothing (<see cref="KnubotEffect.ApplyAll"/>). A taken item is never re-created.
/// </summary>
public sealed record KnubotTakeItem(HashSet<int> TemplateIds, int Count) : KnubotEffect
{
    public override KnubotEffectPhase Phase => KnubotEffectPhase.Take;

    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
        => Claim(context.Player, plan.Reserved) != null;

    /// <summary>Claims and removes at once; <see cref="KnubotEffect.ApplyAll"/> claims before the grants and removes after.</summary>
    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        List<(Item Item, int Count)>? items = Claim(context.Player, new Dictionary<Item, int>(ReferenceEqualityComparer.Instance));
        return items != null && Remove(context.Player, services, items, out _);
    }

    /// <summary>
    /// The carried, unlocked main-inventory items this take removes and how many of each, in inventory order, after
    /// what earlier takes of the line claimed (<paramref name="reserved"/>, updated here). Null when there are not
    /// enough.
    /// </summary>
    internal List<(Item Item, int Count)>? Claim(Player player, Dictionary<Item, int> reserved)
    {
        if (!player.Inventory.IsHydrated)
            return null;

        var items = new List<(Item Item, int Count)>();
        int remaining = Count;
        foreach (Item item in player.Inventory.Inventory.Content.Values)
        {
            if (remaining <= 0)
                break;
            if (item == null || item.Locked || (!TemplateIds.Contains(item.LowId) && !TemplateIds.Contains(item.HighId)))
                continue;

            int free = Math.Max(1, item.StackCount) - reserved.GetValueOrDefault(item);
            if (free <= 0)
                continue;

            int count = Math.Min(free, remaining);
            items.Add((item, count));
            reserved[item] = reserved.GetValueOrDefault(item) + count;
            remaining -= count;
        }

        return remaining <= 0 ? items : null;
    }

    /// <summary>
    /// Removes exactly the claimed items: part of a stack when the stack holds more than claimed (a grant may have
    /// stacked onto it), else the whole item. Nothing is ever re-created. False when one is no longer where it was
    /// claimed or could not be removed; <paramref name="tookAny"/> says whether anything went.
    /// </summary>
    internal bool Remove(Player player, KnubotEffectServices services, List<(Item Item, int Count)> items, out bool tookAny)
    {
        tookAny = false;
        Container page = player.Inventory.Inventory;
        bool complete = true;
        foreach (var (item, count) in items)
        {
            int placement = -1;
            foreach (var (slotIndex, held) in page.Content)
            {
                if (ReferenceEquals(held, item))
                {
                    placement = slotIndex;
                    break;
                }
            }

            if (placement < 0 || item.Locked)
            {
                complete = false;
                continue;
            }

            if (Math.Max(1, item.StackCount) > count)
            {
                if (item.TakeFromStack(player, page, placement, count))
                    tookAny = true;
                else
                    complete = false;
                continue;
            }

            var slot = new Identity { Type = page.Identity.Type, Instance = placement };
            if (!services.InventoryActions.TryDelete(player, slot, item.InstanceId))
            {
                complete = false;
                continue;
            }

            tookAny = true;
            player.Session?.Send(new CharacterActionMessage
            {
                Identity = player.Identity, Action = CharacterActionType.DeleteItem, Target = slot
            });
        }

        return complete;
    }
}

/// <summary><c>{shop}</c>: opens the NPC's vendor window.</summary>
public sealed record KnubotOpenShop : KnubotEffect
{
    public override KnubotEffectPhase Phase => KnubotEffectPhase.Window;

    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services) => context.Npc.Shop != null;

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
        => services.Trades.TryOpenShop(context.Player, context.Npc.Shop!);
}

/// <summary><c>{opentrade}</c>: opens the NPC's Give Item window for the script's Trades.</summary>
public sealed record KnubotOpenTrade : KnubotEffect
{
    public override KnubotEffectPhase Phase => KnubotEffectPhase.Window;

    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services) => context.Script.Trades.Length != 0;

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
        => services.Trades.TryOpenKnubot(context.Player, context.Npc, KnubotService.TradeText(context.Npc), context.Script.TradeSlots);
}

/// <summary>
/// <c>{givebuff NANOID}</c>: the NPC lands the nano on the player at once, as a service (no cast bar, nano cost or
/// recharge; the Portal Technician's Shadowlands charge 239825).
/// </summary>
public sealed record KnubotGiveBuff(int NanoId) : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
        => !context.Player.IsDead && context.Player.Playfield != null;

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        Playfield.Playfield playfield = context.Player.Playfield!;
        Nanos.NanoRuntime.TryApplyImmediate(
            context.Npc,
            context.Player,
            NanoId,
            playfield.GetRequiredService<IItemBuilder>(),
            playfield.GetRequiredService<Data.IInventoryRepository>(),
            DateTime.UtcNow);
        return true;
    }
}

/// <summary>
/// <c>{useitem ITEMHASH}</c> or <c>{useitem ITEMID}</c>: the item's OnUse runs on the player, nothing goes to the inventory. For live's virtual
/// items (Null flag) that apply a service: Specialization Item - First..Fourth Spec 229611-229613/229614 set
/// Specialization for Professor Dedlock.
/// </summary>
public sealed record KnubotUseItem(int ItemId) : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
        => !context.Player.IsDead && context.Player.Playfield != null;

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
    {
        Playfield.Playfield playfield = context.Player.Playfield!;
        IItemBuilder items = playfield.GetRequiredService<IItemBuilder>();
        Item item = items.Create(ItemId, ItemId, 1, ItemSource.Quest);
        item.Definition.ExecuteOnUseSpells(context.Player, playfield.GetRequiredService<Data.IInventoryRepository>(), items, source: context.Player);
        context.Player.FlushDirtyStats();
        return true;
    }
}

/// <summary>
/// <c>{teleport X Y Z PLAYFIELD}</c>: moves the player as the item Teleport function does (feet snapped to the floor;
/// playfield 0 or the current one is an in-place move, any other a one-way zone change). Applied where the text
/// reaches it, like a window (<c>Hold on to something... {delay 2}{teleport 450 0 300 655}</c>); the chat closes as
/// the player goes, so anything written after it is never said.
/// </summary>
public sealed record KnubotTeleport(int PlayfieldId, float X, float Y, float Z) : KnubotEffect
{
    public override KnubotEffectPhase Phase => KnubotEffectPhase.Window;

    public override bool EndsChat => true;

    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
        => !context.Player.IsDead && context.Player.Playfield != null && context.Player.Session is { IsTransferring: false };

    public override bool Apply(KnubotContext context, KnubotEffectServices services)
        => ItemUseFunctions.TeleportTo(context.Player, PlayfieldId, new AORebirth.Core.Vector.Vector3(X, Y, Z));
}

/// <summary>Main-inventory item helpers shared by conditions and effects.</summary>
public static class KnubotItems
{
    /// <summary>
    /// Items carried in the main inventory minted from the ids; with <paramref name="takeable"/>, only those a
    /// <c>{take}</c> could remove now (a locked item is mid-use or mid-move).
    /// </summary>
    public static int CountCarried(Player player, HashSet<int> templateIds, bool takeable = false)
    {
        if (!player.Inventory.IsHydrated)
            return 0;

        int count = 0;
        foreach (Item item in player.Inventory.Inventory.Content.Values)
        {
            if (item == null || (takeable && item.Locked))
                continue;
            if (templateIds.Contains(item.LowId) || templateIds.Contains(item.HighId))
                count += Math.Max(1, item.StackCount);
        }

        return count;
    }

    public static int FreeSlots(Player player)
    {
        Container page = player.Inventory.Inventory;
        int used = 0;
        foreach (int slot in page.Content.Keys)
        {
            if (slot >= page.Offset && slot < page.Offset + page.Capacity)
                used++;
        }

        return page.Capacity - used;
    }

    public static void Give(Player player, Item item, InventoryFlushService flush)
    {
        if (AutoConsumedItems.TryConsume(player, item))
            return;
        if (InventoryStacking.TryGrantOntoStack(player, item, flush))
            return;

        // The first free inventory slot, else overflow (which keeps only its newest items).
        if (!player.Inventory.TryPlace(item, out Container page, out int slot))
            return;

        player.Inventory.MarkDirty(item, page, slot);
        flush.NotifyDirty(player);
        ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.None), ModuleHooks.Place(ItemPlaceKind.Inventory));
        if (ReferenceEquals(page, player.Inventory.Overflow))
        {
            OverflowGrants.Send(player, item);
            OverflowGrants.Trim(player);
            return;
        }

        player.Session?.Send(new AddTemplateMessage
        {
            Identity = player.Identity,
            HighId = item.HighId,
            LowId = item.LowId,
            Quality = item.Quality,
            Count = item.StackCount
        });
    }
}
