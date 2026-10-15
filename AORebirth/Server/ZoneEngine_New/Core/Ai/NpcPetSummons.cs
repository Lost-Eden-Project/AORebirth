namespace ZoneEngine_New.Core.Ai
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Mobs;

    /// <summary>
    /// An NPC's <see cref="MonsterPetRule"/>: once it starts fighting it raises pets from its pet-creation item, one every
    /// <see cref="MonsterPetRule.SummonIntervalSeconds"/> (the first one interval in), up to
    /// <see cref="MonsterPetRule.MaxPets"/>; a pet that dies is replaced
    /// <see cref="MonsterPetRule.RespawnDelaySeconds"/> later while the fight goes on. The pets are ordinary NPC pets
    /// (<see cref="Pets.PetService"/>), so the owner's reset, death or despawn dismisses them.
    /// </summary>
    sealed class NpcPetSummons
    {
        /// <summary>A summon that could not be placed is tried again this much later.</summary>
        const double RetrySeconds = 1;

        readonly NpcCharacter _npc;
        readonly MonsterPetRule _rule;
        readonly string _hash;
        readonly int _level;
        readonly List<NpcCharacter> _pets = new();
        readonly List<DateTime> _due = new();
        bool _inCombat;

        NpcPetSummons(NpcCharacter npc, MonsterPetRule rule, string hash, int level)
        {
            _npc = npc;
            _rule = rule;
            _hash = hash;
            _level = level;
        }

        /// <summary>Name of the equipment item whose OnUse raises an NPC's combat pets (one template per pet kind).</summary>
        public const string PetCreationItemName = "NPC Pet Creation Item";

        /// <summary>
        /// Combat pets for an NPC whose equipment holds a <see cref="PetCreationItemName"/> (the first, by slot, when it
        /// has several) with a pet spawn on use; otherwise null. The NPC's hash may override the
        /// <see cref="MonsterPetRule"/> defaults in MonsterCombatRules.json.
        /// </summary>
        public static NpcPetSummons? TryCreate(NpcCharacter npc, string gameDataRoot)
        {
            Item? creator = null;
            int creatorSlot = int.MaxValue;
            foreach (KeyValuePair<int, Item> slot in npc.Equipment.Content)
            {
                if (slot.Key >= creatorSlot
                    || !string.Equals(slot.Value.Name, PetCreationItemName, StringComparison.OrdinalIgnoreCase))
                    continue;
                creator = slot.Value;
                creatorSlot = slot.Key;
            }

            if (creator == null || !creator.SpellList.TryGetValue(EventType.OnUse, out List<ItemSpell>? spells))
                return null;

            string? hash = npc.MobTemplate?.Hash ?? npc.SpawnHash;
            MonsterPetRule rule = MonsterCombatRulesCatalog.For(gameDataRoot).TryGet(hash, out MonsterCombatRules rules)
                && rules.Pets is MonsterPetRule overridden
                    ? overridden
                    : new MonsterPetRule();
            if (rule.MaxPets <= 0)
                return null;

            foreach (ItemSpell spell in spells)
            {
                if (!spell.Is(FunctionType.SpawnMonster2) && !spell.Is(FunctionType.SummonPet))
                    continue;
                if (!spell.TryReadString(0, out string petHash) || petHash.Length == 0)
                    continue;
                if (!spell.TryReadInt(1, out int level))
                    level = 0;

                return new NpcPetSummons(npc, rule, petHash.Trim(), level);
            }

            return null;
        }

        /// <param name="inCombat">The NPC is fighting and not leashing home.</param>
        public void Tick(bool inCombat, DateTime nowUtc)
        {
            if (!inCombat)
            {
                // Out of the fight nothing new is raised. The reset that follows dismisses the pets.
                _inCombat = false;
                _due.Clear();
                return;
            }

            if (!_inCombat)
            {
                _inCombat = true;
                _pets.RemoveAll(IsGone);
                _due.Clear();
                for (int i = _pets.Count; i < _rule.MaxPets; i++)
                {
                    double delay = (i - _pets.Count + 1) * _rule.SummonIntervalSeconds;
                    _due.Add(nowUtc.AddSeconds(Math.Max(0, delay)));
                }
            }

            // A pet lost mid-fight comes back after the respawn delay.
            for (int i = _pets.Count - 1; i >= 0; i--)
            {
                if (!IsGone(_pets[i]))
                    continue;
                _pets.RemoveAt(i);
                _due.Add(nowUtc.AddSeconds(Math.Max(0, _rule.RespawnDelaySeconds)));
            }

            for (int i = 0; i < _due.Count; i++)
            {
                if (_due[i] > nowUtc || _pets.Count >= _rule.MaxPets)
                    continue;

                NpcCharacter? pet = _npc.Playfield?.GetService<Pets.PetService>()
                    ?.Summon(_npc, _hash, _level, durationSeconds: 0, replaceSameSlot: false);
                if (pet == null)
                {
                    _due[i] = nowUtc.AddSeconds(RetrySeconds);
                    continue;
                }

                _pets.Add(pet);
                _due.RemoveAt(i);
                i--;
            }
        }

        bool IsGone(NpcCharacter pet)
            => pet.IsDead || pet.Playfield == null || !ReferenceEquals(pet.Pet?.Owner, _npc);
    }
}
