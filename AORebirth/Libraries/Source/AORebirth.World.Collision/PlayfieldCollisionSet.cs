namespace AORebirth.World.Collision
{
    using System;
    using System.Collections.Generic;

    using AODB.Common.RDBObjects;

    using AORebirth.Core.GameData;

    /// <summary>
    /// Engine-agnostic collision geometry for one playfield: surface meshes + optional terrain, plus the teleportals
    /// the zone surfaces carry.
    /// </summary>
    public sealed class PlayfieldCollisionSet
    {
        public PlayfieldCollisionSet(
            int playfieldId,
            IReadOnlyList<CollisionTriangleMesh> surfaceMeshes,
            TerrainHeightfield? terrain,
            IReadOnlyList<SurfaceTeleportal>? teleportals = null,
            IReadOnlyList<PlayfieldWaterEntry>? water = null,
            DungeonRoomWaterSet? roomWater = null)
        {
            if (playfieldId <= 0)
                throw new ArgumentOutOfRangeException(nameof(playfieldId));

            PlayfieldId = playfieldId;
            SurfaceMeshes = surfaceMeshes ?? throw new ArgumentNullException(nameof(surfaceMeshes));
            Terrain = terrain;
            Teleportals = teleportals ?? Array.Empty<SurfaceTeleportal>();
            Water = water ?? Array.Empty<PlayfieldWaterEntry>();
            RoomWater = roomWater;
        }

        public int PlayfieldId { get; }

        public IReadOnlyList<CollisionTriangleMesh> SurfaceMeshes { get; }

        public TerrainHeightfield? Terrain { get; }

        /// <summary>
        /// Zone surface teleportals (walk into the X/Z area, land on the destination playfield's destination line),
        /// one per distinct area: every locality cell a teleportal overlaps carries its own copy.
        /// </summary>
        public IReadOnlyList<SurfaceTeleportal> Teleportals { get; }

        /// <summary>
        /// The playfield-wide water meshes (Water.dat's RDBPlayfield.GlobalWaterData, world space): what the client's
        /// liquid surface query is built from. Room water (dungeon templates) is <see cref="RoomWater"/>.
        /// </summary>
        public IReadOnlyList<PlayfieldWaterEntry> Water { get; }

        /// <summary>A dungeon's rooms and their own water, placed; null when no room has any.</summary>
        public DungeonRoomWaterSet? RoomWater { get; }

        public bool HasCollision => Terrain != null || SurfaceMeshes.Count > 0;
    }
}
