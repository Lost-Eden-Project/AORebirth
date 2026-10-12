namespace ZoneEngine_New.Core.Nanos
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Weapon-hit procs. AddOffProc [chance %, nano] fires on a connecting weapon hit the holder makes; AddDefProc fires
    /// when a weapon hit connects with the holder. A proc lives on an active buff's OnUse (Ritualistic Touch 210353
    /// [25, 210341]), a worn item's OnWear (Ofab helmets' AddDefProc) or the swinging weapon's OnHit. A worn weapon's
    /// offensive procs only fire on that weapon's own hits. Each source rolls its own chance. A hostile proc nano lands
    /// on the opponent (210341 drains the struck target); any other lands on the proc's owner.
    /// </summary>
    internal static class ProcRules
    {
        /// <summary>A connecting weapon hit: the attacker's offensive procs, then the defender's defensive ones.</summary>
        public static void OnWeaponHit(Character attacker, Character defender, Item? weapon)
        {
            ArgumentNullException.ThrowIfNull(attacker);
            ArgumentNullException.ThrowIfNull(defender);
            if (attacker.IsDead || defender.IsDead || ReferenceEquals(attacker, defender))
                return;

            Fire(attacker, defender, Collect(attacker, FunctionType.AddOffProc, weapon));
            if (!attacker.IsDead && !defender.IsDead)
                Fire(defender, attacker, Collect(defender, FunctionType.AddDefProc, weapon: null));
        }

        static List<(int Chance, int NanoId)>? Collect(Character holder, FunctionType kind, Item? weapon)
        {
            List<(int Chance, int NanoId)>? procs = null;
            IReadOnlyList<Buff> buffs = holder.Buffs;
            for (int i = 0; i < buffs.Count; i++)
                Add(buffs[i].SpellList, EventType.OnUse, kind, ref procs);

            if (holder is Player player && player.Inventory.IsHydrated)
            {
                foreach (Item item in player.Inventory.Armor.Content.Values)
                    Add(item.SpellList, EventType.OnWear, kind, ref procs);
                foreach (Item item in player.Inventory.Implant.Content.Values)
                    Add(item.SpellList, EventType.OnWear, kind, ref procs);
                // Wielded weapons: defensive procs from any of them, offensive ones from the weapon that hit.
                foreach (Item item in player.Inventory.Equipment.Content.Values)
                {
                    if (kind == FunctionType.AddDefProc || ReferenceEquals(item, weapon))
                        Add(item.SpellList, EventType.OnWear, kind, ref procs);
                }
            }
            else if (holder is NpcCharacter npc)
            {
                foreach (Item item in npc.Equipment.Content.Values)
                {
                    if (kind == FunctionType.AddDefProc || ReferenceEquals(item, weapon))
                        Add(item.SpellList, EventType.OnWear, kind, ref procs);
                }
            }

            if (kind == FunctionType.AddOffProc && weapon != null)
                Add(weapon.SpellList, EventType.OnHit, kind, ref procs);
            return procs;
        }

        static void Add(Dictionary<EventType, List<ItemSpell>> spellList, EventType eventType, FunctionType kind,
            ref List<(int Chance, int NanoId)>? procs)
        {
            if (!spellList.TryGetValue(eventType, out List<ItemSpell>? spells))
                return;

            for (int i = 0; i < spells.Count; i++)
            {
                ItemSpell spell = spells[i];
                if (!spell.Is(kind) || !spell.TryReadInt(0, out int first) || !spell.TryReadInt(1, out int second))
                    continue;

                // [chance %, nano]; a few entries list the nano first (AddDefProc [267590, ...]).
                (int chance, int nanoId) = first > 100 && second <= 100 ? (second, first) : (first, second);
                if (chance > 0 && nanoId > 0)
                    (procs ??= new List<(int, int)>()).Add((Math.Min(chance, 100), nanoId));
            }
        }

        static void Fire(Character holder, Character opponent, List<(int Chance, int NanoId)>? procs)
        {
            if (procs == null)
                return;

            Playfield? playfield = holder.Playfield;
            IItemBuilder? items = playfield?.GetService<IItemBuilder>();
            IInventoryRepository? inventory = playfield?.GetService<IInventoryRepository>();
            if (items == null || inventory == null)
                return;

            DateTime now = DateTime.UtcNow;
            foreach ((int chance, int nanoId) in procs)
            {
                if (Random.Shared.Next(100) >= chance || holder.IsDead)
                    continue;

                ItemTemplate template = items.CreateTemplate(nanoId, nanoId, quality: 1);
                if (template.Id != nanoId)
                    continue;

                Character recipient = NanoSpell.From(template).IsHostile ? opponent : holder;
                if (recipient.IsDead)
                    continue;
                NanoRuntime.TryApplyImmediate(holder, recipient, nanoId, items, inventory, now);
            }
        }
    }
}
