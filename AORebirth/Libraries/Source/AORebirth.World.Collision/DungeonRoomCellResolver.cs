namespace AORebirth.World.Collision
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// Picks a dungeon room cell. A point belongs to the room on its own floor: a placed room's box is a floor
    /// separation tall from that room's own base, so a lower floor's box reaches up into the next floor, and of the
    /// boxes holding a point the one based highest (nearest below it) wins. Nested rooms on one base: smallest
    /// containing AABB wins; otherwise nearest.
    /// </summary>
    public static class DungeonRoomCellResolver
    {
        /// <summary>Bases closer than this are the same floor (float noise), so volume decides between them.</summary>
        const float SameBaseTolerance = 0.01f;

        public static int Resolve(IReadOnlyList<DungeonRoomBounds> rooms, Vector3 position)
        {
            ArgumentNullException.ThrowIfNull(rooms);
            if (rooms.Count == 0)
                throw new ArgumentException("Dungeon room list is empty.", nameof(rooms));

            int bestContain = -1;
            float bestBase = float.MinValue;
            float bestVolume = float.MaxValue;
            int bestNear = rooms[0].Index;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < rooms.Count; i++)
            {
                DungeonRoomBounds room = rooms[i];
                float distance = room.DistanceSquared(position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestNear = room.Index;
                }

                if (!room.Contains(position))
                    continue;

                bool higherFloor = room.Min.Y > bestBase + SameBaseTolerance;
                bool sameFloor = !higherFloor && room.Min.Y >= bestBase - SameBaseTolerance;
                if (!higherFloor && !(sameFloor && room.Volume < bestVolume))
                    continue;

                bestBase = room.Min.Y;
                bestVolume = room.Volume;
                bestContain = room.Index;
            }

            return bestContain >= 0 ? bestContain : bestNear;
        }

        /// <summary>
        /// Whether <paramref name="room"/> holds <paramref name="position"/> on its own floor: its box contains the
        /// point and no other box containing the point is based higher. A character standing at a floor's height is
        /// inside the lower floor's box too, which must not keep it in that room.
        /// </summary>
        public static bool HoldsOnFloor(IReadOnlyList<DungeonRoomBounds> rooms, in DungeonRoomBounds room, Vector3 position)
        {
            ArgumentNullException.ThrowIfNull(rooms);
            if (!room.Contains(position))
                return false;

            for (int i = 0; i < rooms.Count; i++)
            {
                DungeonRoomBounds other = rooms[i];
                if (other.Index == room.Index)
                    continue;
                if (other.Min.Y > room.Min.Y + SameBaseTolerance && other.Contains(position))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="room"/> is the only room that can hold <paramref name="position"/>: its box contains
        /// the point and no other box does, apart from a floor below's reaching up. A placed room's box is the bounding
        /// box of an often irregular room (<see cref="DungeonRoomBounds.FromPlaced"/>), a metre past its tiles, so a
        /// hall's box takes in whole side rooms and every box overlaps its neighbours at the walls and doorways. Which of
        /// the rooms holding a point a dynel's cell says is a matter of history: the room it came from keeps it while
        /// that box still holds it, and a spawn takes the box based highest, then the smallest. Rooms on one floor stand
        /// at their templates' own heights (a few metres apart), so a box is a floor below only when its base is more
        /// than half a floor (half this room's box height) under this room's. A decision that needs the room to be
        /// right (which rooms a sight line crosses) asks this instead of <see cref="HoldsOnFloor"/>.
        /// </summary>
        public static bool HoldsAlone(IReadOnlyList<DungeonRoomBounds> rooms, in DungeonRoomBounds room, Vector3 position)
        {
            ArgumentNullException.ThrowIfNull(rooms);
            if (!room.Contains(position))
                return false;

            float floorBelow = room.Min.Y - (0.5f * (room.Max.Y - room.Min.Y));
            for (int i = 0; i < rooms.Count; i++)
            {
                DungeonRoomBounds other = rooms[i];
                if (other.Index == room.Index)
                    continue;
                if (other.Min.Y >= floorBelow && other.Contains(position))
                    return false;
            }

            return true;
        }
    }
}
