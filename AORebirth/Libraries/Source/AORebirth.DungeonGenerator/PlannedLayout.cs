namespace AORebirth.DungeonGenerator
{
    using Vector3 = System.Numerics.Vector3;

    using AORebirth.World.Collision;

    /// <summary>
    /// The planned layout (generator version 4). A floor is built the way a deck is drawn rather than grown doorway by
    /// doorway: its first room (the entrance, a lift arrival placed over the floor below's departure, or the boss room),
    /// then a spine of main halls running away from it, then real rooms hung off the spine. Connector pieces (end
    /// blocks, hallway bits) only bridge a single gap. No doorway is left with nothing behind it (the client draws it as
    /// a door into the void): it takes a one-door room, else a one-block cap set right on it, else the candidate is
    /// rejected. Several candidates per seed are built and the best scored one is kept, so the result is still a pure
    /// function of the request.
    /// </summary>
    public sealed partial class DungeonGenerator
    {
        /// <summary>Valid candidates built per seed before the best is chosen.</summary>
        const int PlannedCandidates = 6;

        /// <summary>Grid side for the planned layout: larger than classic, so real rooms (up to 9x9 blocks) fit.</summary>
        public static int PlannedGridSize(int floorSize) => Math.Clamp(14 + floorSize, 18, MaxGridBlocks);

        /// <summary>Real rooms a floor has at least: floor sizes 8-16 give 2-5 (live Alien Mothership floors had 3-5).</summary>
        public static int PlannedRoomTarget(int floorSize) => Math.Clamp((int)Math.Round(floorSize / 3.5, MidpointRounding.AwayFromZero), 2, 6);

        /// <summary>
        /// Share of the floor's grid its rooms should cover. Styles with small rooms (offices, clan bases) keep adding
        /// rooms past <see cref="PlannedRoomTarget"/> until they reach it, up to the floor size in rooms.
        /// </summary>
        const double PlannedAreaShare = 0.3;

        DungeonGenerationResult GeneratePlanned(DungeonGenerationRequest request, StyleRoomSet style, int floors, int floorSize)
        {
            int grid = PlannedGridSize(floorSize);
            DungeonGenerationResult? best = null;
            double bestScore = double.NegativeInfinity;
            int found = 0;
            for (int attempt = 0; attempt < MaxAttempts && found < PlannedCandidates; attempt++)
            {
                var random = new Random(unchecked(request.Seed + (attempt * 7919)));
                var builder = new LayoutBuilder(style, grid, grid, random);
                if (!builder.TryBuildPlanned(floors, floorSize, request.UseFloorTransitions, request.BossFloor))
                    continue;

                DungeonValidation validation = DungeonLayoutValidator.Validate(builder.Rooms, grid, grid);
                if (!validation.IsValid)
                    continue;

                DungeonGenerationResult result;
                try
                {
                    result = Complete(request, style, builder, grid, attempt + 1, validation);
                }
                catch (InvalidDataException)
                {
                    // A lift platform that would stand outside its room: this candidate is unusable, the next may not be.
                    continue;
                }

                if (!result.Validation.IsValid)
                    continue;
                found++;
                double score = builder.PlannedScore();
                if (score > bestScore)
                {
                    bestScore = score;
                    best = result;
                }
            }

            return best ?? throw new InvalidOperationException(
                "No valid planned dungeon for style " + request.StyleId + " seed " + request.Seed + " after " + MaxAttempts + " attempts.");
        }

        sealed partial class LayoutBuilder
        {
            readonly HashSet<int> _spine = [];
            readonly List<int> _floorFirst = [];

            /// <summary>Real rooms each floor is built towards (<see cref="PlannedRoomTarget"/>).</summary>
            int _roomTarget;

            /// <summary>Where the floor below's lift stands (x, z used): the next floor's first room is placed over it.</summary>
            Vector3? _liftTarget;

            public bool TryBuildPlanned(int floors, int floorSize, bool transitions, bool bossFloor)
            {
                _roomTarget = PlannedRoomTarget(floorSize);
                int halls = Math.Clamp(1 + (_roomTarget / 2), 2, 4);
                for (int floor = 0; floor < floors; floor++)
                {
                    NewFloor();
                    int first = _rooms.Count;
                    _floorFirst.Add(first);
                    if (bossFloor && floor == floors - 1)
                    {
                        if (!TryPlaceAligned(floor, _style.TransitionBosses, _style.Transitions?.BossRooms))
                            return false;
                        BossRoomIndex = first;
                        FloorArrivals.Add(first);
                        continue;
                    }

                    bool rooted = floor == 0
                        ? TryPlaceEntrance()
                        : transitions
                            ? TryPlaceAligned(floor, _style.TransitionArrivals, _style.Transitions?.ArrivalRooms)
                            : TryPlaceStart(floor);
                    if (!rooted)
                        return false;
                    FloorArrivals.Add(first);
                    if (!BuildSpine(floor, first, halls))
                        return false;

                    // The lift out goes in first, at the far end of the spine, so it is a destination and not a stub.
                    bool needsDeparture = transitions && floor < floors - 1;
                    int real = 0;
                    int departure = -1;
                    if (needsDeparture && TryAttachDeparture(floor, first, featuresOnly: true, out departure))
                        real++;
                    int areaGoal = (int)(_width * _height * PlannedAreaShare);
                    AttachRooms(floor, first, _roomTarget, floorSize, areaGoal, ref real);
                    if (needsDeparture)
                    {
                        if (departure < 0)
                            departure = ExistingDeparture(floor, first);
                        if (departure < 0 && !TryAttachDeparture(floor, first, featuresOnly: false, out departure))
                            return false;
                        FloorDepartures.Add(departure);
                        _liftTarget = LiftPosition(_rooms[departure],
                            _style.Transitions!.DepartureRooms.Single(row => row.Room == _rooms[departure].Shape.RoomId));
                    }

                    if (!CloseDoorways(floor))
                        return false;
                }

                return WayInDoor >= 0;
            }

            void NewFloor()
            {
                var occupancy = new int[_width, _height];
                for (int x = 0; x < _width; x++)
                    for (int z = 0; z < _height; z++)
                        occupancy[x, z] = -1;
                _occupancy.Add(occupancy);
                _open.Add([]);
            }

            /// <summary>
            /// Main halls running away from the floor's first room. On floor 0 every inward doorway of the entrance opens
            /// into a main hall (as in live layouts). Between two halls there is at most one connector piece.
            /// </summary>
            bool BuildSpine(int floor, int root, int halls)
            {
                var rootSlots = SlotsOf(floor, root);
                if (rootSlots.Count == 0)
                    return false;

                (double X, double Z) origin = Centroid(_rooms[root]);
                int placed = 0;
                int last = -1;
                if (floor == 0)
                {
                    foreach (var slot in rootSlots)
                    {
                        if (!_open[floor].ContainsKey(slot))
                            continue;
                        if (!TryFillBest(floor, slot, _style.MainHalls, fit => HallScore(fit, origin), out int hall))
                            return false;
                        _spine.Add(hall);
                        placed++;
                        if (last < 0)
                            last = hall;
                    }
                }
                else
                {
                    last = ExtendSpine(floor, root, origin);
                    if (last < 0)
                        return false;
                    _spine.Add(last);
                    placed++;
                }

                while (placed < halls)
                {
                    int next = ExtendSpine(floor, last, origin);
                    if (next < 0)
                    {
                        foreach (int from in _spine.Where(i => _rooms[i].Floor == floor)
                            .OrderByDescending(i => Distance(Centroid(_rooms[i]), origin)).ToList())
                        {
                            next = ExtendSpine(floor, from, origin);
                            if (next >= 0)
                                break;
                        }
                    }

                    if (next < 0)
                        break;
                    _spine.Add(next);
                    placed++;
                    last = next;
                }

                return placed > 0;
            }

            /// <summary>A main hall on one of <paramref name="from"/>'s doorways, the one leading farthest away first.</summary>
            int ExtendSpine(int floor, int from, (double X, double Z) origin)
            {
                var slots = SlotsOf(floor, from);
                Shuffle(slots);
                slots = slots.OrderByDescending(slot => Distance(Far(slot), origin)).ToList();
                foreach (var slot in slots)
                    if (TryFillBest(floor, slot, _style.MainHalls, fit => HallScore(fit, origin), out int hall))
                        return hall;
                foreach (var slot in slots)
                    if (TryBridge(floor, slot, _style.MainHalls, fit => HallScore(fit, origin), out int hall))
                        return hall;
                return -1;
            }

            /// <summary>
            /// Real rooms hung off the spine (and the floor's first room) until the floor has its target. A room with a
            /// door left may take one more one-door room behind it; rooms never chain further, so there are no mazes.
            /// </summary>
            void AttachRooms(int floor, int root, int target, int maxRooms, int areaGoal, ref int real)
            {
                var tried = new HashSet<(int X, int Z, int Facing)>();
                bool Satisfied(int count) => count >= maxRooms || (count >= target && FloorArea(floor) >= areaGoal);
                for (int guard = 0; guard < 128 && !Satisfied(real); guard++)
                {
                    var slots = _open[floor]
                        .Where(pair => (pair.Value.Room == root || _spine.Contains(pair.Value.Room)) && !tried.Contains(pair.Key))
                        .Select(pair => pair.Key).ToList();
                    if (slots.Count == 0)
                        break;
                    Shuffle(slots);
                    var slot = slots[0];
                    tried.Add(slot);
                    bool closing = real >= target - 1 && FloorArea(floor) >= areaGoal * 0.8;
                    if (!TryFillBest(floor, slot, _style.Features, fit => FeatureScore(fit, closing), out int room)
                        && !TryBridge(floor, slot, _style.Features, fit => FeatureScore(fit, closing), out room))
                        continue;

                    real++;
                    if (Satisfied(real))
                        break;
                    foreach (var next in SlotsOf(floor, room))
                    {
                        if (TryFillBest(floor, next, _style.OneDoorFeatures, fit => FeatureScore(fit, closing: true), out _))
                        {
                            real++;
                            break;
                        }
                    }
                }
            }

            /// <summary>
            /// Every doorway still open gets something behind it: a one-door room when one fits, else a one-block cap.
            /// False when one cannot be closed, so the candidate is dropped rather than left with a door into nothing.
            /// </summary>
            bool CloseDoorways(int floor)
            {
                foreach (var slot in _open[floor].Keys.ToList())
                {
                    if (!_open[floor].ContainsKey(slot))
                        continue;
                    if (!TryFillBest(floor, slot, _style.OneDoorFeatures, fit => FeatureScore(fit, closing: true), out _)
                        && !TryFillBest(floor, slot, _style.Caps, fit => -fit.NewSlots.Count, out _))
                        return false;
                }

                return _open[floor].Count == 0;
            }

            int FloorArea(int floor)
                => _rooms.Where(room => room.Floor == floor).Sum(room => room.Cells.Count);

            bool TryAttachDeparture(int floor, int root, bool featuresOnly, out int departure)
            {
                departure = -1;
                var pool = _style.TransitionDepartures.Where(room => !featuresOnly || !_style.Connectors.Contains(room)).ToList();
                if (pool.Count == 0)
                    return false;

                (double X, double Z) origin = Centroid(_rooms[root]);
                var slots = _open[floor].Keys.ToList();
                Shuffle(slots);
                slots = slots.OrderByDescending(slot => Distance(Far(slot), origin)).ToList();
                foreach (var slot in slots)
                    if (TryFillBest(floor, slot, pool, fit => FeatureScore(fit, closing: true), out departure))
                        return true;
                foreach (var slot in slots)
                    if (TryBridge(floor, slot, pool, fit => FeatureScore(fit, closing: true), out departure))
                        return true;
                return false;
            }

            /// <summary>The deepest departure-capable room already on the floor (never its first room, which is the arrival).</summary>
            int ExistingDeparture(int floor, int first)
                => Enumerable.Range(first + 1, _rooms.Count - first - 1)
                    .Where(i => _rooms[i].Floor == floor && _style.TransitionDepartures.Contains(_rooms[i].Shape))
                    .OrderByDescending(i => _depths[i]).FirstOrDefault(-1);

            /// <summary>
            /// The floor's first room (lift arrival or boss room) placed so its platform stands as near as possible over
            /// the floor below's departure platform, so floors stack like decks of one hull.
            /// </summary>
            bool TryPlaceAligned(int floor, IReadOnlyList<RoomShape> pool, IReadOnlyList<DungeonTransitionRoom>? roles)
            {
                if (_liftTarget is not Vector3 target || roles == null || pool.Count == 0)
                    return TryPlaceStart(floor, pool.Count > 0 ? pool : null);

                Fit? best = null;
                double bestDistance = double.MaxValue;
                foreach (RoomShape shape in pool)
                {
                    DungeonTransitionRoom? role = roles.FirstOrDefault(row => row.Room == shape.RoomId);
                    if (role == null)
                        continue;
                    for (int rotation = 0; rotation < 4; rotation++)
                    {
                        (int w, int h) = Geometry.RotatedBlocks(shape, rotation);
                        for (int bx = 0; bx + w <= _width; bx++)
                        {
                            for (int bz = 0; bz + h <= _height; bz++)
                            {
                                Vector3 lift = LiftPosition(new PlacedRoom(shape, floor, bx, bz, rotation), role);
                                double dx = lift.X - target.X;
                                double dz = lift.Z - target.Z;
                                double distance = (dx * dx) + (dz * dz) + (_random.NextDouble() * 4);
                                if (distance >= bestDistance || !TryFit(floor, shape, rotation, bx, bz, wayInDoor: -1, out Fit fit))
                                    continue;
                                best = fit;
                                bestDistance = distance;
                            }
                        }
                    }
                }

                if (best == null)
                    return TryPlaceStart(floor, pool);
                Commit(best);
                return true;
            }

            /// <summary>Where a room's floor platform stands (the same placement the finished layout's endpoints use).</summary>
            Vector3 LiftPosition(PlacedRoom room, DungeonTransitionRoom role)
            {
                var spec = new DungeonRoomSpec
                {
                    RoomId = (ushort)room.Shape.RoomId,
                    Floor = 0,
                    GridX = (byte)room.BlockX,
                    GridZ = (byte)(_height - room.BlockZ - room.BlocksZ),
                    Facing = (byte)room.Rotation
                };
                StyleRoomTemplate template = room.Shape.Template;
                Vector3 placed = DungeonRoomPlacer.Place(template, spec, 2f, _width, _height, 0, 0);
                return placed + DungeonRoomPlacer.RotateY(role.LocalPosition, (room.Rotation - template.TemplateFacing) & 3);
            }

            /// <summary>Commits the best scored room from <paramref name="pool"/> on <paramref name="slot"/>.</summary>
            bool TryFillBest(int floor, (int X, int Z, int Facing) slot, IReadOnlyList<RoomShape> pool, Func<Fit, double> score,
                out int index)
            {
                index = -1;
                List<Fit> fits = FitsFor(floor, slot, pool);
                if (fits.Count == 0)
                    return false;

                Fit best = fits[0];
                double bestScore = double.NegativeInfinity;
                foreach (Fit fit in fits)
                {
                    double value = score(fit) + (_random.NextDouble() * 0.5);
                    if (value > bestScore)
                    {
                        bestScore = value;
                        best = fit;
                    }
                }

                Commit(best);
                index = _rooms.Count - 1;
                return true;
            }

            /// <summary>
            /// One two-door connector on <paramref name="slot"/> with a room from <paramref name="pool"/> right behind it;
            /// the connector is taken back when no such room fits, so a bridge never ends in nothing.
            /// </summary>
            bool TryBridge(int floor, (int X, int Z, int Facing) slot, IReadOnlyList<RoomShape> pool, Func<Fit, double> score,
                out int index)
            {
                index = -1;
                List<Fit> bridges = FitsFor(floor, slot, _style.Bridges);
                Shuffle(bridges);
                bridges = bridges.OrderByDescending(fit => fit.NewSlots.Any(next => next.Facing == slot.Facing) ? 1 : 0).ToList();
                foreach (Fit bridge in bridges)
                {
                    var snapshot = Snapshot(floor);
                    Commit(bridge);
                    int connector = _rooms.Count - 1;
                    foreach (var next in SlotsOf(floor, connector))
                        if (TryFillBest(floor, next, pool, score, out index))
                            return true;
                    Rollback(floor, snapshot);
                }

                return false;
            }

            List<Fit> FitsFor(int floor, (int X, int Z, int Facing) slot, IReadOnlyList<RoomShape> pool)
            {
                int wanted = (slot.Facing + 2) & 3;
                var fits = new List<Fit>();
                foreach (RoomShape shape in pool)
                {
                    for (int rotation = 0; rotation < 4; rotation++)
                    {
                        foreach (PlacedDoor door in Geometry.PlaceDoors(shape, rotation, 0, 0))
                        {
                            if (door.Facing != wanted)
                                continue;
                            int dx = slot.X - door.X;
                            int dz = slot.Z - door.Z;
                            if (dx % 10 != 0 || dz % 10 != 0)
                                continue;
                            if (TryFit(floor, shape, rotation, dx / 10, dz / 10, wayInDoor: -1, out Fit fit))
                                fits.Add(fit);
                        }
                    }
                }

                return fits;
            }

            (int Count, Dictionary<(int X, int Z, int Facing), (int Room, int Door)> Open) Snapshot(int floor)
                => (_rooms.Count, new Dictionary<(int X, int Z, int Facing), (int Room, int Door)>(_open[floor]));

            void Rollback(int floor, (int Count, Dictionary<(int X, int Z, int Facing), (int Room, int Door)> Open) snapshot)
            {
                int[,] occupancy = _occupancy[floor];
                for (int i = _rooms.Count - 1; i >= snapshot.Count; i--)
                {
                    foreach ((int x, int z) in _rooms[i].Cells)
                        occupancy[x, z] = -1;
                    _rooms.RemoveAt(i);
                    _depths.RemoveAt(i);
                    _spine.Remove(i);
                }

                _open[floor] = snapshot.Open;
            }

            /// <summary>A hall that leads away from the floor's first room and can lead on; unused hall shapes first.</summary>
            double HallScore(Fit fit, (double X, double Z) origin)
            {
                int used = _rooms.Count(room => room.Shape == fit.Room.Shape);
                return Distance(Centroid(fit.Room), origin) + (used == 0 ? 3 : 0) + (fit.NewSlots.Count > 0 ? 2 : -4);
            }

            /// <summary>
            /// Rooms not used yet first, bigger rooms slightly first; each doorway a room leaves open counts against it (it
            /// needs a room or a cap behind it later), more so when the floor is closing; joining two open doorways (a loop)
            /// counts for it.
            /// </summary>
            double FeatureScore(Fit fit, bool closing)
            {
                RoomShape shape = fit.Room.Shape;
                int used = _rooms.Count(room => room.Shape == shape);
                double score = (used == 0 ? 4 : -3 * used) + (Math.Min(shape.FootprintBlocks, 60) * 0.03);
                score -= fit.NewSlots.Count * (closing ? 1.5 : 0.4);
                if (fit.Pairs.Count > 1)
                    score += 1;
                return score;
            }

            /// <summary>
            /// How architectural the candidate is: real and varied rooms count for it; connector pieces, one-block caps, a
            /// room count off target, sprawl, and floors that do not sit over each other count against it.
            /// </summary>
            public double PlannedScore()
            {
                double score = 0;
                HashSet<(int X, int Z)>? below = null;
                for (int floor = 0; floor < _floorFirst.Count; floor++)
                {
                    int first = _floorFirst[floor];
                    int end = floor + 1 < _floorFirst.Count ? _floorFirst[floor + 1] : _rooms.Count;
                    var rooms = _rooms.GetRange(first, end - first);
                    var cells = rooms.SelectMany(room => room.Cells).ToHashSet();
                    double overlap = below == null || cells.Count == 0 ? 0 : cells.Count(below.Contains) / (double)cells.Count;
                    if (BossRoomIndex == first)
                    {
                        score += 4 * overlap;
                        continue;
                    }

                    int connectors = rooms.Count(room => _style.Connectors.Contains(room.Shape));
                    int deadEnds = rooms.Count(room => _style.Connectors.Contains(room.Shape) && room.Doors.Count == 1);
                    var features = rooms.Where(room => _style.Features.Contains(room.Shape)).ToList();
                    int distinct = features.Select(room => room.Shape).Distinct().Count();
                    int width = cells.Max(cell => cell.X) - cells.Min(cell => cell.X) + 1;
                    int depth = cells.Max(cell => cell.Z) - cells.Min(cell => cell.Z) + 1;
                    double density = cells.Count / (double)(width * depth);
                    score += (3 * features.Count) + (1.5 * distinct) - (2.5 * connectors) - (4 * deadEnds)
                        + (6 * density) - (3 * Math.Abs(features.Count - _roomTarget)) + (4 * overlap);
                    below = cells;
                }

                return score;
            }

            static (double X, double Z) Centroid(PlacedRoom room)
                => (room.Cells.Average(cell => cell.X) + 0.5, room.Cells.Average(cell => cell.Z) + 0.5);

            /// <summary>The point just past a doorway, in blocks.</summary>
            static (double X, double Z) Far((int X, int Z, int Facing) slot)
            {
                (int dx, int dz) = Geometry.Direction(slot.Facing);
                return ((slot.X / 10.0) + (dx * 0.5), (slot.Z / 10.0) + (dz * 0.5));
            }

            static double Distance((double X, double Z) a, (double X, double Z) b)
                => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
        }
    }
}
