namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using LostEden.Protocol.GameData;

    using Utility.Config;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Metrics;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Trade;
    using ZoneEngine_New.Core.Teams;
    using ZoneEngine_New.Core.WorldSimulation;
    using AORebirth.Core.GameData;
    using AORebirth.Interfaces.Persistence.Missions;
    using AORebirth.Interfaces.Persistence.Shops;

    public sealed class PlayfieldManager : IDisposable
    {
        public const int DefaultLinkDeadTimeoutSeconds = 60;
        internal static readonly TimeSpan EmptyPlayfieldLifetime = TimeSpan.FromMinutes(10);

        private readonly Lock _sync = new();
        private readonly Timer _emptyPlayfieldSweep;
        private int _sweepingEmptyPlayfields;
        private readonly Dictionary<int, Playfield> _playfields = new();
        private readonly Dictionary<int, Player> _playersByCharacterId = new();

        /// <summary>Playfields being built off every tick thread; one build per id, shared by every waiter.</summary>
        private readonly ConcurrentDictionary<int, Lazy<Task<Playfield>>> _building = new();

        /// <summary>Players waiting on a background build to move (character id to playfield id): one move at a time.</summary>
        private readonly ConcurrentDictionary<int, int> _pendingMoveByCharacter = new();

        /// <summary>When each quest dungeon was first seen empty (UTC ticks); absent while occupied or just requested.</summary>
        private readonly Dictionary<int, long> _dungeonEmptySince = new();

        /// <summary>Playfields being reloaded in place (<see cref="TryStartReload"/>): the old instance, until it is disposed.</summary>
        private readonly Dictionary<int, Playfield> _reloading = new();
        private readonly IZoneLogger _logger;
        private readonly IMessageRouter _router;
        private readonly PlayerHydrator _playerHydrator;
        private readonly IGameData _gameData;
        private readonly IItemBuilder _items;
        private readonly HashItemMinter _hashItems;
        private readonly IInventoryRepository _inventoryRepository;
        private readonly IItemInstanceIdAllocator _instanceIds;
        private readonly InventoryMoveService _inventoryMoves;
        private readonly InventoryFlushService _inventoryFlush;
        private readonly TradeService _trades;
        private readonly CharacterSnapshotService _characterSnapshot;
        private readonly IPlayfieldMetricsRegistry _metricsRegistry;
        private readonly IShopDao _shopDao;
        private readonly ZoneEngine_New.Core.World.CrossInstanceTransfer? _crossInstance;
        private bool _disposed;

        public PlayfieldManager(
            IZoneLogger logger,
            IMessageRouter router,
            PlayerHydrator playerHydrator,
            IGameData gameData,
            IItemBuilder items,
            HashItemMinter hashItems,
            IInventoryRepository inventoryRepository,
            IItemInstanceIdAllocator instanceIds,
            InventoryMoveService inventoryMoves,
            InventoryFlushService inventoryFlush,
            TradeService trades,
            CharacterSnapshotService characterSnapshot,
            IPlayfieldMetricsRegistry metricsRegistry,
            TeamService teams,
            IItemTemplateCatalog itemTemplates,
            IShopDao shopDao,
            Quests.QuestService? quests = null,
            ZoneEngine_New.Core.Quests.Dungeons.QuestDungeonService? questDungeons = null,
            ZoneEngine_New.Core.Knubot.KnubotService? knubot = null,
            ZoneEngine_New.Core.Configuration.GameplaySettings? gameplay = null,
            ZoneEngine_New.Core.Modules.ZoneModuleHost? modules = null,
            ZoneEngine_New.Core.Chat.VicinityChatRelay? vicinity = null,
            ZoneEngine_New.Core.World.WorldLink? world = null,
            ZoneEngine_New.Core.World.CrossInstanceTransfer? crossInstance = null,
            ZoneEngine_New.Core.NpcScripts.NpcScriptCatalog? npcScripts = null,
            ZoneEngine_New.Core.Quests.Dungeons.Static.StaticDungeonService? staticDungeons = null,
            ZoneEngine_New.Core.Teams.SummonService? summons = null)
        {
            World = world;
            Summons = summons;
            StaticDungeons = staticDungeons;
            _crossInstance = crossInstance;
            Vicinity = vicinity;
            NpcScripts = npcScripts;
            QuestDungeons = questDungeons;
            Knubot = knubot;
            Gameplay = gameplay;
            ModuleEvents = modules?.Count > 0 ? modules.Events : null;
            ModuleWorld = modules?.Count > 0 ? modules.World : null;
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(router);
            ArgumentNullException.ThrowIfNull(playerHydrator);
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(hashItems);
            ArgumentNullException.ThrowIfNull(inventoryRepository);
            ArgumentNullException.ThrowIfNull(instanceIds);
            ArgumentNullException.ThrowIfNull(inventoryMoves);
            ArgumentNullException.ThrowIfNull(inventoryFlush);
            ArgumentNullException.ThrowIfNull(trades);
            ArgumentNullException.ThrowIfNull(characterSnapshot);
            ArgumentNullException.ThrowIfNull(metricsRegistry);
            ArgumentNullException.ThrowIfNull(shopDao);

            _logger = logger;
            _router = router;
            _playerHydrator = playerHydrator;
            _gameData = gameData;
            _items = items;
            _hashItems = hashItems;
            _inventoryRepository = inventoryRepository;
            _instanceIds = instanceIds;
            _inventoryMoves = inventoryMoves;
            _inventoryFlush = inventoryFlush;
            _trades = trades;
            _characterSnapshot = characterSnapshot;
            _metricsRegistry = metricsRegistry;
            _shopDao = shopDao;
            Teams = teams ?? throw new ArgumentNullException(nameof(teams));
            ItemTemplates = itemTemplates ?? throw new ArgumentNullException(nameof(itemTemplates));
            DungeonNavMeshes = new ZoneEngine_New.Core.Quests.Dungeons.DungeonNavMeshCache(gameData.RootPath, logger);
            Quests = quests;
            // A reload that gives a visible NPC a script, or takes it away, is told to its observers on each tick.
            if (knubot != null)
                knubot.Catalog.Reloaded += change => ZoneEngine_New.Core.Knubot.KnubotNpcRefresh.Queue(this, change);
            _emptyPlayfieldSweep = new Timer(
                _ => SweepEmptyPlayfields(DateTime.UtcNow),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
        }

        public TeamService Teams { get; }

        /// <summary>This instance's World link; null in tools and tests that run one isolated engine.</summary>
        public ZoneEngine_New.Core.World.WorldLink? World { get; }

        /// <summary>Moves to playfields another zone instance runs; null without a World link.</summary>
        public ZoneEngine_New.Core.World.CrossInstanceTransfer? CrossInstance => _crossInstance;

        /// <summary>Knubot NPC conversations, ticked on every playfield owner.</summary>
        public ZoneEngine_New.Core.Knubot.KnubotService? Knubot { get; }

        /// <summary>Vicinity chat, for NPCs speaking (see <see cref="Chat.VicinityChatRelay.Speak"/>).</summary>
        public ZoneEngine_New.Core.Chat.VicinityChatRelay? Vicinity { get; }

        /// <summary>NPC scripts (GameData/NpcScripts), handed to every playfield's services so spawns can bind to them.</summary>
        public ZoneEngine_New.Core.NpcScripts.NpcScriptCatalog? NpcScripts { get; }

        public IItemTemplateCatalog ItemTemplates { get; }

        /// <summary>Navmeshes for generated dungeons, baked off the tick and kept by layout.</summary>
        public ZoneEngine_New.Core.Quests.Dungeons.DungeonNavMeshCache DungeonNavMeshes { get; }

        /// <summary>Root quest service, forwarded into every playfield container.</summary>
        public Quests.QuestService? Quests { get; }

        /// <summary>Root quest dungeon service, forwarded into every playfield container.</summary>
        public ZoneEngine_New.Core.Quests.Dungeons.QuestDungeonService? QuestDungeons { get; }

        /// <summary>Root static dungeon service, forwarded into every playfield container (walking into a static dungeon's door).</summary>
        public ZoneEngine_New.Core.Quests.Dungeons.Static.StaticDungeonService? StaticDungeons { get; }

        /// <summary>Engineer summons' route to characters on other zone instances, forwarded into every playfield container.</summary>
        public ZoneEngine_New.Core.Teams.SummonService? Summons { get; }

        /// <summary>Root Gameplay.json settings, forwarded into every playfield container.</summary>
        public ZoneEngine_New.Core.Configuration.GameplaySettings? Gameplay { get; }

        /// <summary>External module event handlers, forwarded into every playfield container; null when no module is loaded.</summary>
        public ZoneEngine_New.Core.Modules.ZoneModuleEvents? ModuleEvents { get; }

        /// <summary>The module world view, published from every playfield tick; null when no module is loaded.</summary>
        internal ZoneEngine_New.Core.Modules.ZoneModuleWorld? ModuleWorld { get; }

        public static TimeSpan ResolveLinkDeadTimeout()
        {
            Config? config = ConfigReadWrite.Instance.CurrentConfig;
            int seconds = config == null ? 0 : config.LinkDeadTimeoutSeconds;
            if (seconds <= 0)
                seconds = DefaultLinkDeadTimeoutSeconds;

            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// The playfield, building it on the calling thread when it is not loaded. A tick thread should not build: use
        /// <see cref="WithPlayfield"/>. Joins a background build already running for the same id instead of building
        /// it twice.
        /// </summary>
        public Playfield GetOrCreate(int playfieldId)
        {
            if (_building.TryGetValue(playfieldId, out Lazy<Task<Playfield>>? pending))
                return pending.Value.GetAwaiter().GetResult();

            return GetOrCreateCore(playfieldId);
        }

        /// <summary>Reserves a live destination until the caller has queued its admission or transfer.</summary>
        internal PlayfieldAdmission AcquirePlayfield(int playfieldId)
        {
            while (true)
            {
                Playfield playfield = GetOrCreate(playfieldId);
                PlayfieldAdmission? admission;
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_playfields.TryGetValue(playfieldId, out Playfield? current)
                        || !ReferenceEquals(current, playfield))
                        continue;

                    admission = playfield.TryAcquireAdmission();
                }

                if (admission != null)
                    return admission;

                // Retirement owns disposal and cache eviction. Never wait while holding the manager lock.
                playfield.IdleRetirementCompleted.GetAwaiter().GetResult();
            }
        }

        private PlayfieldAdmission? TryAcquireLoadedPlayfield(int playfieldId)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _playfields.TryGetValue(playfieldId, out Playfield? playfield)
                    ? playfield.TryAcquireAdmission()
                    : null;
            }
        }

        /// <summary>
        /// Runs <paramref name="then"/> with the destination on <paramref name="player"/>'s own playfield tick. When the
        /// destination is loaded that is right now, on the caller's thread, exactly as a direct call. Otherwise the
        /// destination is built on a background thread (no tick waits on it) and <paramref name="then"/> is queued to
        /// the player's playfield once it is ready; it must re-check anything that may have changed meanwhile (session,
        /// playfield, death). False when the player already has a move waiting on a build; the request is dropped.
        /// </summary>
        public bool WithPlayfield(int playfieldId, Player player, Action<Playfield> then,
            ZoneEngine_New.Core.World.RemoteArrival? remote = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(then);

            using (PlayfieldAdmission? loaded = TryAcquireLoadedPlayfield(playfieldId))
            {
                if (loaded != null)
                {
                    then(loaded.Playfield);
                    return true;
                }
            }

            int characterId = player.Identity.Instance;
            if (!_pendingMoveByCharacter.TryAdd(characterId, playfieldId))
                return false;

            // Not loaded here: World says which instance runs it. Another instance means a cross-instance move.
            if (World != null && !World.HostsPlayfield(playfieldId))
            {
                ResolveThenMove(playfieldId, player, then, remote);
                return true;
            }

            BuildThenMove(playfieldId, player, then);
            return true;
        }

        void ResolveThenMove(int playfieldId, Player player, Action<Playfield> then, ZoneEngine_New.Core.World.RemoteArrival? remote)
        {
            int characterId = player.Identity.Instance;
            Playfield? source = player.Playfield;
            World!.ResolveAsync(playfieldId).ContinueWith(resolve =>
            {
                AORebirth.World.Contracts.V1.ResolveReply? reply = resolve.IsCompletedSuccessfully ? resolve.Result : null;
                if (reply == null || !reply.Accepted)
                {
                    _pendingMoveByCharacter.TryRemove(characterId, out _);
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                        "Playfield {0} could not be resolved for character {1}: {2}; the character stays put", playfieldId,
                        characterId, reply?.Reason ?? resolve.Exception?.GetBaseException().GetType().Name ?? "cancelled"));
                    return;
                }

                if (string.Equals(reply.Instance, World.Instance.Name, StringComparison.OrdinalIgnoreCase))
                {
                    BuildThenMove(playfieldId, player, then);
                    return;
                }

                _pendingMoveByCharacter.TryRemove(characterId, out _);
                if (source == null || remote == null || _crossInstance == null)
                {
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                        "Playfield {0} runs on {1}; this move of character {2} has no cross-instance landing", playfieldId,
                        reply.Instance, characterId));
                    return;
                }

                source.DispatchPlayerProjection(player, () =>
                {
                    if (!ReferenceEquals(player.Playfield, source) || player.Session == null) return;
                    if (remote.Prepare != null && !remote.Prepare()) return;
                    _crossInstance.Start(player, playfieldId, remote.Landing, remote.Heading ?? player.Rotation,
                        remote.Kind, remote.ExitProxyDoor);
                });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        void BuildThenMove(int playfieldId, Player player, Action<Playfield> then)
        {
            int characterId = player.Identity.Instance;

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Playfield {0} building in background for character {1}",
                    playfieldId,
                    characterId));

            BuildInBackground(playfieldId).ContinueWith(
                build =>
                {
                    if (!build.IsCompletedSuccessfully)
                    {
                        _pendingMoveByCharacter.TryRemove(characterId, out _);
                        _logger.Error(
                            build.Exception?.GetBaseException() ?? new InvalidOperationException("Playfield build was cancelled."),
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Background build of playfield {0} failed; character {1} stays put",
                                playfieldId,
                                characterId));
                        return;
                    }

                    Playfield? owner = player.Playfield;
                    if (owner == null)
                    {
                        _pendingMoveByCharacter.TryRemove(characterId, out _);
                        return;
                    }

                    owner.DispatchPlayerProjection(player, () =>
                    {
                        using PlayfieldAdmission? admission = TryAcquireLoadedPlayfield(playfieldId);
                        _pendingMoveByCharacter.TryRemove(characterId, out _);
                        if (admission == null)
                        {
                            // A destination can retire before this owner processes its projection.
                            // Rebuild off the tick rather than use a disposed reference or wait here.
                            WithPlayfield(playfieldId, player, then);
                            return;
                        }

                        then(admission.Playfield);
                    });
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        Task<Playfield> BuildInBackground(int playfieldId)
        {
            Lazy<Task<Playfield>> build = _building.GetOrAdd(
                playfieldId,
                id => new Lazy<Task<Playfield>>(
                    () => Task.Run(() =>
                    {
                        try
                        {
                            return GetOrCreateCore(id);
                        }
                        finally
                        {
                            _building.TryRemove(id, out _);
                        }
                    }),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return build.Value;
        }

        private Playfield GetOrCreateCore(int playfieldId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playfieldId);

            // A quest dungeon id must never become an ordinary empty playfield (login, teleport or a crafted route).
            if (ZoneEngine_New.Core.Quests.Dungeons.QuestDungeonIds.IsDungeonPlayfield(playfieldId))
                throw new InvalidOperationException("A quest dungeon playfield requires its quest's generated layout.");

            RequireHostedHere(playfieldId);

            while (true)
            {
                Task? retirement = null;
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    if (_playfields.TryGetValue(playfieldId, out Playfield? existing))
                    {
                        if (!existing.IsDisposed)
                            return existing;

                        retirement = existing.IdleRetirementCompleted;
                    }
                }

                if (retirement == null)
                    break;

                retirement.GetAwaiter().GetResult();
            }

            Playfield created = ConstructPlayfield(playfieldId);

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_playfields.TryGetValue(playfieldId, out Playfield? raced))
                {
                    created.Dispose();
                    return raced;
                }

                _playfields[playfieldId] = created;

                _logger.Info(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "PlayfieldManager created playfield {0}",
                        playfieldId));
            }

            ZoneEngine_New.Core.Metrics.TickStallWatch.Stage("pf.start", playfieldId);
            created.StartHeartbeat();
            return created;
        }

        /// <summary>
        /// Builds a new, unpublished, unstarted ordinary playfield (or proxy instance) for <paramref name="playfieldId"/>.
        /// Off every tick and outside the manager lock.
        /// </summary>
        private Playfield ConstructPlayfield(int playfieldId)
        {
            // Construct outside the manager lock. Playfield starts a heartbeat thread that may
            // call back into Register/Unregister/FindPlayer; holding _sync here deadlocks login.
            ZoneEngine_New.Core.Metrics.TickStallWatch.Stage("pf.construct", playfieldId);
            IZoneLogger playfieldLogger = _logger.CreateForPlayfield(playfieldId);
            Identity identity = new Identity
            {
                Type = IdentityType.Playfield,
                Instance = playfieldId
            };

            // A proxy instance is built from the interior its source door leads to, under its own identity.
            int resourceId = playfieldId;
            int sourceDoor = 0;
            if (ProxyPlayfieldIds.IsProxy(playfieldId))
            {
                if (!ProxyPlayfieldIds.TryResolveResource(_gameData, playfieldId, out resourceId, out int sourcePlayfield, out sourceDoor))
                    throw new InvalidOperationException("Proxy playfield " + playfieldId.ToString(CultureInfo.InvariantCulture)
                        + " has no walk-in door behind it any more.");
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Proxy playfield {0} is interior {1} behind door 0x{2:X8} on playfield {3}", playfieldId, resourceId, sourceDoor, sourcePlayfield));
            }

            Playfield created;
            if (RequiresWorldSimulation(_gameData, resourceId))
            {
                created = new ACGPlayfield(
                    identity,
                    playfieldLogger,
                    _router,
                    this,
                    _playerHydrator,
                    _gameData,
                    _items,
                    _hashItems,
                    _inventoryRepository,
                    _instanceIds,
                    _inventoryMoves,
                    _inventoryFlush,
                    _trades,
                    _characterSnapshot,
                    _metricsRegistry,
                    _shopDao,
                    resourceId,
                    sourceDoor);
            }
            else
            {
                created = new Playfield(
                    identity,
                    playfieldLogger,
                    _router,
                    this,
                    _playerHydrator,
                    _gameData,
                    _items,
                    _hashItems,
                    _inventoryRepository,
                    _instanceIds,
                    _inventoryMoves,
                    _inventoryFlush,
                    _trades,
                    _characterSnapshot,
                    _metricsRegistry,
                    _shopDao,
                    resourceId,
                    sourceDoor);
            }

            ZoneEngine_New.Core.Metrics.TickStallWatch.Stage("pf.build", playfieldId);
            try
            {
                created.Build();
            }
            catch
            {
                created.Dispose();
                throw;
            }

            return created;
        }

        /// <summary>
        /// The playfield GameData reload (<see cref="GameData.PlayfieldsReload"/>): every playfield that is not running,
        /// being built, being reloaded in place or waited on by a move reads its files from disk again the next time it
        /// is built. Running playfields keep their data until they unload or <see cref="TryStartReload"/> replaces them.
        /// Off every tick.
        /// </summary>
        internal AORebirth.Modules.Content.GameDataReloadResult ReloadIdlePlayfieldData()
        {
            const string name = GameData.PlayfieldsReload.ReloadName;
            if (_gameData is not GameDataStore store)
                return new(name, false, 0, ["this zone's GameData does not cache playfields"]);

            var keep = new HashSet<int>();
            lock (_sync)
            {
                foreach (Playfield playfield in _playfields.Values)
                {
                    keep.Add(playfield.Identity.Instance);
                    keep.Add(playfield.ResourceId);
                }

                foreach (Playfield playfield in _reloading.Values)
                {
                    keep.Add(playfield.Identity.Instance);
                    keep.Add(playfield.ResourceId);
                }

                keep.UnionWith(_building.Keys);
                keep.UnionWith(_pendingMoveByCharacter.Values);
            }

            int released = store.ReleaseIdlePlayfieldCaches(keep);
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Playfield GameData reloaded: {0} cached playfields released, {1} running keep theirs until they unload",
                released, keep.Count));
            return new(name, true, released, []);
        }

        /// <summary>How long a spawn reload waits for each playfield's tick to apply it before reporting it as pending.</summary>
        internal static TimeSpan SpawnReloadTimeout { get; set; } = TimeSpan.FromSeconds(10);

        // One spawn reload at a time, whoever asks (the Playfields GameData reload, the module API, the GM command).
        private readonly Lock _spawnReloadSync = new();

        /// <summary>
        /// The Playfields reload with its per-playfield report (module API 1.20): <see cref="ReloadIdlePlayfieldData"/>, then
        /// <see cref="ReloadRunningPlayfieldSpawns"/>. Off every tick.
        /// </summary>
        internal AORebirth.Modules.Content.PlayfieldReloadResult ReloadPlayfields(IReadOnlyCollection<int>? playfieldIds)
        {
            AORebirth.Modules.Content.GameDataReloadResult idle = ReloadIdlePlayfieldData();
            AORebirth.Modules.Content.PlayfieldReloadResult spawns = ReloadRunningPlayfieldSpawns(playfieldIds);
            return new(idle.Ok && spawns.Ok, idle.Entries, spawns.Playfields, [.. idle.Problems, .. spawns.Problems]);
        }

        /// <summary>
        /// Applies Spawns.json and Districts.json read again from disk (through the override folder) to the running
        /// playfields in <paramref name="playfieldIds"/> (instance or resource id; every running one when null), in place:
        /// <see cref="HashSpawnSystem.ApplyReload"/> on each playfield's own tick. The files are read and compared here, off
        /// every tick; a file that fails to read keeps that playfield's points as they are. Quest dungeons and generated or
        /// static dungeons are left out (listed as not applied when asked for by id). Blocks up to
        /// <see cref="SpawnReloadTimeout"/> per playfield: never call it on a playfield tick.
        /// </summary>
        internal AORebirth.Modules.Content.PlayfieldReloadResult ReloadRunningPlayfieldSpawns(IReadOnlyCollection<int>? playfieldIds)
        {
            if (_gameData is not GameDataStore store)
                return new(false, 0, [], ["this zone's GameData does not reload playfield spawns"]);

            lock (_spawnReloadSync)
            {
                HashSet<int>? wanted = playfieldIds == null ? null : new HashSet<int>(playfieldIds);
                var running = new List<Playfield>();
                lock (_sync)
                {
                    foreach (Playfield playfield in _playfields.Values)
                    {
                        if (!playfield.IsDisposed && !_reloading.ContainsKey(playfield.Identity.Instance)
                            && (wanted == null || wanted.Contains(playfield.Identity.Instance) || wanted.Contains(playfield.ResourceId)))
                            running.Add(playfield);
                    }
                }

                var results = new List<AORebirth.Modules.Content.PlayfieldSpawnReload>();
                bool ok = true;
                if (wanted != null)
                {
                    var found = new HashSet<int>();
                    foreach (Playfield playfield in running)
                    {
                        found.Add(playfield.Identity.Instance);
                        found.Add(playfield.ResourceId);
                    }

                    foreach (int id in wanted.Where(id => !found.Contains(id)).Order())
                        results.Add(HashSpawnSystem.NotReloaded(id,
                            "not running on this zone instance; it reads its files when it next loads"));
                }

                var files = new Dictionary<int, (PlayfieldSpawnsData? Spawns, PlayfieldDistrictsData? Districts, string? Problem)>();
                var pending = new List<(int Id, Task<AORebirth.Modules.Content.PlayfieldSpawnReload> Applied)>();
                foreach (Playfield playfield in running.OrderBy(playfield => playfield.Identity.Instance))
                {
                    int id = playfield.Identity.Instance;
                    HashSpawnSystem? spawns = playfield.GetService<HashSpawnSystem>();
                    if (playfield is QuestDungeonPlayfield || spawns == null || !spawns.IsReloadable)
                    {
                        if (wanted != null)
                            results.Add(HashSpawnSystem.NotReloaded(id, "dungeons place their own spawn points; they are not reloaded"));
                        continue;
                    }

                    if (!files.TryGetValue(playfield.ResourceId, out var read))
                        files[playfield.ResourceId] = read = ReadSpawnFiles(store, playfield.ResourceId);
                    if (read.Problem != null)
                    {
                        ok = false;
                        results.Add(HashSpawnSystem.NotReloaded(id, read.Problem));
                        continue;
                    }

                    HashSpawnSystem.SpawnReloadInput input = spawns.PrepareReload(read.Spawns!, read.Districts);
                    pending.Add((id, DispatchSpawnReload(playfield, spawns, input)));
                }

                foreach ((int id, Task<AORebirth.Modules.Content.PlayfieldSpawnReload> applied) in pending)
                {
                    AORebirth.Modules.Content.PlayfieldSpawnReload result;
                    try
                    {
                        result = applied.Wait(SpawnReloadTimeout)
                            ? applied.Result
                            : HashSpawnSystem.NotReloaded(id, string.Format(CultureInfo.InvariantCulture,
                                "its tick did not apply the reload within {0:0} s; it applies when the tick gets to it",
                                SpawnReloadTimeout.TotalSeconds));
                    }
                    catch (AggregateException exception)
                    {
                        _logger.Error(exception.GetBaseException(), "Spawn reload failed playfield=" + id.ToString(CultureInfo.InvariantCulture));
                        result = HashSpawnSystem.NotReloaded(id, "the reload failed: " + exception.GetBaseException().Message);
                    }

                    ok &= result.Applied;
                    results.Add(result);
                }

                return new(ok, 0, results, []);
            }
        }

        /// <summary>A playfield's Districts.json then Spawns.json, read again; the spawns cache takes the new file.</summary>
        static (PlayfieldSpawnsData? Spawns, PlayfieldDistrictsData? Districts, string? Problem) ReadSpawnFiles(
            GameDataStore store, int resourceId)
        {
            try
            {
                PlayfieldDistrictsData? districts = HashSpawnSystem.ReadDistricts(store.RootPath, resourceId);
                return (store.ReloadPlayfieldSpawns(resourceId), districts, null);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return (null, null, exception.Message + "; it keeps its spawn points");
            }
        }

        static Task<AORebirth.Modules.Content.PlayfieldSpawnReload> DispatchSpawnReload(Playfield playfield,
            HashSpawnSystem spawns, HashSpawnSystem.SpawnReloadInput input)
        {
            var done = new TaskCompletionSource<AORebirth.Modules.Content.PlayfieldSpawnReload>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = playfield.Dispatch(() =>
            {
                try
                {
                    playfield.RequireTick();
                    done.TrySetResult(spawns.ApplyReload(input));
                }
                catch (Exception exception)
                {
                    done.TrySetException(exception);
                }
            });
            if (!queued)
                done.TrySetResult(HashSpawnSystem.NotReloaded(playfield.Identity.Instance, "the playfield is shutting down"));
            return done.Task;
        }

        /// <summary>How long a reload in place waits for everyone to leave the old instance before disposing it.</summary>
        internal static readonly TimeSpan ReloadDrainTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Starts replacing the running ordinary playfield <paramref name="old"/> with a fresh instance built from its
        /// GameData read again from disk. The new instance is built off every tick and published under the same id; every
        /// player on the old one is zone-transferred to where they stand (departure saves them first), and the old one is
        /// disposed once they have left. Anyone still on it then (a link-dead character, a move that never departed) is
        /// logged out and saved by that disposal. <paramref name="completed"/> gets the outcome on a background thread.
        /// Null when the reload started; otherwise why it did not.
        /// </summary>
        internal string? TryStartReload(Playfield old, Action<string> completed)
        {
            ArgumentNullException.ThrowIfNull(old);
            ArgumentNullException.ThrowIfNull(completed);
            int id = old.Identity.Instance;
            if (old is QuestDungeonPlayfield)
                return "Quest dungeons cannot be reloaded.";

            lock (_sync)
            {
                if (_disposed)
                    return "The zone is shutting down.";
                if (old.IsDisposed || !_playfields.TryGetValue(id, out Playfield? current) || !ReferenceEquals(current, old))
                    return "Playfield " + id.ToString(CultureInfo.InvariantCulture) + " is not running here.";
                if (!_reloading.TryAdd(id, old))
                    return "Playfield " + id.ToString(CultureInfo.InvariantCulture) + " is already reloading.";
            }

            _ = Task.Run(async () =>
            {
                string outcome;
                try
                {
                    outcome = await ReloadInPlaceAsync(old).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Playfield reload failed playfield=" + id.ToString(CultureInfo.InvariantCulture));
                    outcome = "Reload of playfield " + id.ToString(CultureInfo.InvariantCulture) + " failed: "
                        + exception.GetBaseException().Message;
                }
                finally
                {
                    lock (_sync)
                        _reloading.Remove(id);
                }

                try
                {
                    completed(outcome);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Playfield reload report failed playfield=" + id.ToString(CultureInfo.InvariantCulture));
                }
            });
            return null;
        }

        async Task<string> ReloadInPlaceAsync(Playfield old)
        {
            int id = old.Identity.Instance;
            if (_gameData is GameDataStore store)
            {
                store.ReleasePlayfieldCache(id);
                if (old.ResourceId != id)
                    store.ReleasePlayfieldCache(old.ResourceId);
            }

            Playfield created = ConstructPlayfield(id);
            bool published = false;
            try
            {
                lock (_sync)
                {
                    if (!_disposed && !old.IsDisposed && _playfields.TryGetValue(id, out Playfield? current)
                        && ReferenceEquals(current, old))
                    {
                        _playfields[id] = created;
                        published = true;
                    }
                }
            }
            finally
            {
                if (!published)
                    created.Dispose();
            }

            if (!published)
                return "Playfield " + id.ToString(CultureInfo.InvariantCulture) + " stopped while it was rebuilding; nothing was reloaded.";

            created.StartHeartbeat();
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Playfield {0} rebuilt from GameData; moving its players", id));

            List<Player> movers;
            lock (_sync)
                movers = _playersByCharacterId.Values.Where(player => ReferenceEquals(player.Playfield, old)).ToList();

            foreach (Player player in movers)
                old.DispatchPlayerProjection(player, () => MoveToReloaded(player, old, created));

            DateTime deadline = DateTime.UtcNow + ReloadDrainTimeout;
            while (DateTime.UtcNow < deadline && IsDraining(old))
                await Task.Delay(250).ConfigureAwait(false);

            int leftBehind;
            lock (_sync)
                leftBehind = _playersByCharacterId.Values.Count(player => ReferenceEquals(player.Playfield, old));

            // Same id stays loaded: no World unload and no metrics removal, the new instance owns both.
            old.Dispose();
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Playfield {0} reloaded in place: players={1} loggedOutByDisposal={2}", id, movers.Count, leftBehind));

            return string.Format(CultureInfo.InvariantCulture,
                "Playfield {0} reloaded: {1} player(s) moved to the new instance{2}.", id, movers.Count - leftBehind,
                leftBehind > 0
                    ? string.Format(CultureInfo.InvariantCulture, ", {0} saved and logged out", leftBehind)
                    : string.Empty);
        }

        /// <summary>On the old instance's tick: one player zones into the reloaded instance where they stand.</summary>
        void MoveToReloaded(Player player, Playfield old, Playfield created)
        {
            if (!ReferenceEquals(player.Playfield, old) || player.Session is not IZoneSession session || session.IsTransferring)
                return;

            try
            {
                var landing = new AORebirth.Core.Vector.Vector3(player.Position.x, player.Position.y, player.Position.z);
                session.TransferToPlayfield(created, landing, player.Rotation);
            }
            catch (Exception exception)
            {
                // The old instance's disposal saves and logs out whoever could not move.
                _logger.Error(exception, "Playfield reload could not move character " + player.Identity.Instance.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>A player with a session is still on the old instance, or a move in or out of it is unfinished.</summary>
        bool IsDraining(Playfield old)
        {
            if (old.HasTransfersInFlight)
                return true;

            lock (_sync)
                return _playersByCharacterId.Values.Any(player => ReferenceEquals(player.Playfield, old) && player.Session != null);
        }

        /// <summary>
        /// Never simulate a playfield World gave to another instance: two copies would each run its NPCs and loot.
        /// Asks World (off any tick: builds never run on one) when this instance does not know yet.
        /// </summary>
        void RequireHostedHere(int playfieldId)
        {
            if (World == null || World.HostsPlayfield(playfieldId))
                return;
            if (!World.IsServing)
                throw new InvalidOperationException("World is unavailable; playfield " + playfieldId.ToString(CultureInfo.InvariantCulture)
                    + " cannot be assigned.");
            AORebirth.World.Contracts.V1.ResolveReply reply = World.ResolveAsync(playfieldId).GetAwaiter().GetResult();
            if (!reply.Accepted)
                throw new InvalidOperationException("World could not assign playfield " + playfieldId.ToString(CultureInfo.InvariantCulture)
                    + ": " + reply.Reason);
            if (!string.Equals(reply.Instance, World.Instance.Name, StringComparison.OrdinalIgnoreCase))
                throw new ZoneEngine_New.Core.World.PlayfieldHostedElsewhereException(playfieldId, reply.Instance);
        }

        /// <summary>
        /// Outdoor playfields carry metadata, while many retail interiors only carry Dynels.dat.
        /// An interior still needs the world simulation when it contains a portal or is the
        /// destination of a return-recording TeleportProxy; otherwise its exit boundary can never
        /// initiate the server-side area change.
        /// </summary>
        /// <summary>
        /// The interior a proxy instance id stands for. False when its source door is gone or no longer leads to an
        /// indoor interior; <paramref name="sourcePlayfieldId"/> is still the door's playfield when the id decodes.
        /// </summary>
        public bool TryResolveProxyResource(int playfieldId, out int resourceId, out int sourcePlayfieldId)
            => ProxyPlayfieldIds.TryResolveResource(_gameData, playfieldId, out resourceId, out sourcePlayfieldId, out _);

        internal static bool RequiresWorldSimulation(IGameData gameData, int playfieldId)
        {
            ArgumentNullException.ThrowIfNull(gameData);
            if (gameData.GetPlayfieldMetaData(playfieldId) != null
                || gameData.GetExitProxyDoorInstances(playfieldId).Count > 0)
            {
                return true;
            }

            var dynels = gameData.GetPlayfieldGeometry(playfieldId).Dynels?.Dynels;
            return dynels != null && dynels.Any(d => PortalDoorLandingResolver.TryReadPortal(d, out _));
        }

        public bool TryGet(int playfieldId, out Playfield? playfield)
        {
            lock (_sync)
            {
                if (_playfields.TryGetValue(playfieldId, out playfield) && !playfield.IsDisposed)
                    return true;

                playfield = null;
                return false;
            }
        }

        /// <summary>Retires empty ordinary worlds off their heartbeat threads; quest dungeon policy is separate.</summary>
        internal void SweepEmptyPlayfields(DateTime nowUtc)
        {
            if (Interlocked.Exchange(ref _sweepingEmptyPlayfields, 1) != 0)
                return;

            try
            {
                List<Playfield> candidates;
                lock (_sync)
                {
                    if (_disposed)
                        return;

                    var pendingDestinations = new HashSet<int>(_pendingMoveByCharacter.Values);
                    candidates = _playfields.Values
                        .Where(playfield => playfield is not AcgDungeonPlayfield
                            && !playfield.IsDisposed
                            && !_building.ContainsKey(playfield.Identity.Instance)
                            && !_reloading.ContainsKey(playfield.Identity.Instance)
                            && !pendingDestinations.Contains(playfield.Identity.Instance))
                        .ToList();
                }

                foreach (Playfield playfield in candidates)
                {
                    // A tick may acquire _sync. Do not take its tick lock while holding _sync.
                    if (!playfield.TryBeginIdleRetirement(nowUtc, EmptyPlayfieldLifetime))
                        continue;

                    try
                    {
                        // Keep the retiring instance published until all its cleanup has finished.
                        // Admissions wait on its completion, preventing overlapping worlds of one ID.
                        playfield.Dispose();
                        lock (_sync)
                        {
                            int id = playfield.Identity.Instance;
                            if (_playfields.TryGetValue(id, out Playfield? current)
                                && ReferenceEquals(current, playfield))
                            {
                                if (_gameData is GameDataStore store)
                                    store.ReleasePlayfieldCache(id);
                                _metricsRegistry.Remove(id);
                                _playfields.Remove(id);
                            }
                        }

                        playfield.CompleteIdleRetirement();
                        World?.PlayfieldUnloaded(playfield.Identity.Instance);
                        _logger.Info(string.Format(
                            CultureInfo.InvariantCulture,
                            "Empty playfield unloaded playfield={0} emptyLifetimeSeconds={1}",
                            playfield.Identity.Instance,
                            (int)EmptyPlayfieldLifetime.TotalSeconds));
                    }
                    catch (Exception exception)
                    {
                        // A failed cleanup must not admit a replacement over partially disposed state.
                        playfield.CompleteIdleRetirement(exception);
                        _logger.Error(exception, "Empty playfield unload failed playfield=" + playfield.Identity.Instance);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Empty playfield sweep failed.");
            }
            finally
            {
                Volatile.Write(ref _sweepingEmptyPlayfields, 0);
            }
        }

        /// <summary>
        /// The live dungeon for <paramref name="questId"/>, built from <paramref name="layout"/> when none exists.
        /// Requesting it restarts its empty timer, so a dungeon cannot be released under a player who is entering.
        /// </summary>
        public QuestDungeonPlayfield GetOrCreateQuestDungeon(int playfieldId, string questId,
            ZoneEngine_New.Core.Quests.Dungeons.DungeonLayout layout, Utility.GameData.Missions.MissionEntrancePlacement entrance,
            string? targetHash = null, int quality = 0,
            ZoneEngine_New.Core.Quests.Dungeons.MissionItemObjectiveBinding? objectiveItem = null,
            Func<Player, MissionObjectiveItem, bool>? collectObjective = null,
            ZoneEngine_New.Core.Quests.Dungeons.MissionNpcObjectiveBinding? npcObjective = null,
            ZoneEngine_New.Core.Quests.Dungeons.DungeonKills? kills = null, bool spawnNpcObjective = true,
            ZoneEngine_New.Core.Quests.Dungeons.MissionChestPopulation? chestPopulation = null)
        {
            ArgumentNullException.ThrowIfNull(questId);
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(entrance);
            if (!ZoneEngine_New.Core.Quests.Dungeons.QuestDungeonIds.IsDungeonPlayfield(playfieldId))
                throw new ArgumentOutOfRangeException(nameof(playfieldId), "Not a quest dungeon id.");

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_playfields.TryGetValue(playfieldId, out var existing))
                {
                    _dungeonEmptySince.Remove(playfieldId);
                    return RequireQuestDungeon(existing, questId);
                }
            }

            RequireHostedHere(playfieldId);
            QuestDungeonPlayfield created = new(playfieldId, questId, layout, entrance, targetHash, quality, _logger.CreateForPlayfield(playfieldId),
                _router, this, _playerHydrator, _gameData, _items, _hashItems, _inventoryRepository, _instanceIds,
                _inventoryMoves, _inventoryFlush, _trades, _characterSnapshot, _metricsRegistry, _shopDao, objectiveItem, collectObjective, npcObjective,
                kills, spawnNpcObjective, chestPopulation);
            try
            {
                ZoneEngine_New.Core.Metrics.TickStallWatch.Stage("pf.build", playfieldId);
                created.Build();
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_playfields.TryGetValue(playfieldId, out var raced))
                    {
                        QuestDungeonPlayfield winner = RequireQuestDungeon(raced, questId);
                        _dungeonEmptySince.Remove(playfieldId);
                        created.Dispose();
                        return winner;
                    }

                    _playfields.Add(playfieldId, created);
                    _dungeonEmptySince.Remove(playfieldId);
                }
            }
            catch
            {
                created.Dispose();
                throw;
            }

            created.StartHeartbeat();
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Quest dungeon created playfield={0} quest={1}", playfieldId, questId));
            return created;
        }

        /// <summary>
        /// The live static dungeon instance <paramref name="playfieldId"/>, built from <paramref name="blueprint"/> when none
        /// exists; <paramref name="chestSeed"/> rolls this instance's chest and turret spots and <paramref name="released"/>
        /// hears of its disposal. Requesting it restarts its empty timer, as for a quest dungeon.
        /// </summary>
        public StaticDungeonPlayfield GetOrCreateStaticDungeon(int playfieldId,
            ZoneEngine_New.Core.Quests.Dungeons.Static.StaticDungeonBlueprint blueprint, int chestSeed,
            Action<StaticDungeonPlayfield>? released = null)
        {
            ArgumentNullException.ThrowIfNull(blueprint);
            if (!ZoneEngine_New.Core.Quests.Dungeons.Static.StaticDungeonIds.IsStaticDungeonPlayfield(playfieldId))
                throw new ArgumentOutOfRangeException(nameof(playfieldId), "Not a static dungeon instance id.");

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_playfields.TryGetValue(playfieldId, out var existing))
                {
                    _dungeonEmptySince.Remove(playfieldId);
                    return RequireStaticDungeon(existing, blueprint);
                }
            }

            RequireHostedHere(playfieldId);
            StaticDungeonPlayfield created = new(playfieldId, blueprint, chestSeed, _logger.CreateForPlayfield(playfieldId),
                _router, this, _playerHydrator, _gameData, _items, _hashItems, _inventoryRepository, _instanceIds,
                _inventoryMoves, _inventoryFlush, _trades, _characterSnapshot, _metricsRegistry, _shopDao)
            {
                Released = released
            };
            try
            {
                ZoneEngine_New.Core.Metrics.TickStallWatch.Stage("pf.build", playfieldId);
                created.Build();
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_playfields.TryGetValue(playfieldId, out var raced))
                    {
                        StaticDungeonPlayfield winner = RequireStaticDungeon(raced, blueprint);
                        _dungeonEmptySince.Remove(playfieldId);
                        // The loser never became the instance: its records must not be dropped when it goes.
                        created.Released = null;
                        created.Dispose();
                        return winner;
                    }

                    _playfields.Add(playfieldId, created);
                    _dungeonEmptySince.Remove(playfieldId);
                }
            }
            catch
            {
                created.Released = null;
                created.Dispose();
                throw;
            }

            created.StartHeartbeat();
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Static dungeon created playfield={0} entrance={1}", playfieldId,
                blueprint.Entrance.IdentityInstance));
            return created;
        }

        /// <summary>The live static instance <paramref name="playfieldId"/>, kept from the empty-dungeon sweep; false when it is not live here.</summary>
        public bool TryReuseStaticDungeon(int playfieldId, out StaticDungeonPlayfield dungeon)
        {
            lock (_sync)
            {
                dungeon = null!;
                if (_disposed || !_playfields.TryGetValue(playfieldId, out var existing) || existing is not StaticDungeonPlayfield live)
                    return false;
                _dungeonEmptySince.Remove(playfieldId);
                dungeon = live;
                return true;
            }
        }

        public bool TryGetStaticDungeon(int playfieldId, out StaticDungeonPlayfield dungeon)
        {
            lock (_sync)
            {
                dungeon = (_playfields.TryGetValue(playfieldId, out var existing) ? existing as StaticDungeonPlayfield : null)!;
                return dungeon != null;
            }
        }

        /// <summary>The static dungeon instances running here.</summary>
        public IReadOnlyList<StaticDungeonPlayfield> SnapshotStaticDungeons()
        {
            lock (_sync)
            {
                return _playfields.Values.OfType<StaticDungeonPlayfield>().ToList();
            }
        }

        /// <summary>Releases a static instance now if it is empty. False while occupied or when it is not live here.</summary>
        public bool TryReleaseStaticDungeon(int playfieldId)
        {
            StaticDungeonPlayfield released;
            lock (_sync)
            {
                if (_disposed || !_playfields.TryGetValue(playfieldId, out var existing) || existing is not StaticDungeonPlayfield dungeon
                    || IsOccupied(dungeon))
                    return false;

                _playfields.Remove(playfieldId);
                _dungeonEmptySince.Remove(playfieldId);
                released = dungeon;
            }

            DisposeReleased([released]);
            return true;
        }

        static StaticDungeonPlayfield RequireStaticDungeon(Playfield candidate, ZoneEngine_New.Core.Quests.Dungeons.Static.StaticDungeonBlueprint blueprint)
        {
            if (candidate is not StaticDungeonPlayfield dungeon || !dungeon.Blueprint.Entrance.Identity.Equals(blueprint.Entrance.Identity))
                throw new InvalidOperationException("Static dungeon id is held by a different playfield.");
            return dungeon;
        }

        /// <summary>
        /// The live dungeon for <paramref name="questId"/>, kept from the empty-dungeon sweep the way
        /// <see cref="GetOrCreateQuestDungeon"/> keeps an existing one, without building anything. False when it is not live here.
        /// </summary>
        public bool TryReuseQuestDungeon(int playfieldId, string questId, out QuestDungeonPlayfield dungeon)
        {
            ArgumentNullException.ThrowIfNull(questId);
            lock (_sync)
            {
                dungeon = null!;
                if (_disposed || !_playfields.TryGetValue(playfieldId, out var existing))
                    return false;
                _dungeonEmptySince.Remove(playfieldId);
                dungeon = RequireQuestDungeon(existing, questId);
                return true;
            }
        }

        public bool TryGetQuestDungeon(int playfieldId, out QuestDungeonPlayfield dungeon)
        {
            lock (_sync)
            {
                dungeon = (_playfields.TryGetValue(playfieldId, out var existing) ? existing as QuestDungeonPlayfield : null)!;
                return dungeon != null;
            }
        }

        /// <summary>
        /// Releases dungeons (quest dungeons and static instances) that have had nobody in them for
        /// <paramref name="emptyLifetime"/>. A player still linked to the dungeon (in it, transferring, or link-dead)
        /// keeps it alive. Run off every playfield tick.
        /// </summary>
        public void SweepQuestDungeons(DateTime nowUtc, TimeSpan emptyLifetime)
        {
            var released = new List<AcgDungeonPlayfield>();
            lock (_sync)
            {
                if (_disposed) return;
                // One pass over players, not one per dungeon.
                var withPlayers = new HashSet<Playfield>(ReferenceEqualityComparer.Instance);
                foreach (Player player in _playersByCharacterId.Values)
                    if (player.Playfield is Playfield current) withPlayers.Add(current);

                foreach (Playfield playfield in _playfields.Values)
                {
                    if (playfield is not AcgDungeonPlayfield dungeon) continue;
                    int id = dungeon.Identity.Instance;
                    if (withPlayers.Contains(dungeon) || dungeon.GetRequiredService<DynelRegistry>().HasPlayers())
                    {
                        _dungeonEmptySince.Remove(id);
                        continue;
                    }

                    if (!_dungeonEmptySince.TryGetValue(id, out long since))
                    {
                        _dungeonEmptySince[id] = nowUtc.Ticks;
                        continue;
                    }

                    if (nowUtc.Ticks - since >= emptyLifetime.Ticks)
                        released.Add(dungeon);
                }

                foreach (AcgDungeonPlayfield dungeon in released)
                {
                    _playfields.Remove(dungeon.Identity.Instance);
                    _dungeonEmptySince.Remove(dungeon.Identity.Instance);
                }
            }

            DisposeReleased(released);
        }

        /// <summary>Releases a quest's dungeon now if it is empty (its quest ended). False while occupied.</summary>
        public bool TryReleaseQuestDungeon(int playfieldId, string questId)
        {
            QuestDungeonPlayfield? released;
            lock (_sync)
            {
                if (_disposed || !_playfields.TryGetValue(playfieldId, out var existing)) return true;
                if (existing is not QuestDungeonPlayfield dungeon || !string.Equals(dungeon.QuestId, questId, StringComparison.Ordinal)
                    || IsOccupied(dungeon))
                    return false;

                _playfields.Remove(playfieldId);
                _dungeonEmptySince.Remove(playfieldId);
                released = dungeon;
            }

            DisposeReleased([released]);
            return true;
        }

        // Caller holds _sync.
        bool IsOccupied(Playfield playfield)
            => playfield.GetRequiredService<DynelRegistry>().HasPlayers()
                || _playersByCharacterId.Values.Any(player => ReferenceEquals(player.Playfield, playfield));

        void DisposeReleased(List<AcgDungeonPlayfield> released)
        {
            // Never dispose (join a heartbeat) while holding the manager lock.
            foreach (AcgDungeonPlayfield dungeon in released)
            {
                try
                {
                    dungeon.Dispose();
                    World?.PlayfieldUnloaded(dungeon.Identity.Instance);
                    _logger.Info(string.Format(CultureInfo.InvariantCulture, "Dungeon released playfield={0} {1}",
                        dungeon.Identity.Instance, dungeon is QuestDungeonPlayfield quest ? "quest=" + quest.QuestId
                            : dungeon is StaticDungeonPlayfield fixedDungeon ? "static entrance=" + fixedDungeon.Blueprint.Entrance.IdentityInstance.ToString(CultureInfo.InvariantCulture)
                            : string.Empty));
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Quest dungeon disposal failed playfield=" + dungeon.Identity.Instance);
                }
            }
        }

        static QuestDungeonPlayfield RequireQuestDungeon(Playfield candidate, string questId)
        {
            if (candidate is not QuestDungeonPlayfield dungeon || !string.Equals(dungeon.QuestId, questId, StringComparison.Ordinal))
                throw new InvalidOperationException("Quest dungeon id is held by a different playfield.");
            return dungeon;
        }

        public bool FindPlayer(int characterId, out Player player)
        {
            lock (_sync)
            {
                return _playersByCharacterId.TryGetValue(characterId, out player!);
            }
        }

        public IReadOnlyList<Player> SnapshotPlayers()
        {
            lock (_sync)
            {
                return _playersByCharacterId.Values.ToList();
            }
        }

        public IReadOnlyList<Playfield> SnapshotPlayfields()
        {
            lock (_sync)
            {
                return _playfields.Values.Where(playfield => !playfield.IsDisposed).ToList();
            }
        }

        public void RegisterPlayer(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            int characterId = player.Identity.Instance;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_playersByCharacterId.TryGetValue(characterId, out Player? existing)
                    && !ReferenceEquals(existing, player))
                    throw new InvalidOperationException("A character already has an authoritative player instance.");
                _playersByCharacterId[characterId] = player;
            }

            ZoneEngine_New.Core.Metrics.WatchdogFeed.Sync(player);
            PlayerRegistered?.Invoke(player);
        }

        /// <summary>
        /// A player became this instance's authoritative copy of its character (login, reconnect or arrival from another
        /// instance). Raised on the registering thread; handlers must not block and dispatch onto the player's tick.
        /// </summary>
        public event Action<Player>? PlayerRegistered;

        public void UnregisterPlayer(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            int characterId = player.Identity.Instance;
            lock (_sync)
            {
                if (_playersByCharacterId.TryGetValue(characterId, out Player? existing)
                    && ReferenceEquals(existing, player))
                {
                    _playersByCharacterId.Remove(characterId);
                }
            }

            // A move queued behind a background build is dropped with its player; free the slot.
            _pendingMoveByCharacter.TryRemove(characterId, out _);

            ZoneEngine_New.Core.Metrics.WatchdogFeed.Remove(player);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            // Wait outside _sync for an in-progress retirement before disposing the remaining worlds.
            _emptyPlayfieldSweep.DisposeAsync().AsTask().GetAwaiter().GetResult();
            List<Playfield> playfields;
            lock (_sync)
            {
                playfields = new List<Playfield>(_playfields.Values);
                // An old instance mid-reload is no longer published but still holds players to save.
                foreach (Playfield old in _reloading.Values)
                {
                    if (!playfields.Contains(old))
                        playfields.Add(old);
                }

                _playfields.Clear();
                _playersByCharacterId.Clear();
            }

            foreach (Playfield playfield in playfields)
                playfield.Dispose();

            _metricsRegistry.Clear();
        }
    }
}
