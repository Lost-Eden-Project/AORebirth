namespace ZoneEngine_New.Core.Quests.Dungeons.Static
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.Database.Domain.Dungeons;

    using LostEden.Protocol.GameData;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Walk-in dungeons behind world doors (<see cref="StaticDungeonCatalog"/>). Using or walking into an ACG entrance
    /// the catalog knows, with no mission key for a quest dungeon behind it, puts the player in that dungeon's one
    /// shared instance: every player at an entrance, teamed or not, meets in the same live instance. It is built off the
    /// tick on first entry and released by the same empty-dungeon sweep as quest dungeons. Instances are recorded in
    /// <c>staticdungeoninstances</c> so every zone instance shares them and a login inside one finds its entrance
    /// after a restart.
    /// </summary>
    public sealed class StaticDungeonService : IDisposable
    {
        /// <summary>The owner every instance row is recorded under: one instance per entrance, shared by everyone.</summary>
        const int OwnerShared = 2;
        const int SharedOwnerId = 0;

        /// <summary>Minimum time between one player's entrance requests: they can cost a database read.</summary>
        static readonly TimeSpan RequestCooldown = TimeSpan.FromMilliseconds(750);

        /// <summary>Rows older than this belong to instances a zone never released (a crash); the duty holder drops them.</summary>
        static readonly TimeSpan StaleRowAge = TimeSpan.FromDays(1);

        static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);

        const int PurgeBatch = 50;

        /// <summary>A live or building instance: the dungeon at an entrance.</summary>
        sealed record Instance(int PlayfieldId, MissionPlacementIdentity Entrance);

        readonly StaticDungeonCatalog _catalog;
        readonly MissionDestinationCatalog _entrances;
        readonly IStaticDungeonInstanceStore _store;
        readonly IItemInstanceIdAllocator _ids;
        readonly Lazy<PlayfieldManager> _playfields;
        readonly IGameData _gameData;
        readonly IZoneLogger _logger;
        readonly ZoneEngine_New.Core.World.WorldLink? _world;
        readonly ConcurrentDictionary<MissionPlacementIdentity, int> _byEntrance = new();
        readonly ConcurrentDictionary<int, Instance> _byPlayfield = new();
        readonly ConcurrentDictionary<int, long> _lastRequest = new();
        /// <summary>Instance builds in flight, by dungeon playfield: every entrant waits on the one build.</summary>
        readonly ConcurrentDictionary<int, Lazy<Task<bool>>> _pendingBuilds = new();
        readonly Timer _purge;
        int _purging;

        public StaticDungeonService(StaticDungeonCatalog catalog, MissionDestinationCatalog entrances, IStaticDungeonInstanceStore store,
            IItemInstanceIdAllocator ids, Lazy<PlayfieldManager> playfields, IGameData gameData, IZoneLogger logger,
            ZoneEngine_New.Core.World.WorldLink? world = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _entrances = entrances ?? throw new ArgumentNullException(nameof(entrances));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _world = world;
            _purge = new Timer(_ => PurgeStaleRows(), null, PurgeInterval, PurgeInterval);
        }

        public void Dispose() => _purge.Dispose();

        public StaticDungeonCatalog Catalog => _catalog;

        /// <summary>
        /// The player used or walked into a MissionEntrance that no carried mission key opened. When the catalog has a
        /// dungeon behind it, they are sent into its shared instance (built if needed). False when the entrance is
        /// not a static dungeon's, so other entrance handling can take the use.
        /// </summary>
        public bool TryEnter(Player player, Identity target)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (target.Type != IdentityType.ACGEntrance || !_catalog.TryGet(target, out StaticDungeonBlueprint blueprint))
                return false;

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.IsDead || player.Session is not IZoneSession session
                    || player.Playfield is not Playfield playfield || playfield is AcgDungeonPlayfield
                    || playfield.Identity.Instance != blueprint.Entrance.PlayfieldId
                    || !QuestDungeonService.WithinEntrance(player, blueprint.Entrance) || !TryBeginRequest(player))
                    return false;

                return Enter(player, session, playfield, target, blueprint);
            }
        }

        /// <summary>
        /// A GM enters the dungeon behind <paramref name="entranceInstance"/> from anywhere, into its shared instance (no
        /// range check; the use cooldown still applies). False when the catalog has no such entrance or the GM cannot
        /// be moved now.
        /// </summary>
        public bool TryEnterAsGm(Player player, uint entranceInstance)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!_catalog.TryGet((uint)IdentityType.ACGEntrance, entranceInstance, out StaticDungeonBlueprint blueprint))
                return false;
            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined || player.IsDead || player.Session is not IZoneSession session
                    || player.Playfield is not Playfield playfield || !TryBeginRequest(player))
                    return false;
                var target = new Identity { Type = IdentityType.ACGEntrance, Instance = unchecked((int)entranceInstance) };
                return Enter(player, session, playfield, target, blueprint, gm: true);
            }
        }

        bool Enter(Player player, IZoneSession session, Playfield playfield, Identity target, StaticDungeonBlueprint blueprint,
            bool gm = false)
        {
            PlayfieldManager playfields = _playfields.Value;
            // The dungeon's navmesh bakes while the player waits; the same mesh serves every instance of this entrance.
            _ = playfields.DungeonNavMeshes.GetOrStart(blueprint.Layout);

            if (!TryGetOrCreateInstance(blueprint, out Instance instance))
            {
                _logger.Warn("Static dungeon " + blueprint.Name + ": no instance id could be allocated; the entrance did nothing");
                return true;
            }

            int dungeonPlayfield = instance.PlayfieldId;
            // Already live here: the tick only hands the player over, facing into the entrance room.
            if (playfields.TryReuseStaticDungeon(dungeonPlayfield, out StaticDungeonPlayfield live))
                return session.TransferToPlayfield(live, live.Layout.Spawn, live.Layout.SpawnHeading);

            // World has not said where this instance runs: ask off the tick, then try the entrance again.
            ZoneEngine_New.Core.World.WorldLink? link = playfields.World;
            if (link != null && !link.HostsPlayfield(dungeonPlayfield))
            {
                _ = link.ResolveAsync(dungeonPlayfield).ContinueWith(resolve =>
                {
                    if (!resolve.IsCompletedSuccessfully || !resolve.Result.Accepted) return;
                    bool local = string.Equals(resolve.Result.Instance, link.Instance.Name, StringComparison.OrdinalIgnoreCase);
                    playfield.DispatchPlayerProjection(player, () =>
                    {
                        if (!ReferenceEquals(player.Playfield, playfield) || player.Session == null) return;
                        if (local) Reenter(player, target, gm);
                        else playfields.CrossInstance?.Start(player, dungeonPlayfield, blueprint.Layout.Spawn, blueprint.Layout.SpawnHeading,
                            AORebirth.World.Contracts.V1.ArrivalKind.ArrivalDungeon);
                    });
                }, TaskScheduler.Default);
                return true;
            }

            // Building costs several tick budgets (geometry, collision, navmesh wait, spawns, chests): on the thread pool,
            // then the entrance is tried again once the instance is live. Everyone who enters meanwhile waits on it.
            Task<bool> build = _pendingBuilds.GetOrAdd(dungeonPlayfield,
                _ => new Lazy<Task<bool>>(() => Task.Run(() => BuildOffTick(dungeonPlayfield, blueprint)))).Value;
            build.ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully || !done.Result) return;
                playfield.DispatchPlayerProjection(player, () =>
                {
                    if (!ReferenceEquals(player.Playfield, playfield) || player.Session == null) return;
                    Reenter(player, target, gm);
                });
            }, TaskScheduler.Default);
            return true;
        }

        /// <summary>
        /// The entrance's one instance: the one in memory, else the one the shared table holds (another zone instance may
        /// have made it), else a new one from the durable allocator, recorded in both. A raced insert (two zones, one
        /// door) re-reads the row and takes the winner's.
        /// </summary>
        internal bool TryGetOrCreateInstance(StaticDungeonBlueprint blueprint, out int playfieldId)
        {
            bool found = TryGetOrCreateInstance(blueprint, out Instance instance);
            playfieldId = found ? instance.PlayfieldId : 0;
            return found;
        }

        bool TryGetOrCreateInstance(StaticDungeonBlueprint blueprint, out Instance instance)
        {
            MissionPlacementIdentity key = blueprint.Entrance.Identity;
            if (_byEntrance.TryGetValue(key, out int known) && _byPlayfield.TryGetValue(known, out instance!))
                return true;

            StaticDungeonInstanceRow? row = _store.Find(blueprint.Entrance.IdentityType, blueprint.Entrance.IdentityInstance, OwnerShared, SharedOwnerId);
            if (row == null)
            {
                if (!StaticDungeonIds.TryCreate(_ids.Allocate(), out int playfieldId))
                {
                    instance = null!;
                    return false;
                }
                row = new StaticDungeonInstanceRow
                {
                    PlayfieldId = playfieldId, EntranceType = blueprint.Entrance.IdentityType, EntranceInstance = blueprint.Entrance.IdentityInstance,
                    OwnerKind = OwnerShared, OwnerId = SharedOwnerId, CreatedAtUtcTicks = DateTime.UtcNow.Ticks
                };
                if (!_store.TryInsert(row))
                    row = _store.Find(blueprint.Entrance.IdentityType, blueprint.Entrance.IdentityInstance, OwnerShared, SharedOwnerId) ?? row;
            }

            instance = new Instance(row.PlayfieldId, blueprint.Entrance.Identity);
            _byPlayfield[instance.PlayfieldId] = instance;
            _byEntrance[key] = instance.PlayfieldId;
            return true;
        }

        bool BuildOffTick(int dungeonPlayfield, StaticDungeonBlueprint blueprint)
        {
            try
            {
                StaticDungeonPlayfield world = _playfields.Value.GetOrCreateStaticDungeon(dungeonPlayfield, blueprint,
                    RandomNumberGenerator.GetInt32(int.MaxValue), OnReleased);
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Static dungeon instance live playfield={0} entrance={1} ({2}) placed npcs={3} chests={4} turrets={5}",
                    dungeonPlayfield, blueprint.Entrance.IdentityInstance, blueprint.Name, world.Placed.Npcs, world.Placed.Chests, world.Placed.Turrets));
                return true;
            }
            catch (ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException)
            {
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, string.Format(CultureInfo.InvariantCulture, "Static dungeon build failed playfield={0} entrance={1}",
                    dungeonPlayfield, blueprint.Entrance.IdentityInstance));
                return false;
            }
            finally
            {
                _pendingBuilds.TryRemove(dungeonPlayfield, out _);
            }
        }

        /// <summary>Tries the entrance again after off-tick work finished for it, without the use cooldown in the way.</summary>
        void Reenter(Player player, Identity target, bool gm)
        {
            _lastRequest.TryRemove(player.Identity.Instance, out _);
            if (gm)
                TryEnterAsGm(player, unchecked((uint)target.Instance));
            else
                TryEnter(player, target);
        }

        /// <summary>The instance was disposed (released): its records go, so the next entry builds a fresh one.</summary>
        internal void OnReleased(StaticDungeonPlayfield dungeon)
        {
            int playfieldId = dungeon.Identity.Instance;
            if (_byPlayfield.TryRemove(playfieldId, out Instance? instance))
                _byEntrance.TryRemove(new KeyValuePair<MissionPlacementIdentity, int>(instance.Entrance, playfieldId));
            _store.Delete(playfieldId);
        }

        /// <summary>
        /// Login inside a static instance: the live instance when it still runs here (the stored position stands);
        /// otherwise (released, or after a restart) in front of its entrance, where the exit door puts people down;
        /// otherwise (the entrance has no dungeon any more, or the row is gone) the respawn point.
        /// </summary>
        internal (Playfield Playfield, Vector3? Position, PlayfieldAdmission? Admission) ResolveLogin(int storedPlayfield)
        {
            if (!StaticDungeonIds.IsStaticDungeonPlayfield(storedPlayfield))
                throw new ArgumentOutOfRangeException(nameof(storedPlayfield));
            PlayfieldManager playfields = _playfields.Value;
            if (playfields.TryGetStaticDungeon(storedPlayfield, out StaticDungeonPlayfield live))
                return (live, null, null);

            MissionEntrancePlacement? entrance = null;
            if (_byPlayfield.TryGetValue(storedPlayfield, out Instance? instance))
                _entrances.TryGetByIdentity(instance.Entrance.IdentityType, instance.Entrance.IdentityInstance, out entrance!);
            else if (_store.Load(storedPlayfield) is { } row)
                _entrances.TryGetByIdentity(row.EntranceType, row.EntranceInstance, out entrance!);
            if (entrance != null && _catalog.TryGet(entrance.IdentityType, entrance.IdentityInstance, out _))
            {
                Vector3 landing = AcgDungeonPlayfield.ExitLanding(entrance, out _);
                PlayfieldAdmission admission = AcquireForLogin(playfields, entrance.PlayfieldId, landing);
                return (admission.Playfield, landing, admission);
            }

            RespawnContentCatalog respawn = _gameData.RespawnContent;
            if (respawn.PlayfieldId <= 0 || respawn.Position is not { Length: 3 })
                throw new InvalidOperationException("No respawn destination is configured for a login in a released static dungeon.");
            var respawnAt = new Vector3(respawn.Position[0], respawn.Position[1], respawn.Position[2]);
            PlayfieldAdmission respawnAdmission = AcquireForLogin(playfields, respawn.PlayfieldId, respawnAt);
            return (respawnAdmission.Playfield, respawnAt, respawnAdmission);
        }

        static PlayfieldAdmission AcquireForLogin(PlayfieldManager playfields, int playfieldId, Vector3 position)
        {
            try
            {
                return playfields.AcquirePlayfield(playfieldId);
            }
            catch (ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException)
            {
                throw new ZoneEngine_New.Core.World.LoginRelocationException(playfieldId, position);
            }
        }

        /// <summary>Releases an instance now if nobody is in it. False while occupied or when it is not live here.</summary>
        public bool TryReset(int playfieldId) => _playfields.Value.TryReleaseStaticDungeon(playfieldId);

        /// <summary>The live instances this zone instance runs, for GM listing: playfield, entrance, owner, players, NPCs left.</summary>
        public IReadOnlyList<(int PlayfieldId, StaticDungeonBlueprint Blueprint, string Owner, int Players, int NpcsLeft)> LiveInstances()
        {
            var rows = new List<(int, StaticDungeonBlueprint, string, int, int)>();
            foreach (StaticDungeonPlayfield dungeon in _playfields.Value.SnapshotStaticDungeons())
            {
                string owner = _byPlayfield.ContainsKey(dungeon.Identity.Instance) ? "shared" : "unknown";
                int players = 0;
                int npcs = 0;
                foreach (Dynel dynel in dungeon.GetRequiredService<DynelRegistry>().Dynels())
                {
                    if (dynel is Player) players++;
                    else if (dynel is NpcCharacter { IsDead: false }) npcs++;
                }
                rows.Add((dungeon.Identity.Instance, dungeon.Blueprint, owner, players, npcs));
            }
            return rows;
        }

        /// <summary>The owner every instance row is recorded under.</summary>
        internal static (int Kind, int Id) SharedOwner => (OwnerShared, SharedOwnerId);

        /// <summary>Tests: the instance id recorded for an entrance, or 0.</summary>
        internal int InstanceOf(MissionPlacementIdentity entrance)
            => _byEntrance.TryGetValue(entrance, out int playfieldId) ? playfieldId : 0;

        /// <summary>Tests: whether a build for <paramref name="playfieldId"/> is still running; waits for it when it is.</summary>
        internal bool WaitForBuild(int playfieldId, TimeSpan timeout)
            => !_pendingBuilds.TryGetValue(playfieldId, out Lazy<Task<bool>>? build) || build.Value.Wait(timeout);

        bool TryBeginRequest(Player player)
        {
            long now = DateTime.UtcNow.Ticks;
            int id = player.Identity.Instance;
            if (_lastRequest.TryGetValue(id, out long last) && now - last < RequestCooldown.Ticks)
                return false;
            _lastRequest[id] = now;
            return true;
        }

        /// <summary>Drops rows of instances no zone released (a crash): the database-wide half runs on World's duty holder only.</summary>
        void PurgeStaleRows()
        {
            if (Interlocked.Exchange(ref _purging, 1) == 1)
                return;
            try
            {
                long cutoff = DateTime.UtcNow.Ticks - RequestCooldown.Ticks;
                foreach (KeyValuePair<int, long> request in _lastRequest.ToArray())
                    if (request.Value < cutoff)
                        _lastRequest.TryRemove(request.Key, out _);

                if (_world != null && !_world.IsDutyHolder)
                    return;
                // A live instance older than a day keeps running from memory; only its row's cross-zone sharing is lost.
                _store.PurgeOlderThan(DateTime.UtcNow.Ticks - StaleRowAge.Ticks, PurgeBatch);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Static dungeon row purge failed; it runs again next interval");
            }
            finally
            {
                Volatile.Write(ref _purging, 0);
            }
        }
    }
}
