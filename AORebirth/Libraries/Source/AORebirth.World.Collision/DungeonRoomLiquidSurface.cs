namespace AORebirth.World.Collision
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Core.GameData;

    using N3Lite;
    using N3Lite.Liquids;
    using N3Lite.Surfaces;

    using Vector3 = System.Numerics.Vector3;

    /// <summary>
    /// A dungeon's collision with its rooms' own water: the ground answers as the inner surface does, then the liquid
    /// step of LostEden.Core's <c>DungeonRoomSurface.CalculateClosestPoint</c> (N3 <c>n3RoomSurface_t</c> 10013ee6): the
    /// point's room's water over the result, with the query point's height as the limit
    /// (<c>n3Zone_t::PerformLiquidCollisionTest</c>, 1001a80c, in the room's frame), a result more than 1.2 under it
    /// raised to that depth (<see cref="LiquidLift"/>), and the body's liquid medium told. A point in no room, or a room
    /// without water, finds none. The liquid normal is kept between calls, as the client's one shared copy is.
    /// <para>The room is the server's room lookup (<see cref="DungeonRoomCellResolver"/>, the room on the point's own
    /// floor), not the client's tile-exact <c>PosToRoom</c>.</para>
    /// </summary>
    public sealed class DungeonRoomLiquidSurface : ISurface, ILiquidSurface
    {
        readonly ISurface _inner;
        readonly IReadOnlyList<DungeonRoomBounds> _rooms;
        readonly Dictionary<int, (RoomLiquid Liquid, Vec3 Origin, int Turns)> _water = new();
        Vec3 _liquidNormal;

        public DungeonRoomLiquidSurface(ISurface inner, DungeonRoomWaterSet water)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            ArgumentNullException.ThrowIfNull(water);
            _rooms = water.Rooms;
            foreach (DungeonRoomWater room in water.Water)
            {
                RoomLiquid liquid = Build(room.Meshes);
                if (liquid.Count > 0)
                    _water[room.Room] = (liquid, new Vec3(room.Origin.X, room.Origin.Y, room.Origin.Z), room.QuarterTurns);
            }
        }

        /// <summary>The ground and line queries' own surface.</summary>
        public ISurface Inner => _inner;

        /// <summary>Rooms with water.</summary>
        public int WaterRoomCount => _water.Count;

        public bool GetLineIntersection(Vec3 start, Vec3 end, out Vec3 hit, out Vec3 normal, bool clipToBounds, object locality)
            => _inner.GetLineIntersection(start, end, out hit, out normal, clipToBounds, locality);

        public bool VetoPosition(ref Vec3 position, object locality, Vec3 previous)
            => _inner.VetoPosition(ref position, locality, previous);

        public void CalculateClosestPoint(Vec3 point, out Vec3 closest, out Vec3 normal, object locality)
            => CalculateClosestPoint(point, out closest, out normal, locality, null);

        public void CalculateClosestPoint(Vec3 point, out Vec3 closest, out Vec3 normal, object locality, LiquidMedium medium)
        {
            _inner.CalculateClosestPoint(point, out closest, out normal, locality);
            if (_water.Count == 0)
                return;

            // A room with no water answers "none", and the medium still hears it.
            float height = LiquidZones.NoLiquid;
            Vec3 hitNormal = Vec3.Zero;
            int type = 0;
            if (TryRoomAt(point, out int room) && _water.TryGetValue(room, out var water))
                height = water.Liquid.PerformLiquidCollisionTest(closest, out hitNormal, out type, point.Y, water.Origin, water.Turns);
            if (!hitNormal.IsZero)
                _liquidNormal = hitNormal;

            LiquidLift.Apply(height, _liquidNormal, ref closest, ref normal);

            if (medium != null)
            {
                medium.Height = height;
                medium.Normal = _liquidNormal;
                medium.Type = type;
            }
        }

        bool TryRoomAt(Vec3 point, out int room)
        {
            room = -1;
            if (_rooms.Count == 0)
                return false;

            var position = new Vector3(point.X, point.Y, point.Z);
            int resolved = DungeonRoomCellResolver.Resolve(_rooms, position);
            // The resolver falls back to the nearest room; outside every room there is no room's water.
            for (int i = 0; i < _rooms.Count; i++)
            {
                if (_rooms[i].Index != resolved)
                    continue;
                if (!_rooms[i].Contains(position))
                    return false;
                room = resolved;
                return true;
            }

            return false;
        }

        /// <summary>LostEden.Core <c>DungeonRoomSurface.BuildRooms</c>: the room's water meshes read into one room liquid.</summary>
        static RoomLiquid Build(IReadOnlyList<PlayfieldWaterEntry> water)
        {
            var meshes = new List<LiquidBuilder.Mesh>(water.Count);
            for (int i = 0; i < water.Count; i++)
            {
                PlayfieldWaterEntry entry = water[i];
                float[] floats = entry.Vertices ?? Array.Empty<float>();
                var vertices = new List<Vec3>(floats.Length / 3);
                for (int v = 0; v + 2 < floats.Length; v += 3)
                    vertices.Add(new Vec3(floats[v], floats[v + 1], floats[v + 2]));

                short[] triangles = entry.Triangles ?? Array.Empty<short>();
                var indices = new List<int>(triangles.Length);
                for (int t = 0; t < triangles.Length; t++)
                    indices.Add((ushort)triangles[t]);

                meshes.Add(new LiquidBuilder.Mesh(entry.Flags, vertices, indices));
            }

            return new RoomLiquid(LiquidBuilder.ReadMeshes(meshes));
        }
    }
}
