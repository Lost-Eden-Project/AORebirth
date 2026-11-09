namespace AORebirth.World.Collision
{
    using System;
    using System.Numerics;

    /// <summary>
    /// Where the client puts a body its room surface vetoes into no room: Gamecode <c>PlayfieldAnarchy_t::GetSafePos</c>
    /// (10121b3f), indoor branch, reached from N3 <c>n3RoomSurface_t::VetoRoomTransition</c> (1001462f). The start room is
    /// <c>n3Playfield_t</c>+0x44, the playfield resource's +0x4c, which the <c>RDBPlayfield_t</c> ctor (1001bf0e) zeroes
    /// and nothing sets for a generated building: room 0. Its position (<c>n3Room_t</c>+0x50, the placed template
    /// position, the middle of its tile rect) at <c>n3Room_t::ForcePosInY</c>'s height (100112cb) when that tile has a
    /// type; otherwise <c>n3Room_t::GetAPosInRoom</c> (100111a0), the centre of the room's first typed tile.
    /// </summary>
    public static class DungeonSafePosition
    {
        public static Vector3 Compute(DungeonStyleCatalog catalog, StyleRoomTemplate room, Vector3 placed, int facing)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(room);
            float s = catalog.TileSize;
            float minHeight = DungeonRoomPlacer.ComputeYOffset(
                catalog.Gnda, catalog.Dcga, catalog.MapWidth, catalog.MapHeight, room, catalog.HeightScale);
            Vector3 origin = room.LocalOrigin * s + new Vector3(1f, 0f, 1f);

            // n3Room_t::GetTilemapIndexFromPos (10011077) of the room's own position: no turn, just the origin offset.
            int ix = (int)MathF.Floor(origin.X / s);
            int iz = (int)MathF.Floor(origin.Z / s);
            if (ix >= 0 && iz >= 0 && ix < room.NumTilesX && iz < room.NumTilesZ)
            {
                int tx = room.TileMinX + ix;
                int tz = room.TileMinZ + iz;
                if (TypeNoWalls(catalog, tx, tz) != 0)
                    return new Vector3(placed.X, ForcePosInY(catalog, tx, tz) - minHeight + placed.Y, placed.Z);
            }

            // GetAPosInRoom: x outer, z inner; no typed tile answers (0, 0, 0).
            for (int i = 0; i < room.NumTilesX; i++)
            {
                for (int k = 0; k < room.NumTilesZ; k++)
                {
                    int tx = room.TileMinX + i;
                    int tz = room.TileMinZ + k;
                    if (TypeNoWalls(catalog, tx, tz) == 0)
                        continue;

                    // n3Room_t::GetTileCenterGlobalPos (10010895).
                    var local = new Vector3((i + 0.5f) * s, Floor(catalog, tx, tz) - minHeight, (k + 0.5f) * s) - origin;
                    return placed + DungeonRoomPlacer.RotateY(local, facing);
                }
            }

            return Vector3.Zero;
        }

        /// <summary><c>n3Room_t::ForcePosInY</c> (100112cb): the highest of the tile's four floor corners.</summary>
        static float ForcePosInY(DungeonStyleCatalog catalog, int tx, int tz)
        {
            int dx = tx != 0 ? 1 : 0;
            int dz = tz != 0 ? 1 : 0;
            float h = Floor(catalog, tx, tz);
            h = MathF.Max(h, Floor(catalog, tx - dx, tz));
            h = MathF.Max(h, Floor(catalog, tx - dx, tz - dz));
            h = MathF.Max(h, Floor(catalog, tx, tz - dz));
            return h;
        }

        /// <summary><c>n3Tilemap_t::GetTileTypeNoWalls</c> (10017aa1).</summary>
        static int TypeNoWalls(DungeonStyleCatalog catalog, int x, int z)
        {
            int i = z * catalog.MapWidth + x;
            return (uint)x < (uint)catalog.MapWidth && (uint)i < (uint)catalog.Dcga.Length ? catalog.Dcga[i] & 0x7F : 0;
        }

        /// <summary>The tile's floor byte times the height scale (N3 FUN_10016454).</summary>
        static float Floor(DungeonStyleCatalog catalog, int x, int z)
        {
            int i = z * catalog.MapWidth + x;
            return (uint)i < (uint)catalog.Gnda.Length ? catalog.Gnda[i] * catalog.HeightScale : 0f;
        }
    }
}
