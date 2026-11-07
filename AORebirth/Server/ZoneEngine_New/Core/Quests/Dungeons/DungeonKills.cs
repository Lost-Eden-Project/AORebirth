namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;

using ZoneEngine_New.Core.Entities;

/// <summary>
/// The kills a mission dungeon's population has taken. The dungeon owns this in memory: every spawn slot the build
/// registers gets an ordinal, a kill marks its ordinal, and a killed slot does not spawn again while the dungeon is
/// loaded or after it is rebuilt from its seed. It is written as one mission flag (<see cref="Checkpoint"/>) when the
/// dungeon unloads and when the quest completes, never per kill. The token chance at completion is killed over total,
/// leaving out a Find Person objective, which is not an enemy.
/// </summary>
public sealed class DungeonKills
{
    public const string ObjectiveSlot = "objective";

    readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);
    readonly HashSet<int> _killed = new();
    readonly Dictionary<NpcCharacter, string> _slots = new(ReferenceEqualityComparer.Instance);
    readonly bool _hasObjective;
    readonly bool _objectiveCounts;

    public DungeonKills(int ownerId, string questId, long assignedAtTicks, MissionTokenRewardPolicy? policy,
        bool hasObjective, bool objectiveCounts, string? checkpoint)
    {
        if (ownerId <= 0 || string.IsNullOrWhiteSpace(questId) || assignedAtTicks <= 0)
            throw new ArgumentException("Dungeon kills belong to an exact accepted mission.");
        OwnerId = ownerId;
        QuestId = questId;
        AssignedAtTicks = assignedAtTicks;
        Policy = policy;
        _hasObjective = hasObjective;
        _objectiveCounts = objectiveCounts;
        // The objective, when the mission has one, is always ordinal 0 so district slots keep their ordinals across
        // rebuilds whether or not the objective still spawns.
        if (hasObjective)
            RegisterSlot(ObjectiveSlot);
        foreach (int ordinal in ParseKilled(checkpoint))
            _killed.Add(ordinal);
    }

    public int OwnerId { get; }
    public string QuestId { get; }
    public long AssignedAtTicks { get; }

    /// <summary>The sided token policy frozen with the mission; null when the quest awards no tokens.</summary>
    public MissionTokenRewardPolicy? Policy { get; }

    /// <summary>Slots registered by the build: the population an untouched dungeon spawns.</summary>
    public int Total => _ordinals.Count;

    /// <summary>True when a kill happened since the last checkpoint.</summary>
    public bool Dirty { get; private set; }

    public static string Slot(string point, int branch) => point + ":" + branch.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gives <paramref name="slot"/> its ordinal; the same build order always yields the same ordinals.</summary>
    internal void RegisterSlot(string slot)
    {
        if (!_ordinals.ContainsKey(slot))
            _ordinals[slot] = _ordinals.Count;
    }

    /// <summary>Forgets every slot registered from <paramref name="firstOrdinal"/> on (a spawn point the grid refused).</summary>
    internal void RollbackSlots(int firstOrdinal)
    {
        var dropped = new List<string>();
        foreach ((string slot, int ordinal) in _ordinals)
            if (ordinal >= firstOrdinal)
                dropped.Add(slot);
        foreach (string slot in dropped)
            _ordinals.Remove(slot);
    }

    internal bool IsKilled(string slot) => _ordinals.TryGetValue(slot, out int ordinal) && _killed.Contains(ordinal);

    /// <summary>The live actor now standing in <paramref name="slot"/> (a spawn, or a respawn after the cell slept).</summary>
    internal void Bind(NpcCharacter actor, string slot) => _slots[actor] = slot;

    /// <summary>True when <paramref name="npc"/> was one of this dungeon's population; its slot is dead from now on.</summary>
    public bool TryRecordKill(NpcCharacter npc)
    {
        if (!_slots.Remove(npc, out string? slot) || !_ordinals.TryGetValue(slot, out int ordinal))
            return false;
        if (_killed.Add(ordinal))
            Dirty = true;
        return true;
    }

    /// <summary>Killed and total enemies for the token chance; a Find Person objective is left out of both.</summary>
    public (int Killed, int Total) Chance()
    {
        int killed = _killed.Count;
        int total = Total;
        if (_hasObjective && !_objectiveCounts)
        {
            total--;
            if (_killed.Contains(0))
                killed--;
        }
        return (Math.Max(0, killed), Math.Max(0, total));
    }

    /// <summary>The same chance read from a checkpoint alone, when the dungeon is not loaded.</summary>
    public static (int Killed, int Total) ChanceOf(QuestDungeonParameters parameters, string? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrEmpty(checkpoint))
            return (0, 0);
        int bar = checkpoint.IndexOf('|');
        if (bar <= 0 || !int.TryParse(checkpoint.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out int total))
            return (0, 0);
        var killed = new HashSet<int>(ParseKilled(checkpoint));
        bool hasObjective = parameters.ObjectiveNpc != null;
        if (hasObjective && parameters.MissionType != 0)
        {
            total--;
            killed.Remove(0);
        }
        return (Math.Max(0, Math.Min(killed.Count, Math.Max(0, total))), Math.Max(0, total));
    }

    /// <summary>"total|hex bitset of killed ordinals"; fits the mission flag value for any dungeon the generator makes.</summary>
    public string Checkpoint()
    {
        int max = -1;
        foreach (int ordinal in _killed)
            max = Math.Max(max, ordinal);
        byte[] bits = new byte[max < 0 ? 0 : max / 8 + 1];
        foreach (int ordinal in _killed)
            bits[ordinal / 8] |= (byte)(1 << (ordinal % 8));
        return Total.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(bits);
    }

    internal void MarkSaved() => Dirty = false;

    static IEnumerable<int> ParseKilled(string? checkpoint)
    {
        if (string.IsNullOrEmpty(checkpoint))
            yield break;
        int bar = checkpoint.IndexOf('|');
        string hex = bar < 0 ? string.Empty : checkpoint[(bar + 1)..];
        if (hex.Length == 0 || hex.Length % 2 != 0)
            yield break;
        byte[] bits;
        try { bits = Convert.FromHexString(hex); }
        catch (FormatException) { yield break; }
        for (int i = 0; i < bits.Length; i++)
            for (int bit = 0; bit < 8; bit++)
                if ((bits[i] & (1 << bit)) != 0)
                    yield return i * 8 + bit;
    }
}
