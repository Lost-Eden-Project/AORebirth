namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Ai;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Mobs;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// DelayedSpawnNpc (53220) on an NPC's equipment: the stock way a monster brings others into the world. OnWear runs
    /// when the NPC spawns (Master of Biological Metamorphoses' EP3_HealTower 262910 puts a Regeneration Conduit 19 m
    /// either side of it; the Coccoon Attendant's EP3_CoccoonRoomFill items lay out its 43 cocoons), OnTerminate when it
    /// is killed (an Alien Coccoon's EP3_CoccoonSurprise 262886 leaves a Fresh Recruit where it died). Arguments, as the
    /// stock items write them: [hash, level, x, y, z, ?, delay ms, mode, ?]. The level is interpolated with the item's
    /// QL like any argument (1 on the QL 1 item, 250 on the QL 250 one), so the spawn follows the spawner's level; the
    /// offset is on world axes from the spawner; a death spawn starts on the killer when it may attack them. The mode
    /// (0 heal towers, 2 death spawns, 10 room fill) decides only the heading; arguments 5 and 8 are not read.
    /// </summary>
    public sealed partial class SpawnService
    {
        /// <summary>Spawns waiting for their delay on this playfield; more are dropped with a warning.</summary>
        const int MaxPendingItemSpawns = 256;

        const int MaxItemSpawnDelayMs = 60_000;

        const int LevelArgument = 1;

        const int DelayArgument = 6;

        const int ModeArgument = 7;

        readonly List<PendingItemSpawn> _pendingItemSpawns = new();
        readonly List<Item> _spawnItems = new();
        readonly Dictionary<string, int> _spawnItemVariants = new(StringComparer.Ordinal);
        bool _itemSpawnOverflowWarned;

        /// <param name="Spawner">The equipped NPC: its spawn generation and (OnWear) whether it is still here.</param>
        readonly record struct PendingItemSpawn(
            long DueMs,
            string Hash,
            int Level,
            Vector3 At,
            Quaternion Heading,
            NpcCharacter Spawner,
            bool NeedsLiveSpawner,
            Identity Enemy);

        /// <summary>
        /// Queues every DelayedSpawnNpc on <paramref name="npc"/>'s equipment for <paramref name="trigger"/>. Spawn items
        /// whose names differ only in a variant number (EP3_CoccoonRoomFill1_001 / 2 / 3) are alternative layouts the
        /// NPC wears together: one of them, picked at random, runs. Nothing for a pet, or for an NPC already as many spawn
        /// generations deep as a script may go (<see cref="NpcScripts.NpcScriptRunner.MaxSpawnDepth"/>), so spawns that
        /// spawn each other always stop.
        /// </summary>
        public void ScheduleEquipmentSpawns(NpcCharacter npc, EventType trigger, Character? enemy = null)
        {
            ArgumentNullException.ThrowIfNull(npc);
            if (npc.Pet != null || npc.SpawnSource == SpawnSource.Summoned
                || npc.ScriptSpawnDepth >= NpcScripts.NpcScriptRunner.MaxSpawnDepth)
                return;

            List<Item> items = _spawnItems;
            items.Clear();
            foreach (KeyValuePair<int, Item> slot in npc.Equipment.EnumerateSlots())
            {
                if (HasSpawn(slot.Value, trigger))
                    items.Add(slot.Value);
            }

            if (items.Count == 0)
                return;

            // Alternatives: count each variant stem, then keep one item per stem by reservoir pick.
            Dictionary<string, int> variants = _spawnItemVariants;
            variants.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                string stem = VariantStem(items[i]);
                variants[stem] = variants.TryGetValue(stem, out int seen) ? seen + 1 : 1;
            }

            long now = Environment.TickCount64;
            // Each stem's count becomes the index of the one to keep; counting it down below finds that item.
            foreach (string stem in new List<string>(variants.Keys))
                variants[stem] = Random.Shared.Next(variants[stem]);

            for (int i = 0; i < items.Count; i++)
            {
                string stem = VariantStem(items[i]);
                int pick = variants[stem];
                variants[stem] = pick - 1;
                if (pick != 0)
                    continue;

                if (!Enqueue(npc, items[i], trigger, enemy, now))
                    break;
            }

            items.Clear();
        }

        /// <summary>
        /// The item's spawn functions as its QL 1 template writes them. The QL 300 template lists the same spawns in a
        /// different order (EP3_CoccoonRoomFill2_001 starts with the Accelerator's cocoon at [-11, 0, -9], _300 with it at
        /// [13, 0, -10]), so interpolating the two entry by entry blends unrelated offsets and scatters the field. The
        /// QL 1 list is the one the retail capture matches; only the level is read from the interpolated item.
        /// </summary>
        List<ItemSpell>? LowSpawnSpells(Item item, EventType trigger)
        {
            if (item.LowId <= 0 || item.LowId == item.HighId)
                return null;

            if (!_lowSpawnSpells.TryGetValue((item.LowId, trigger), out List<ItemSpell>? spells))
            {
                ItemTemplate low = _items.CreateTemplate(item.LowId, item.LowId, item.Quality);
                spells = low.SpellList.TryGetValue(trigger, out List<ItemSpell>? found) ? found : null;
                _lowSpawnSpells[(item.LowId, trigger)] = spells;
            }

            return spells;
        }

        readonly Dictionary<(int LowId, EventType Trigger), List<ItemSpell>?> _lowSpawnSpells = new();

        /// <summary>Queues one item's DelayedSpawnNpc functions; false when the queue is full.</summary>
        bool Enqueue(NpcCharacter npc, Item item, EventType trigger, Character? enemy, long now)
        {
            List<ItemSpell> spells = item.SpellList[trigger];
            List<ItemSpell>? placement = LowSpawnSpells(item, trigger);
            if (placement != null && placement.Count != spells.Count)
                placement = null;

            for (int i = 0; i < spells.Count; i++)
            {
                ItemSpell levelled = spells[i];
                ItemSpell spell = placement?[i] ?? levelled;
                if (!spell.Is(FunctionType.DelayedSpawnNpc)
                    || !spell.TryReadString(0, out string hash) || hash.Length == 0
                    || !_gameData.CanResolveMobHash(hash))
                    continue;

                if (_pendingItemSpawns.Count >= MaxPendingItemSpawns)
                {
                    if (!_itemSpawnOverflowWarned)
                    {
                        _itemSpawnOverflowWarned = true;
                        _logger.Warn("Item spawns: " + MaxPendingItemSpawns + " waiting on playfield "
                            + _playfield.Identity.Instance.ToString(CultureInfo.InvariantCulture) + "; further ones are dropped");
                    }

                    return false;
                }

                int level = levelled.TryReadInt(LevelArgument, out int argument) && argument > 0
                    ? argument
                    : Math.Max(1, npc.Stats.GetOrOne(CharacterStat.Level));
                // World axes, not the spawner's frame: Master of Biological Metamorphoses faced -90 degrees with its
                // conduits at world x +/-19, and the cocoons stood at the item offsets on world axes (captures 2026-11-09).
                var at = new Vector3(npc.Position.x + ReadNumber(spell, 2), npc.Position.y + ReadNumber(spell, 3), npc.Position.z + ReadNumber(spell, 4));
                int delay = spell.TryReadInt(DelayArgument, out int ms) ? Math.Clamp(ms, 0, MaxItemSpawnDelayMs) : 0;

                // Heading, as the captures show it, never the spawner's: mode 0 (the heal towers) stands at heading 0
                // whatever way its boss faces; mode 10 (room fill cocoons) and mode 2 (a cocoon's hatchling) each get
                // their own random yaw.
                Quaternion heading = spell.TryReadInt(ModeArgument, out int mode) && mode != 0
                    ? YawHeading(Random.Shared.NextDouble() * Math.PI * 2.0)
                    : new Quaternion(0, 0, 0, 1);
                _pendingItemSpawns.Add(new PendingItemSpawn(now + delay, hash, level, at, heading, npc,
                    NeedsLiveSpawner: trigger != EventType.OnTerminate, enemy?.Identity ?? Identity.None));
            }

            return true;
        }

        /// <summary>A heading turned <paramref name="yaw"/> radians about the vertical axis.</summary>
        static Quaternion YawHeading(double yaw) => new(0, Math.Sin(yaw / 2.0), 0, Math.Cos(yaw / 2.0));

        static bool HasSpawn(Item item, EventType trigger)
        {
            if (!item.SpellList.TryGetValue(trigger, out List<ItemSpell>? spells) || spells == null)
                return false;
            for (int i = 0; i < spells.Count; i++)
            {
                if (spells[i].Is(FunctionType.DelayedSpawnNpc))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The item's name with the variant number before its trailing _NNN taken out ("EP3_CoccoonRoomFill#_001"); an
        /// item without one is its own stem. Unnamed items stand alone by instance.
        /// </summary>
        static string VariantStem(Item item)
        {
            string? name = item.Definition?.Name;
            if (string.IsNullOrEmpty(name))
                return "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item).ToString(CultureInfo.InvariantCulture);

            int suffix = name.LastIndexOf('_');
            int end = suffix > 0 ? suffix : name.Length;
            int start = end;
            while (start > 0 && char.IsAsciiDigit(name[start - 1]))
                start--;
            return start == end ? name : string.Concat(name.AsSpan(0, start), "#", name.AsSpan(end));
        }

        /// <summary>Spawns the queued item spawns that are due. Called from <see cref="Tick"/>.</summary>
        void SpawnDueItemSpawns()
        {
            if (_pendingItemSpawns.Count == 0)
                return;

            long now = Environment.TickCount64;
            for (int i = 0; i < _pendingItemSpawns.Count; i++)
            {
                PendingItemSpawn pending = _pendingItemSpawns[i];
                if (pending.DueMs > now)
                    continue;

                _pendingItemSpawns.RemoveAt(i--);
                NpcCharacter spawner = pending.Spawner;
                if (pending.NeedsLiveSpawner && (spawner.IsDead || !ReferenceEquals(spawner.Playfield, _playfield)))
                    continue;

                // Read now, not when queued: an OnWear entry is queued inside SpawnMob, before the caller sets it.
                if (spawner.ScriptSpawnDepth >= NpcScripts.NpcScriptRunner.MaxSpawnDepth)
                    continue;

                int level = pending.Level;
                if (!_gameData.TryResolveMobTemplate(pending.Hash, level, out MobTemplate template)
                    || !NpcTemplateValidation.CanSpawn(template))
                    continue;

                NpcCharacter add;
                try
                {
                    add = SpawnMob(template, pending.At, pending.Heading, level, SpawnSource.ItemSpawn, spawnHash: pending.Hash);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
                {
                    _logger.Warn("Item spawn of '" + pending.Hash + "' from " + spawner.Name + " failed: " + exception.Message);
                    continue;
                }

                add.ScriptSpawnDepth = spawner.ScriptSpawnDepth + 1;
                if (pending.Enemy.Instance != 0 && _registry.TryGet(pending.Enemy, out Dynel? dynel)
                    && dynel is Character { IsDead: false } enemy && ReferenceEquals(enemy.Playfield, _playfield)
                    && CombatRules.CanAttack(add, enemy))
                    add.Brain?.AddThreat(enemy.Identity, NpcAiRules.ProximityHate);
            }
        }

        /// <summary>A numeric argument (stock items write offsets as integers or floats); 0 when absent.</summary>
        static double ReadNumber(ItemSpell spell, int index)
        {
            if (index < 0 || index >= spell.Arguments.Count)
                return 0;

            return spell.Arguments[index] switch
            {
                int i => i,
                long l => l,
                short s => s,
                byte b => b,
                uint u => u,
                float f when float.IsFinite(f) => f,
                double d when double.IsFinite(d) => d,
                _ => 0
            };
        }
    }
}
