namespace AORebirth.Interfaces.Persistence.Missions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>Optional capability of the existing owner-locked mission transaction; never commits independently.</summary>
    public interface IMissionChestPopulationTransaction
    {
        /// <summary>False means an earlier population committed. Reload its flags and current container rows; never reroll it.</summary>
        bool TryInitializeChestPopulation(MissionChestPopulationRequest request);
    }

    public sealed class MissionChestPopulationRequest
    {
        public string QuestId { get; set; }
        public long AssignedAtUtcTicks { get; set; }
        public string ExpectedAcgBuildingGeneratorJson { get; set; }
        public long NowUtcTicks { get; set; }
        public IReadOnlyList<MissionChestInitialData> Chests { get; set; }
    }

    public sealed class MissionChestInitialData
    {
        public int ContainerInstance { get; set; }
        /// <summary>Frozen placement/appearance metadata. Existing mission flags permit at most 1024 characters per chest.</summary>
        public string SnapshotJson { get; set; }
        public IReadOnlyList<MissionItemInstanceData> Items { get; set; }
    }

    public static class MissionChestPopulationFlags
    {
        public static string Prefix(long assignedAtUtcTicks)
        {
            if (assignedAtUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(assignedAtUtcTicks));
            return "mission-chest:1:" + assignedAtUtcTicks.ToString(CultureInfo.InvariantCulture) + ":";
        }

        /// <summary>The seal value is the invariant decimal chest count; zero also seals an intentionally empty population.</summary>
        public static string Seal(long assignedAtUtcTicks) => Prefix(assignedAtUtcTicks) + "sealed";

        public static string Chest(long assignedAtUtcTicks, int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            return Prefix(assignedAtUtcTicks) + "chest:" + index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
