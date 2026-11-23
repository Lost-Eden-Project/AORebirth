namespace ZoneEngine_New.Core.GameData
{
    using AODB.Common.RDBObjects;

    using AORebirth.World.Collision;

    /// <summary>
    /// Parsed Walls.dat / Dynels.dat / Doors.dat plus collision meshes for one playfield.
    /// Missing files yield null or empty members.
    /// </summary>
    public sealed class PlayfieldGeometryData
    {
        public PlayfieldWalls? Walls { get; init; }

        public PlayfieldDynels? Dynels { get; init; }

        public PlayfieldDoors? Doors { get; init; }

        /// <summary>Normalized Collision.dat / Surfaces.dat meshes for World sim and tools.</summary>
        public PlayfieldCollisionSet? Collision { get; init; }

        /// <summary>
        /// PlayfieldContent DoorLandings.json: where arrivals at a listed door stand instead of in front of its frame,
        /// keyed by door instance. Null when the playfield configures none.
        /// </summary>
        public System.Collections.Generic.IReadOnlyDictionary<int, (float X, float Y, float Z)>? DoorLandings { get; init; }

        public bool HasAny =>
            Walls != null
            || Dynels != null
            || Doors != null
            || (Collision?.HasCollision ?? false);
    }
}
