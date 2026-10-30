namespace AORebirth.Modules.Events
{
    using System;

    using AORebirth.Modules.World;

    /// <summary>Which entities a visibility observer hears about (module API 1.6).</summary>
    public sealed class VisibilitySubscription
    {
        /// <summary>Entity types to report (<see cref="EntityRef.Type"/>); empty means NPCs only.</summary>
        public int[] EntityTypes { get; init; } = Array.Empty<int>();
    }

    /// <summary>Why an entity became visible or stopped being visible to a client (module API 1.6).</summary>
    public enum VisibilityReason
    {
        Spawned,
        EnteredRange,
        PlayerArrived,
        Despawned,
        LeftRange,
        PlayerLeft
    }

    /// <summary>
    /// Told when the server tells a client that an entity exists, and when it stops (module API 1.6,
    /// <c>observe-visibility</c>): the moment a player could first have seen something. Runs on the playfield tick, at the
    /// moment the spawn or despawn message is queued to that client. <see cref="OnVisibility"/> must not block or allocate;
    /// one that throws is logged and, after 10 failures in a row, removed. One slower than 2 ms is reported.
    /// </summary>
    public interface IVisibilityObserver
    {
        void OnVisibility(in VisibilitySample sample);
    }

    /// <summary>One visibility change.</summary>
    public readonly struct VisibilitySample
    {
        public VisibilitySample(int characterId, int playfieldId, EntityRef entity, bool visible, VisibilityReason reason, long timestamp)
        {
            CharacterId = characterId;
            PlayfieldId = playfieldId;
            Entity = entity;
            Visible = visible;
            Reason = reason;
            Timestamp = timestamp;
        }

        /// <summary>Whose client was told.</summary>
        public int CharacterId { get; }

        public int PlayfieldId { get; }

        public EntityRef Entity { get; }

        /// <summary>True when the client was told it exists; false when it was told it is gone.</summary>
        public bool Visible { get; }

        public VisibilityReason Reason { get; }

        /// <summary>When it was queued to the client, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; }
    }
}
