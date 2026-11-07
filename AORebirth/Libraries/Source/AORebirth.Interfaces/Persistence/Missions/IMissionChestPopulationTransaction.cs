namespace AORebirth.Interfaces.Persistence.Missions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>Optional capability of the existing owner-locked mission transaction; never commits independently.</summary>
    public interface IMissionChestPopulationTransaction
    {
        /// <summary>
        /// Stores a mission run's chests: their item rows and one flag listing the container identities in placement
        /// order. False means an earlier population committed for this run; read its flag and rows and never reroll.
        /// </summary>
        bool TryInitializeChestPopulation(MissionChestPopulationRequest request);
    }

    public sealed class MissionChestPopulationRequest
    {
        public string QuestId { get; set; }
        public long AssignedAtUtcTicks { get; set; }
        public long NowUtcTicks { get; set; }
        public IReadOnlyList<MissionChestInitialData> Chests { get; set; }
    }

    public sealed class MissionChestInitialData
    {
        public int ContainerInstance { get; set; }
        public IReadOnlyList<MissionItemInstanceData> Items { get; set; }
    }

    public static class MissionChestPopulationFlags
    {
        /// <summary>Most items one chest may hold; the chest dynel's loot capacity.</summary>
        public const int MaxItemsPerChest = 20;

        /// <summary>The flag whose value is the run's chest container identities, comma separated, in placement order.</summary>
        public static string Key(long assignedAtUtcTicks)
        {
            if (assignedAtUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(assignedAtUtcTicks));
            return "chests:" + assignedAtUtcTicks.ToString(CultureInfo.InvariantCulture);
        }
    }
}
