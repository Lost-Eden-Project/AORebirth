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

    public QuestState? QuestState(KnubotContext context, string hash)
    {
        if (Quests.TryGetValue(hash, out QuestState? planned))
            return planned;

        return context.Quests.GetLog(context.Player).TryFind(hash, out PlayerQuest? quest) ? quest.State : null;
    }
}

/// <summary>
/// What the effects applied so far can give back when a later effect of the same line fails, newest first.
/// </summary>
public sealed class KnubotUndo
{
    readonly List<(KnubotEffect Effect, Action Revert)> _steps = [];

    public void Add(KnubotEffect effect, Action revert) => _steps.Add((effect, revert));

    /// <summary>Runs every step and names the effects given back, or "" when there was nothing to give back.</summary>
    public string Revert()
    {
        if (_steps.Count == 0)
            return string.Empty;

        var names = new List<string>(_steps.Count);
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            _steps[i].Revert();
            names.Add(_steps[i].Effect.ToString()!);
        }

        _steps.Clear();
        return "; gave back " + string.Join(", ", names);
    }
}

/// <summary>
/// A side effect written inline in a line's text. All of a line's effects are checked together against one
/// <see cref="KnubotPlan"/> before the line starts, so a line that cannot finish goes to its Fail line up front.
/// They are then all applied at once, under the player's persistence gate, before any of the text is sent: the
/// chat closing, the player walking off or logging out mid-line can never leave a line half applied.
/// </summary>
public abstract record KnubotEffect
{
    public abstract bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services);

    /// <summary>
    /// Applies the effect; false when it could not be fully applied even though <see cref="Check"/> passed.
    /// An effect that takes something registers how to give it back in <paramref name="undo"/>.
    /// </summary>
    public abstract bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo);

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
    /// Applies every effect in the order written, right after <see cref="CanApplyAll"/> passed and under the same
    /// gate. When one still fails, the effects before it give back what they took and <paramref name="failure"/>
    /// names the effect and what was given back; nothing of the line should then be said but its Fail line.
    /// </summary>
    public static bool ApplyAll(KnubotEffect[] effects, KnubotContext context, KnubotEffectServices services, out string failure)
    {
        failure = string.Empty;
        var undo = new KnubotUndo();
        foreach (KnubotEffect effect in effects)
        {
            if (effect.Apply(context, services, undo))
                continue;

            failure = effect + " failed" + undo.Revert();
            return false;
        }

        return true;
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

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
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

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
        => services.Quests.TryComplete(context.Player, Hash, out _);
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

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
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
/// count, as it would not be taken. A take that still comes up short fails the line, and what it took is given
/// back when a later effect of the line fails.
/// </summary>
public sealed record KnubotTakeItem(HashSet<int> TemplateIds, int Count) : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services)
        => KnubotItems.CountCarried(context.Player, TemplateIds, takeable: true) >= Count;

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
    {
        Player player = context.Player;
        Container page = player.Inventory.Inventory;
        var taken = new List<(int LowId, int HighId, int Quality, int Count)>();
        int remaining = Count;
        foreach (var (placement, item) in page.Content.ToArray())
        {
            if (remaining <= 0)
                break;
            if (item == null || item.Locked || (!TemplateIds.Contains(item.LowId) && !TemplateIds.Contains(item.HighId)))
                continue;

            int stack = Math.Max(1, item.StackCount);
            if (stack > remaining)
            {
                if (item.TakeFromStack(player, page, placement, remaining))
                {
                    taken.Add((item.LowId, item.HighId, item.Quality, remaining));
                    remaining = 0;
                }

                continue;
            }

            var slot = new Identity { Type = page.Identity.Type, Instance = placement };
            if (!services.InventoryActions.TryDelete(player, slot, item.InstanceId))
                continue;

            taken.Add((item.LowId, item.HighId, item.Quality, stack));
            remaining -= stack;
            player.Session?.Send(new CharacterActionMessage
            {
                Identity = player.Identity, Action = CharacterActionType.DeleteItem, Target = slot
            });
        }

        if (taken.Count != 0)
            undo.Add(this, () => GiveBack(player, taken, services.Flush));

        // Short: give back what this effect took, so the player is left as they were.
        if (remaining > 0)
        {
            undo.Revert();
            return false;
        }

        return true;
    }

    /// <summary>Re-creates what was taken (the taken instances are retired) and hands it back like a grant.</summary>
    static void GiveBack(Player player, List<(int LowId, int HighId, int Quality, int Count)> taken, InventoryFlushService flush)
    {
        if (player.Playfield == null)
            return;

        IItemBuilder items = player.Playfield.GetRequiredService<IItemBuilder>();
        foreach (var (lowId, highId, quality, count) in taken)
            KnubotItems.Give(player, items.Create(lowId, highId, quality, ItemSource.Quest, count), flush);
    }
}

/// <summary><c>{shop}</c>: opens the NPC's vendor window.</summary>
public sealed record KnubotOpenShop : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services) => context.Npc.Shop != null;

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
        => services.Trades.TryOpenShop(context.Player, context.Npc.Shop!);
}

/// <summary><c>{opentrade}</c>: opens the NPC's Give Item window for the script's Trades.</summary>
public sealed record KnubotOpenTrade : KnubotEffect
{
    public override bool Check(KnubotContext context, KnubotPlan plan, KnubotEffectServices services) => context.Script.Trades.Length != 0;

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
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

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
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

    public override bool Apply(KnubotContext context, KnubotEffectServices services, KnubotUndo undo)
    {
        Playfield.Playfield playfield = context.Player.Playfield!;
        IItemBuilder items = playfield.GetRequiredService<IItemBuilder>();
        Item item = items.Create(ItemId, ItemId, 1, ItemSource.Quest);
        item.Definition.ExecuteOnUseSpells(context.Player, playfield.GetRequiredService<Data.IInventoryRepository>(), items, source: context.Player);
        context.Player.FlushDirtyStats();
        return true;
    }
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
