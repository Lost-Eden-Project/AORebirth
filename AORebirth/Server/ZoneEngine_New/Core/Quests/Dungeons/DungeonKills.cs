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
/// <para>
/// The reward chance is each player's own: a kill credits the players who get kill credit for it (the killer and the
/// teammates near them, as for kill quests and XP), and a player's chance counts only the kills credited to them. A
/// teammate who was outside does not gain from the team's kills, and starts at zero when they enter. Each player's
/// credit is one more mission flag under their own character (<see cref="CreditCheckpoint"/>).
/// </para>
/// </summary>
public sealed class DungeonKills
{
    public const string ObjectiveSlot = "objective";

    /// <summary>
    /// Ends a population checkpoint written since kill credit is per player. Without it the checkpoint is older: its
    /// kills were the whole team's, and they stay the owner's credit so a mission in progress keeps its chance.
    /// </summary>
    const string CreditMarker = "|c";

    readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);
    readonly HashSet<int> _killed = new();
    readonly Dictionary<NpcCharacter, string> _slots = new(ReferenceEqualityComparer.Instance);
    readonly bool _hasObjective;
    readonly bool _objectiveCounts;
    readonly bool _legacyCheckpoint;
    readonly Dictionary<int, HashSet<int>> _credited = new();
    readonly HashSet<int> _creditDirty = new();

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
        _legacyCheckpoint = IsLegacyCheckpoint(checkpoint);
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

    /// <summary>Players whose kill credit changed since it was last saved.</summary>
    public IReadOnlyCollection<int> DirtyCredits => _creditDirty;

    /// <summary>True when <paramref name="playerId"/>'s credit is in memory (loaded from their flag, or credited here).</summary>
    public bool HasCredit(int playerId) => _credited.ContainsKey(playerId);

    /// <summary>
    /// Takes <paramref name="playerId"/>'s saved credit (<see cref="CreditCheckpoint"/>) unless it is already loaded.
    /// The owner of a mission checkpointed before per-player credit, with no credit flag, keeps the team's old kills.
    /// </summary>
    public void LoadCredit(int playerId, string? checkpoint)
    {
        if (_credited.ContainsKey(playerId))
            return;
        HashSet<int> credit;
        if (string.IsNullOrEmpty(checkpoint) && _legacyCheckpoint && playerId == OwnerId)
        {
            credit = new HashSet<int>(_killed);
            if (credit.Count > 0)
                _creditDirty.Add(playerId);
        }
        else
        {
            credit = new HashSet<int>(ParseKilled(checkpoint));
        }
        _credited[playerId] = credit;
    }

    /// <summary>True when a saved population checkpoint predates per-player credit (see <see cref="CreditMarker"/>).</summary>
    public static bool IsLegacyCheckpoint(string? checkpoint)
        => !string.IsNullOrEmpty(checkpoint) && !checkpoint.EndsWith(CreditMarker, StringComparison.Ordinal);

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

    /// <summary>
    /// True when <paramref name="npc"/> was one of this dungeon's population; its slot is dead from now on, and the kill
    /// is credited to <paramref name="credited"/> (load each one's saved credit first, <see cref="LoadCredit"/>).
    /// </summary>
    public bool TryRecordKill(NpcCharacter npc, IEnumerable<int> credited)
    {
        ArgumentNullException.ThrowIfNull(credited);
        if (!_slots.Remove(npc, out string? slot) || !_ordinals.TryGetValue(slot, out int ordinal))
            return false;
        if (_killed.Add(ordinal))
            Dirty = true;
        foreach (int playerId in credited)
        {
            if (!_credited.TryGetValue(playerId, out HashSet<int>? credit))
                _credited[playerId] = credit = new HashSet<int>();
            if (credit.Add(ordinal))
                _creditDirty.Add(playerId);
        }
        return true;
    }

    /// <summary>The whole population's killed and total enemies; a Find Person objective is left out of both.</summary>
    public (int Killed, int Total) Chance() => Count(_killed);

    /// <summary>
    /// <paramref name="playerId"/>'s own reward chance: the kills credited to them over the whole population. Zero kills
    /// when their credit is not loaded (<see cref="LoadCredit"/>).
    /// </summary>
    public (int Killed, int Total) Chance(int playerId)
        => Count(_credited.TryGetValue(playerId, out HashSet<int>? credit) ? credit : EmptyCredit);

    static readonly HashSet<int> EmptyCredit = new();

    (int Killed, int Total) Count(HashSet<int> killedSet)
    {
        int killed = killedSet.Count;
        int total = Total;
        if (_hasObjective && !_objectiveCounts)
        {
            total--;
            if (killedSet.Contains(0))
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

    /// <summary>
    /// "total|hex bitset of killed ordinals|c"; fits the mission flag value for any dungeon the generator makes. The
    /// trailing marker says credit is per player (<see cref="CreditMarker"/>).
    /// </summary>
    public string Checkpoint() => Encode(_killed) + CreditMarker;

    /// <summary><paramref name="playerId"/>'s credited kills, "total|hex bitset" (read back by <see cref="ChanceOf"/>).</summary>
    public string CreditCheckpoint(int playerId)
        => Encode(_credited.TryGetValue(playerId, out HashSet<int>? credit) ? credit : EmptyCredit);

    string Encode(HashSet<int> ordinals)
    {
        int max = -1;
        foreach (int ordinal in ordinals)
            max = Math.Max(max, ordinal);
        byte[] bits = new byte[max < 0 ? 0 : max / 8 + 1];
        foreach (int ordinal in ordinals)
            bits[ordinal / 8] |= (byte)(1 << (ordinal % 8));
        return Total.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(bits);
    }

    internal void MarkSaved() => Dirty = false;

    internal void MarkCreditSaved(int playerId) => _creditDirty.Remove(playerId);

    static IEnumerable<int> ParseKilled(string? checkpoint)
    {
        if (string.IsNullOrEmpty(checkpoint))
            yield break;
        int bar = checkpoint.IndexOf('|');
        string hex = bar < 0 ? string.Empty : checkpoint[(bar + 1)..];
        int end = hex.IndexOf('|');
        if (end >= 0)
            hex = hex[..end];
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
