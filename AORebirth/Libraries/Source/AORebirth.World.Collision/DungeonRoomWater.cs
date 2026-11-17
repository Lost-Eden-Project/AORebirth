namespace AORebirth.World.Collision
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    using AORebirth.Core.GameData;

    /// <summary>
    /// One placed dungeon room's own water (Water.dat's RoomTemplate.Water for its template), kept in room space as the
    /// client keeps it, with the frame it is asked in: the room's placed origin and quarter turns (N3
    /// <c>n3Room_t</c>+0x50 / +0x40). A room vertex <c>w</c> lies at <c>Origin + RotateY(w, QuarterTurns)</c>, the same
    /// placement the room's surfaces take (<see cref="DungeonRoomPlacer.TransformSurfaceVertex"/>).
    /// </summary>
    public sealed class DungeonRoomWater
    {
        public DungeonRoomWater(int room, Vector3 origin, int quarterTurns, IReadOnlyList<PlayfieldWaterEntry> meshes)
        {
            Room = room;
            Origin = origin;
            QuarterTurns = quarterTurns & 3;
            Meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
        }

        /// <summary>The room's cell index (<see cref="DungeonRoomBounds.Index"/>).</summary>
        public int Room { get; }

        public Vector3 Origin { get; }

        public int QuarterTurns { get; }

        public IReadOnlyList<PlayfieldWaterEntry> Meshes { get; }
    }

    /// <summary>
    /// A dungeon's rooms and the water of those that have any: what the room liquid query needs to find the point's room
    /// and ask that room's water (N3 <c>n3RoomSurface_t::CalculateClosestPoint</c>, 10013ee6).
    /// </summary>
    public sealed class DungeonRoomWaterSet
    {
        public DungeonRoomWaterSet(IReadOnlyList<DungeonRoomBounds> rooms, IReadOnlyList<DungeonRoomWater> water)
        {
            Rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            Water = water ?? throw new ArgumentNullException(nameof(water));
        }

        public IReadOnlyList<DungeonRoomBounds> Rooms { get; }

        public IReadOnlyList<DungeonRoomWater> Water { get; }
    }
}
