namespace AORebirth.DungeonGenerator
{
    /// <summary>Outcome of checking a layout the way the client does.</summary>
    public sealed class DungeonValidation
    {
        public int PairedDoors { get; init; }

        /// <summary>Doors facing an empty block inside the grid: the client seals them and leaves them out.</summary>
        public int SealedDoors { get; init; }

        /// <summary>Doors with nothing to pair with that are not sealed. Exactly one (the way in) is valid.</summary>
        public IReadOnlyList<(int Room, int Door)> Unmatched { get; init; } = [];

        /// <summary>Rooms overlapping another room's blocks, or outside the grid.</summary>
        public IReadOnlyList<int> BadRooms { get; init; } = [];

        public IReadOnlyList<int> UnreachableRooms { get; init; } = [];

        public IReadOnlyList<int> BadTransitions { get; init; } = [];

        public bool IsValid => Unmatched.Count == 1 && BadRooms.Count == 0
            && UnreachableRooms.Count == 0 && BadTransitions.Count == 0;
    }

    /// <summary>
    /// The client's legality test (Gamecode.dll FUN_100c92b3), applied to our own model rather than trusting the
    /// generator's bookkeeping: a door pairs with a door on the same floor at the same position facing the opposite
    /// way; a door facing an empty block inside the grid is sealed; the layout is valid only when exactly one door in
    /// the whole dungeon is left unmatched, which is the way in (it faces out of the grid).
    /// </summary>
    public static class DungeonLayoutValidator
    {
        /// <summary>Door legality plus reachability from the entry room through doors and explicit floor platforms.</summary>
        public static DungeonValidation ValidateConnected(IReadOnlyList<PlacedRoom> rooms, int gridWidth,
            int gridHeight, IReadOnlyList<DungeonFloorTransition> transitions)
        {
            ArgumentNullException.ThrowIfNull(transitions);
            DungeonValidation geometry = Validate(rooms, gridWidth, gridHeight);
            var edges = Enumerable.Range(0, rooms.Count).Select(_ => new HashSet<int>()).ToArray();
            for (int from = 0; from < rooms.Count; from++)
            {
                for (int to = from + 1; to < rooms.Count; to++)
                {
                    if (rooms[from].Floor == rooms[to].Floor
                        && rooms[from].Doors.Any(door => rooms[to].Doors.Any(door.Meets)))
                        Connect(from, to);
                }
            }

            var badTransitions = new List<int>();
            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < transitions.Count; i++)
            {
                DungeonFloorTransition transition = transitions[i];
                int from = transition.From.RoomIndex;
                int to = transition.To.RoomIndex;
                if (from < 0 || from >= rooms.Count || to < 0 || to >= rooms.Count
                    || Math.Abs(rooms[from].Floor - rooms[to].Floor) != 1
                    || !seen.Add((Math.Min(from, to), Math.Max(from, to)))
                    || !ValidEndpoint(transition.From) || !ValidEndpoint(transition.To))
                {
                    badTransitions.Add(i);
                    continue;
                }

                Connect(from, to);
            }

            var reached = new HashSet<int>();
            var pending = new Queue<int>();
            if (rooms.Count > 0)
            {
                reached.Add(0);
                pending.Enqueue(0);
            }
            while (pending.TryDequeue(out int room))
            {
                foreach (int next in edges[room])
                    if (reached.Add(next))
                        pending.Enqueue(next);
            }

            return new DungeonValidation
            {
                PairedDoors = geometry.PairedDoors,
                SealedDoors = geometry.SealedDoors,
                Unmatched = geometry.Unmatched,
                BadRooms = geometry.BadRooms,
                UnreachableRooms = Enumerable.Range(0, rooms.Count).Where(room => !reached.Contains(room)).ToArray(),
                BadTransitions = badTransitions
            };

            void Connect(int from, int to)
            {
                edges[from].Add(to);
                edges[to].Add(from);
            }

            static bool ValidEndpoint(DungeonTransitionEndpoint endpoint)
                => endpoint.PlatformTemplateId > 0 && endpoint.ButtonTemplateId > 0
                    && float.IsFinite(endpoint.Position.X) && float.IsFinite(endpoint.Position.Y)
                    && float.IsFinite(endpoint.Position.Z);
        }

        public static DungeonValidation Validate(IReadOnlyList<PlacedRoom> rooms, int gridWidth, int gridHeight)
        {
            ArgumentNullException.ThrowIfNull(rooms);
            var owner = new Dictionary<(int Floor, int X, int Z), int>();
            var badRooms = new List<int>();
            for (int r = 0; r < rooms.Count; r++)
            {
                foreach ((int x, int z) in rooms[r].Cells)
                {
                    if (x < 0 || z < 0 || x >= gridWidth || z >= gridHeight || !owner.TryAdd((rooms[r].Floor, x, z), r))
                    {
                        if (!badRooms.Contains(r)) badRooms.Add(r);
                    }
                }
            }

            var doors = new List<(int Room, int Door, int Floor, PlacedDoor Placed)>();
            for (int r = 0; r < rooms.Count; r++)
                for (int d = 0; d < rooms[r].Doors.Count; d++)
                    doors.Add((r, d, rooms[r].Floor, rooms[r].Doors[d]));

            var used = new bool[doors.Count];
            int paired = 0;
            int sealedCount = 0;
            var unmatched = new List<(int, int)>();
            for (int i = 0; i < doors.Count; i++)
            {
                if (used[i])
                    continue;

                (int room, int door, int floor, PlacedDoor placed) = doors[i];
                (int fx, int fz) = placed.FarBlock;
                bool inGrid = fx >= 0 && fz >= 0 && fx < gridWidth && fz < gridHeight;
                if (inGrid && !owner.ContainsKey((floor, fx, fz)))
                {
                    sealedCount++;
                    used[i] = true;
                    continue;
                }

                int partner = -1;
                for (int j = 0; j < doors.Count && partner < 0; j++)
                {
                    if (j != i && !used[j] && doors[j].Floor == floor && doors[j].Room != room && placed.Meets(doors[j].Placed))
                        partner = j;
                }

                used[i] = true;
                if (partner >= 0)
                {
                    used[partner] = true;
                    paired++;
                }
                else
                    unmatched.Add((room, door));
            }

            return new DungeonValidation { PairedDoors = paired, SealedDoors = sealedCount, Unmatched = unmatched, BadRooms = badRooms };
        }
    }
}
