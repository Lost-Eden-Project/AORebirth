namespace AORebirth.Interfaces.Persistence.Missions
{
    public sealed class MissionCommitOutcomeUnknownException : System.Exception
    {
        public MissionCommitOutcomeUnknownException(System.Exception inner)
            : base("Mission transaction commit outcome is unknown; quarantine the character and reconcile before another write.", inner) { }
    }

    /// <summary>One item row a mission transaction grants or expects: chest contents, quest rewards, mission items.</summary>
    public sealed class MissionItemInstanceData
    {
        public int InstanceId { get; set; }
        public int ContainerType { get; set; }
        public int ContainerInstance { get; set; }
        public int ContainerPlacement { get; set; }
        public int ItemType { get; set; }
        public int LowId { get; set; }
        public int HighId { get; set; }
        public int Quality { get; set; }
        public int StackCount { get; set; }
        public byte Source { get; set; }
    }
}
