namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.Enums;
    using AORebirth.Modules.Events;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using MySqlConnector;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Organizations;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Trade;
    using ZoneEngine_New.Core.World;

    using PlayfieldInstance = ZoneEngine_New.Core.Playfield.Playfield;

    /// <summary>
    /// Org cities: the city controllers players buy districts from, the houses on them, upkeep and advantages.
    /// <para>
    /// The database (<see cref="ICityStore"/>) is the only authority. The tick checks a request against this instance's
    /// cache of the districts and answers what it can; what changes a district runs off the tick, in that district's own
    /// queue, as one transaction that checks everything again under the district's row lock. Each committed change is
    /// read back and published to the cache by version; every instance also reloads districts whose version moved
    /// (another instance's change, a due sweep), and tells the clients on the district's playfield what changed.
    /// </para>
    /// <para>
    /// Credits and items move so that a crash can lose them, never copy them, and nothing is taken for a request the
    /// database refuses: a purchase is paid from the organization's bank in the same transaction that gives it the
    /// district; a house reserves its tiles first (pending), then its house item is retired in the builder's lane, then the
    /// house is confirmed behind that retire. A pending house nobody confirms (a crash between) is given up by the due
    /// sweep, never confirmed.
    /// </para>
    /// </summary>
    public sealed partial class CityService : IDisposable
    {
        /// <summary>CityID_t's controller identity type (AOCityInterfaceClient_c vtable +0x94, 1012e5b0).</summary>
        public const int CityIdentityType = 0xC419;

        /// <summary>The client takes an item whose Side (stat 33) is this as any side's (GUI 100e56a8).</summary>
        public const int AnySide = 1234567890;

        /// <summary>RDB record type of house templates; the client names a template as (this, id).</summary>
        public const int HouseTemplateRecordType = 1000040;

        /// <summary>Signal 5's GUI types (GUI.dll: 1 the buy window, 2 the terminal).</summary>
        public const int BuyWindow = 1, TerminalWindow = 2;

        /// <summary>
        /// Signal 5/6 lease modes as this server uses them: 3 the full window (purchase enabled in the buy window; build,
        /// demolish, cloak and advantages in the terminal), 2 the buy window with purchase greyed out, 1 the read-only
        /// terminal (GUI 100e6208).
        /// </summary>
        public const int LeaseReadOnly = 1, LeaseLimited = 2, LeaseFull = 3;

        /// <summary>Signal 5's third int: build controls are disabled at lease mode 3 when it is below 2.</summary>
        public const int BuildAllowed = 2, BuildDenied = 1;

        /// <summary>
        /// Lost Eden's AOTransportSignal 1000 (AreaPrices, team request 29): CityId, then the district's purchase cost and
        /// its area upkeep, two ints. The Lost Eden client shows these on the Buy Land Area tab instead of its RDB 1000039
        /// numbers, and keeps Purchase disabled until they arrive. Stock signals run 2..16.
        /// </summary>
        public const int AreaPricesSignal = 1000;

        const string NotLeaderText = "Only your organization's leader can buy a city district.";

        const int MaxRequestsInFlight = 8;
        static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);
        static readonly TimeSpan DueInterval = TimeSpan.FromSeconds(5);

        /// <summary>How long a step that must run on the player's tick (taking a house item) may wait before it is given up.</summary>
        static readonly TimeSpan TickStepTimeout = TimeSpan.FromMinutes(2);

        readonly ICityStore _store;
        readonly CityCatalog _catalog;
        readonly IGameData _gameData;
        readonly IItemBuilder _items;
        readonly IItemTemplateCatalog _templates;
        readonly InventoryActionService _inventory;
        readonly InventoryFlushService _flush;
        readonly TradeService _trades;
        readonly OrganizationService _organizations;
        readonly Lazy<PlayfieldManager> _playfields;
        readonly WorldLink? _world;
        readonly IZoneLogger _logger;

        readonly ConcurrentDictionary<(int, int), CityPlotState> _plots = new();
        readonly Dictionary<(int, int), Task> _queues = new();
        readonly object _queueLock = new();
        readonly object _publishLock = new();
        readonly ConcurrentDictionary<int, CityWindow> _windows = new();
        // Per player object, so a count a lost step never brought back ends with that login.
        readonly ConditionalWeakTable<Player, StrongBox<int>> _inFlight = new();
        readonly ConcurrentDictionary<TickStep, byte> _tickSteps = new();
        Timer? _syncTimer;
        Timer? _dueTimer;
        int _syncing;
        int _sweeping;
        int _started;
        volatile bool _disposed;

        public CityService(ICityStore store, CityCatalog catalog, IGameData gameData, IItemBuilder items, IItemTemplateCatalog templates,
            InventoryActionService inventory, InventoryFlushService flush, TradeService trades,
            OrganizationService organizations, Lazy<PlayfieldManager> playfields, IZoneLogger logger, WorldLink? world = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _templates = templates ?? throw new ArgumentNullException(nameof(templates));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
            _trades = trades ?? throw new ArgumentNullException(nameof(trades));
            _organizations = organizations ?? throw new ArgumentNullException(nameof(organizations));
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _world = world;
        }

        public CityCatalog Catalog => _catalog;

        CityRules Rules => _catalog.Rules;

        public bool Enabled => _catalog.Enabled && Volatile.Read(ref _started) != 0;

        /// <summary>A player's open city window: which district, and whether it is the one with full access.</summary>
        sealed record CityWindow(Player Player, int PlayfieldId, int ControllerIndex, bool Full);

        // ------------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Loads every district and starts the sync and due timers. Call once, before any client connects.</summary>
        public void Start() => Start(timers: true);

        /// <summary><see cref="Start()"/>; tests leave the timers off and run <see cref="SyncNow"/> and <see cref="SweepDueNow"/> themselves.</summary>
        internal void Start(bool timers)
        {
            if (!_catalog.Enabled || Volatile.Read(ref _started) != 0)
                return;
            IReadOnlyList<CityPlotState> stored;
            try
            {
                stored = _store.LoadAll();
            }
            catch (MySqlException exception)
            {
                // Cities stay off; the rest of the zone runs. The usual cause is the migration not applied yet.
                _logger.Error(exception, "Cities disabled: the city tables could not be read (is migration 20261124_city_plots.sql applied?)");
                return;
            }

            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            foreach (CityPlotState state in stored)
                _plots[state.Key] = state;
            CityInsides.Describe = DescribeInside;
            _organizations.MembershipReconciled += OnMembershipReconciled;
            _catalog.Reloaded += OnCatalogReloaded;
            if (timers)
            {
                _syncTimer = new Timer(_ => Sync(), null, SyncInterval, SyncInterval);
                _dueTimer = new Timer(_ => SweepDue(), null, DueInterval, DueInterval);
            }

            _logger.Info("Cities started districts=" + _plots.Count(p => p.Value.IsOwned) + " owned of " + _plots.Count + " stored");
        }

        public void Dispose()
        {
            _disposed = true;
            _syncTimer?.Dispose();
            _dueTimer?.Dispose();
            if (_started != 0)
            {
                CityInsides.Describe = null;
                _organizations.MembershipReconciled -= OnMembershipReconciled;
                _catalog.Reloaded -= OnCatalogReloaded;
            }
        }

        // ------------------------------------------------------------------------------------------------ lookups

        CityPlotState Plot(int playfieldId, int index)
            => _plots.TryGetValue((playfieldId, index), out CityPlotState? state) ? state : new CityPlotState(playfieldId, index);

        /// <summary>The district an organization owns (paid for or pending), by this instance's cache.</summary>
        CityPlotState? PlotOf(int organizationId)
        {
            if (organizationId <= 0)
                return null;
            foreach (CityPlotState state in _plots.Values)
                if (state.OrganizationId == organizationId)
                    return state;
            return null;
        }

        /// <summary>The city controller a CityController dynel of a playfield is, when the playfield has cities.</summary>
        public bool TryGetController(PlayfieldInstance playfield, int dynelInstance, out CityControllerDefinition controller)
        {
            controller = null!;
            return Enabled && !playfield.IsProxyInstance && _catalog.TryGetPlayfield(playfield.Identity.Instance, out CityPlayfieldDefinition city)
                && city.TryGetByDynelInstance(dynelInstance, out controller);
        }

        bool TryGetByCityInstance(PlayfieldInstance playfield, int cityInstance, out CityControllerDefinition controller)
        {
            controller = null!;
            return Enabled && !playfield.IsProxyInstance && _catalog.TryGetPlayfield(playfield.Identity.Instance, out CityPlayfieldDefinition city)
                && city.TryGetByCityInstance(cityInstance, out controller);
        }

        /// <summary>
        /// The CityID_t the client built for the controller (Gamecode LoadPlayfieldCityData 101224a0): (0xC419, controller
        /// instance), the playfield proxy's +0xc and the playfield's model id (n3Playfield_t +8, the proxy's identity). The
        /// proxy is PlayfieldAnarchyF's PlayfieldId1 (+0), Unknown3 (+8) and SG (+0xc) (city.dll 10027385 lays it out), so
        /// the int is SG and the model id PlayfieldId1. The client compares all three (city.dll 1000a59f) and ignores a
        /// window signal with any other.
        /// </summary>
        public static CityId CityIdOf(PlayfieldInstance playfield, CityControllerDefinition controller)
        {
            PlayfieldAnarchyFMessage anarchy = playfield.CreatePlayfieldAnarchyFMessage(default);
            return new CityId(new Identity((IdentityType)CityIdentityType, controller.CityInstance), anarchy.SG, anarchy.PlayfieldId1);
        }

        bool CanManage(Player player, int organizationId)
            => _organizations.TryGetStanding(organizationId, player.Identity.Instance, out GoverningForm form, out int rank, out bool leader)
                && Rules.CanManage(form, rank, leader);

        /// <summary>Whether the player leads the organization (its highest rank): only the leader buys a district for it.</summary>
        bool IsLeader(Player player, int organizationId)
            => organizationId > 0 && _organizations.TryGetStanding(organizationId, player.Identity.Instance, out _, out _, out bool leader) && leader;

        /// <summary>Whether the organization's cached bank holds the price: a purchase is paid from it (the transaction checks it again).</summary>
        bool BankCanPay(int organizationId, long price)
            => organizationId > 0 && _organizations.TryGetBank(organizationId, out long bank) && bank >= price;

        /// <summary>The same rule from a member's stored row, for the transactions.</summary>
        CityManagePermission ManagePermission()
        {
            OrganizationRules organizations = _gameData.Organizations;
            CityRules rules = Rules;
            return (form, storedRank, isLeader) =>
                organizations.Enabled
                && rules.CanManage(organizations.FormOf(form), organizations.EffectiveRank(form, storedRank, isLeader), isLeader);
        }

        // ------------------------------------------------------------------------------------------------ the window

        /// <summary>
        /// A player used a city controller (the dynel's own reach and requirement checks passed). On the tick. Opens the
        /// buy window or the terminal for its district, as this player may use it.
        /// </summary>
        public bool OpenWindow(Player player, Dynel controllerDynel)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(controllerDynel);
            if (player.Session == null || player.Playfield is not PlayfieldInstance playfield
                || !TryGetController(playfield, controllerDynel.Identity.Instance, out CityControllerDefinition controller))
                return false;

            CityPlotState state = Plot(controller.PlayfieldId, controller.Index);
            int organizationId = player.Organization?.Id ?? 0;
            int guiType, lease, build = 0;
            string owner = state.IsOwned ? state.OrganizationName : string.Empty;
            bool full = false;
            MessageBody? notice = null;
            if (!state.IsOwned)
            {
                guiType = BuyWindow;
                bool canBuy = IsLeader(player, organizationId) && PlotOf(organizationId) == null
                    && !state.IsLockedOut(organizationId, DateTime.UtcNow)
                    && BankCanPay(organizationId, controller.PurchaseCost);
                lease = canBuy ? LeaseFull : LeaseLimited;
                // Every leader who may buy holds a buy window; the purchase itself settles who gets the district.
                full = canBuy;
                if (organizationId > 0 && PlotOf(organizationId) != null)
                    notice = Feedback(player, CityText.OrgCategory, CityText.OrganizationOwnsCity);
                else if (organizationId > 0 && !IsLeader(player, organizationId))
                    notice = Text(player, NotLeaderText);
            }
            else if (state.OrganizationId == organizationId)
            {
                guiType = TerminalWindow;
                // One member at a time has the full terminal (retail text 110/240791923).
                if (FullWindowHolder(controller, player) is { } holder)
                {
                    lease = LeaseReadOnly;
                    notice = TextFeedback(player, CityText.Category, CityText.TerminalInUse, holder.Name ?? string.Empty);
                }
                else
                {
                    lease = LeaseFull;
                    full = true;
                    build = CanManage(player, organizationId) ? BuildAllowed : BuildDenied;
                    notice = Feedback(player, CityText.Category, CityText.FullAccess);
                }
            }
            else
            {
                guiType = TerminalWindow;
                lease = LeaseReadOnly;
                notice = Feedback(player, CityText.TerminalCategory, CityText.NoAccess);
            }

            CityId id = CityIdOf(playfield, controller);
            // The prices first, so the Buy Land Area tab has them when the window opens; a purchase is charged these.
            player.Session.Send(AreaPrices(player, id, controller));
            player.Session.Send(new AOTransportSignalMessage
            {
                Identity = player.Identity, Signal = CitySignal.OpenGuiSession, City = id, Identity2 = player.Identity,
                Int1 = guiType, Int2 = lease, Int3 = build, Text = owner
            });
            SendWindowState(player, controller, state);
            if (notice != null)
                player.Session.Send(notice);
            _windows[player.Identity.Instance] = new CityWindow(player, controller.PlayfieldId, controller.Index, full);
            return true;
        }

        /// <summary>The member who has this district's full terminal open, other than <paramref name="except"/>.</summary>
        Player? FullWindowHolder(CityControllerDefinition controller, Player except)
        {
            foreach (CityWindow window in _windows.Values)
            {
                if (!window.Full || window.PlayfieldId != controller.PlayfieldId || window.ControllerIndex != controller.Index
                    || window.Player.Identity.Instance == except.Identity.Instance)
                    continue;
                if (IsWindowCurrent(window, controller))
                    return window.Player;
            }

            return null;
        }

        /// <summary>Whether a recorded window is still open: the same player object, online, on the playfield, near the controller.</summary>
        bool IsWindowCurrent(CityWindow window, CityControllerDefinition controller)
        {
            Player player = window.Player;
            return player.Session != null && player.Playfield is PlayfieldInstance playfield && !playfield.IsDisposed
                && playfield.Identity.Instance == controller.PlayfieldId
                && _playfields.Value.FindPlayer(player.Identity.Instance, out Player current) && ReferenceEquals(current, player)
                && ControllerDynel(playfield, controller) is { } dynel && player.GetEdgeDistanceTo(dynel) <= Rules.InteractionRange;
        }

        static Dynel? ControllerDynel(PlayfieldInstance playfield, CityControllerDefinition controller)
            => playfield.GetRequiredService<DynelRegistry>().TryGet(
                new Identity(IdentityType.CityController, controller.DynelInstance), out Dynel? dynel) ? dynel : null;

        /// <summary>Signals 16, 10, 13, 14 and 15: owner, upkeep, next bill, cloak and charge.</summary>
        void SendWindowState(Player player, CityControllerDefinition controller, CityPlotState state)
        {
            if (player.Session is not { } session)
                return;
            DateTime now = DateTime.UtcNow;
            long upkeep = state.IsOwned ? state.Upkeep(_catalog, controller) : controller.UpkeepCost;
            int seconds = state.IsActive && state.NextBillAtUtc is { } bill ? ClampSeconds(bill - now) : 0;
            (int cloak, int timer) = CloakSignal(state, now);
            session.Send(Signal(player, CitySignal.CityOrgName, text: state.IsOwned ? state.OrganizationName : string.Empty));
            session.Send(Signal(player, CitySignal.CityUpkeepCost, (int)Math.Min(int.MaxValue, upkeep)));
            session.Send(Signal(player, CitySignal.UpkeepSecondsTillBill, seconds));
            session.Send(new AOTransportSignalMessage
            {
                Identity = player.Identity, Signal = CitySignal.CloakingDeviceState, Int1 = cloak, Int2 = timer
            });
            session.Send(new AOTransportSignalMessage
            {
                Identity = player.Identity, Signal = CitySignal.ControllerCharge,
                Float = state.IsOwned ? state.ControllerCharge : Rules.StartingControllerCharge
            });
        }

        /// <summary>
        /// Signal 14: the state is 0 without a cloaking device, 1 cloaked, -1 not (the button shows pressed above 0; the
        /// toggle is offered while it is not 0 and the timer is below 0: seconds until it may change again, minus once it may).
        /// </summary>
        (int State, int Timer) CloakSignal(CityPlotState state, DateTime now)
        {
            if (!state.IsActive || !state.HasStandingType(_catalog, CityPlacement.IsCloakingDevice))
                return (0, -1);
            int timer = state.CloakToggledAtUtc is { } toggled ? (int)Math.Ceiling((toggled + Rules.CloakToggleCooldown - now).TotalSeconds) : -1;
            if (timer >= 0)
                timer = Math.Max(1, timer);
            return (state.CloakState != 0 ? 1 : -1, timer < 0 ? -1 : timer);
        }

        static int ClampSeconds(TimeSpan span) => (int)Math.Clamp(Math.Ceiling(span.TotalSeconds), 0, int.MaxValue);

        /// <summary>
        /// Signal 1000 (<see cref="AreaPricesSignal"/>): the district's purchase cost and area upkeep, as the server charges
        /// them. LostEden.Protocol 0.5.0 has no reader for the id, so its arguments go as the signal's raw payload, written
        /// the way the typed signals write theirs.
        /// </summary>
        internal static AOTransportSignalMessage AreaPrices(Player player, CityId id, CityControllerDefinition controller)
        {
            var writer = new LostEden.Protocol.PacketWriter(32);
            id.Write(writer);
            writer.WriteInt32((int)Math.Clamp(controller.PurchaseCost, 0, int.MaxValue));
            writer.WriteInt32((int)Math.Clamp(controller.UpkeepCost, 0, int.MaxValue));
            return new AOTransportSignalMessage { Identity = player.Identity, Signal = (CitySignal)AreaPricesSignal, Payload = writer.ToArray() };
        }

        /// <summary>
        /// The city data was reloaded (a price may have changed): every open window gets its district's prices again, on
        /// its player's tick.
        /// </summary>
        void OnCatalogReloaded()
        {
            foreach (CityWindow window in _windows.Values)
            {
                Player player = window.Player;
                Dispatch(player, () =>
                {
                    if (!_windows.TryGetValue(player.Identity.Instance, out CityWindow? current) || !ReferenceEquals(current, window)
                        || player.Playfield is not PlayfieldInstance playfield
                        || !_catalog.TryGetController(window.PlayfieldId, window.ControllerIndex, out CityControllerDefinition controller)
                        || !IsWindowCurrent(window, controller))
                        return;
                    player.Session?.Send(AreaPrices(player, CityIdOf(playfield, controller), controller));
                });
            }
        }

        static AOTransportSignalMessage Signal(Player player, CitySignal signal, int value = 0, string? text = null)
            => new() { Identity = player.Identity, Signal = signal, Int1 = value, Text = text ?? string.Empty };

        /// <summary>The window's close or "Cancel": the client closes only on signal 7 (GUI 100e7203).</summary>
        public void CloseWindow(Player player, int cityInstance)
        {
            if (player.Session == null || player.Playfield is not PlayfieldInstance playfield
                || !TryGetByCityInstance(playfield, cityInstance, out CityControllerDefinition controller))
                return;
            if (_windows.TryGetValue(player.Identity.Instance, out CityWindow? window) && window.PlayfieldId == controller.PlayfieldId
                && window.ControllerIndex == controller.Index)
                _windows.TryRemove(new KeyValuePair<int, CityWindow>(player.Identity.Instance, window));
            player.Session.Send(new AOTransportSignalMessage
            {
                Identity = player.Identity, Signal = CitySignal.CloseGuiSession, City = CityIdOf(playfield, controller)
            });
        }

        /// <summary>
        /// Every request from the window: the player in play, its open window this district's with full access, and the
        /// player still near the controller. Anything else is a crafted request and is dropped.
        /// </summary>
        bool TryRequest(Player player, int cityInstance, out PlayfieldInstance playfield, out CityControllerDefinition controller,
            out CityPlotState state)
        {
            state = null!;
            controller = null!;
            playfield = null!;
            if (player.Session == null || player.IsPersistenceQuarantined || player.IsDead
                || player.Playfield is not PlayfieldInstance current)
                return false;
            playfield = current;
            if (!TryGetByCityInstance(playfield, cityInstance, out controller)
                || !_windows.TryGetValue(player.Identity.Instance, out CityWindow? window) || !window.Full
                || window.PlayfieldId != controller.PlayfieldId || window.ControllerIndex != controller.Index
                || !IsWindowCurrent(window, controller))
                return false;
            state = Plot(controller.PlayfieldId, controller.Index);
            return true;
        }

        bool BeginWork(Player player)
        {
            StrongBox<int> count = _inFlight.GetValue(player, _ => new StrongBox<int>());
            while (true)
            {
                int current = Volatile.Read(ref count.Value);
                if (current >= MaxRequestsInFlight)
                    return false;
                if (Interlocked.CompareExchange(ref count.Value, current + 1, current) == current)
                    return true;
            }
        }

        void EndWork(Player player)
        {
            if (!_inFlight.TryGetValue(player, out StrongBox<int>? count))
                return;
            while (true)
            {
                int current = Volatile.Read(ref count.Value);
                if (current <= 0 || Interlocked.CompareExchange(ref count.Value, current - 1, current) == current)
                    return;
            }
        }

        /// <summary>The player's requests this instance is still working on, for tests.</summary>
        internal int RequestsInFlight(Player player)
            => _inFlight.TryGetValue(player, out StrongBox<int>? count) ? Volatile.Read(ref count.Value) : 0;

        /// <summary>Runs <paramref name="work"/> after every earlier piece of work of the same district, off the tick.</summary>
        Task Queue((int, int) key, Action work)
        {
            lock (_queueLock)
            {
                Task previous = _queues.TryGetValue(key, out Task? tail) ? tail : Task.CompletedTask;
                Task next = previous.ContinueWith(_ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(exception, "City work failed district=" + key.Item1 + "/" + key.Item2);
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _queues[key] = next;
                // Forget the tail once it is done and nothing has queued behind it.
                next.ContinueWith(done =>
                {
                    lock (_queueLock)
                        if (_queues.TryGetValue(key, out Task? last) && ReferenceEquals(last, done))
                            _queues.Remove(key);
                }, TaskScheduler.Default);
                return next;
            }
        }

        /// <summary>Every district's queued work so far, for tests.</summary>
        internal Task WhenIdle()
        {
            lock (_queueLock)
                return Task.WhenAll(_queues.Values.ToArray());
        }

        internal void SyncNow() => Sync();

        internal void SweepDueNow() => SweepDue();

        /// <summary>This instance's cached district, for tests and diagnostics.</summary>
        internal CityPlotState CachedPlot(int playfieldId, int index) => Plot(playfieldId, index);

        /// <summary>Runs <paramref name="action"/> on the player's tick while it is still this instance's player in play.</summary>
        bool Dispatch(Player player, Action action, Action? skipped = null)
        {
            if (player.Playfield is not PlayfieldInstance playfield || playfield.IsDisposed)
                return false;
            playfield.DispatchPlayerProjection(player, () =>
            {
                if (!IsInPlay(player))
                {
                    skipped?.Invoke();
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "City update failed character=" + player.Identity.Instance);
                }
            });
            return true;
        }

        bool IsInPlay(Player player)
            => player.Session != null && player.Playfield is PlayfieldInstance playfield && !playfield.IsDisposed
                && _playfields.Value.FindPlayer(player.Identity.Instance, out Player current) && ReferenceEquals(current, player);

        /// <summary>
        /// A step of a request that must run on the player's tick, and what undoes the request if it never does. Exactly one
        /// of the two runs: the step on the tick, or <see cref="OrElse"/> when the player left before it ran, the step was
        /// dropped on the way, or it waited past its deadline (<see cref="SweepTickSteps"/>).
        /// </summary>
        sealed class TickStep(Player player, Action orElse, DateTime? deadlineUtc)
        {
            int _claimed;
            public Player Player { get; } = player;
            public Action OrElse { get; } = orElse;
            public DateTime? DeadlineUtc { get; } = deadlineUtc;
            public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
        }

        /// <summary>
        /// Runs <paramref name="step"/> on the player's tick, or <paramref name="orElse"/> off it when the step can not run:
        /// never both, never neither (a request's credits, item or tiles are always either used or given back).
        /// </summary>
        void DispatchOrElse(Player player, Action step, Action orElse, TimeSpan? timeout)
        {
            var pending = new TickStep(player, orElse, timeout is { } t ? DateTime.UtcNow + t : null);
            _tickSteps[pending] = 0;
            if (!Dispatch(player, () =>
                {
                    if (!pending.TryClaim())
                        return;
                    _tickSteps.TryRemove(pending, out _);
                    step();
                }, skipped: () => GiveUp(pending)))
                GiveUp(pending);
        }

        void GiveUp(TickStep pending)
        {
            if (!pending.TryClaim())
                return;
            _tickSteps.TryRemove(pending, out _);
            try
            {
                pending.OrElse();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "City request could not be given up character=" + pending.Player.Identity.Instance);
            }
        }

        /// <summary>Gives up every tick step whose player has left, or that waited past its deadline. Off the tick.</summary>
        void SweepTickSteps()
        {
            DateTime now = DateTime.UtcNow;
            foreach (TickStep pending in _tickSteps.Keys)
                if (!IsInPlay(pending.Player) || pending.DeadlineUtc is { } deadline && deadline <= now)
                    GiveUp(pending);
        }

        /// <summary>The tick steps still waiting, for tests.</summary>
        internal int TickStepsWaiting => _tickSteps.Count;

        // ------------------------------------------------------------------------------------------------ purchase

        /// <summary>"Click to Purchase land area" (ClientRequestBuy). On the tick.</summary>
        public void Buy(Player player, int cityInstance)
        {
            if (!TryRequest(player, cityInstance, out _, out CityControllerDefinition controller, out CityPlotState state))
                return;
            int organizationId = player.Organization?.Id ?? 0;
            if (organizationId <= 0)
            {
                SendText(player, "You must be in an organization to buy a city district.");
                return;
            }

            if (!IsLeader(player, organizationId))
            {
                SendText(player, NotLeaderText);
                return;
            }

            if (state.IsOwned)
            {
                Send(player, Feedback(player, CityText.Category, CityText.AlreadyOccupied));
                return;
            }

            if (PlotOf(organizationId) != null)
            {
                Send(player, Feedback(player, CityText.Category, CityText.OrganizationOwnsCityShort));
                return;
            }

            if (state.IsLockedOut(organizationId, DateTime.UtcNow))
            {
                SendText(player, "Your organization lost this district without building a headquarters and may not claim it again yet.");
                return;
            }

            if (!BankCanPay(organizationId, controller.PurchaseCost))
            {
                SendText(player, BankTooLowText(controller.PurchaseCost));
                return;
            }

            if (!BeginWork(player))
                return;
            int characterId = player.Identity.Instance;
            long price = controller.PurchaseCost;
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                CityClaimResult claim;
                try
                {
                    // One transaction: the price leaves the organization's bank and the district is the organization's, or neither.
                    claim = _store.Claim(controller.PlayfieldId, controller.Index, organizationId, characterId, price,
                        Rules.StartingControllerCharge, Rules.BillingPeriod);
                }
                catch
                {
                    Dispatch(player, () => SendText(player, "The purchase could not be completed."));
                    throw;
                }
                finally
                {
                    EndWork(player);
                }

                if (claim.Status != CityClaimStatus.Claimed || claim.State == null)
                {
                    Refresh(controller.PlayfieldId, controller.Index);
                    if (claim.Status == CityClaimStatus.NotEnoughInBank)
                        // The cached balance was stale: bring it up to date so the window greys the purchase out.
                        _organizations.BankChangedElsewhere(organizationId);
                    Dispatch(player, () => Send(player, claim.Status switch
                    {
                        CityClaimStatus.AlreadyOwned => Feedback(player, CityText.Category, CityText.AlreadyOccupied),
                        CityClaimStatus.OrganizationOwnsCity => Feedback(player, CityText.Category, CityText.OrganizationOwnsCityShort),
                        CityClaimStatus.LockedOut => Text(player, "Your organization may not claim this district again yet."),
                        CityClaimStatus.NotLeader => Text(player, NotLeaderText),
                        CityClaimStatus.NotEnoughInBank => Text(player, BankTooLowText(price)),
                        _ => Text(player, "You must be in an organization to buy a city district.")
                    }));
                    return;
                }

                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "City district bought playfield={0} controller={1} org={2} character={3} price={4} bank={5}",
                    controller.PlayfieldId, controller.Index, organizationId, characterId, price, claim.Bank));
                // Publishing the new owner reopens the buyer's window as the district's terminal.
                Publish(claim.State);
                _organizations.BankChangedElsewhere(organizationId);
                Dispatch(player, () => SendText(player, string.Format(CultureInfo.InvariantCulture,
                    "Your organization bought this district for {0:N0} credits from its bank.", price)));
            });
        }

        static string BankTooLowText(long price)
            => string.Format(CultureInfo.InvariantCulture,
                "Your organization's bank does not hold the {0:N0} credits this district costs.", price);

        // ------------------------------------------------------------------------------------------------ build

        /// <summary>"Construct Buildings": one house dragged onto the map (ClientRequestBuild). On the tick.</summary>
        public void Build(Player player, ClientRequestBuildMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!TryRequest(player, message.CityInstance, out _, out CityControllerDefinition controller, out CityPlotState state))
                return;
            int organizationId = player.Organization?.Id ?? 0;
            if (organizationId <= 0 || state.OrganizationId != organizationId || !state.IsActive)
                return;
            if (!CanManage(player, organizationId))
            {
                SendText(player, "Your rank in the organization is too low to build in its city.");
                return;
            }

            if ((int)message.Template.Type != HouseTemplateRecordType
                || !_catalog.TryGetTemplate(message.Template.Instance, out HouseTemplateDefinition template))
                return;
            if (!TryFindHouseItem(player, message.Item, out Identity slot, out Item item) || _trades.TryGetSession(player, out _))
            {
                SendText(player, "The house must be in your inventory.");
                return;
            }

            if (item.GetStat(CharacterStat.HouseTemplate) != template.Id)
                return;
            // One item builds one house: a stack would be retired whole.
            if (item.StackCount != 1)
            {
                SendText(player, "Split the stack first: one house item builds one house.");
                return;
            }

            int side = item.GetStat(CharacterStat.Side);
            if (side != AnySide && side != player.Stats.GetOrZero(CharacterStat.Side, StatDetail.Base))
            {
                SendText(player, "This house can not be built by your side.");
                return;
            }

            BuildRefusal refusal = CityPlacement.Check(controller, template, message.Tile.X, message.Tile.Z, message.Rotation, state.Houses,
                TemplateOf, Rules.MaxHousesPerPlot, allowMarket: true);
            if (refusal != BuildRefusal.None)
            {
                SendRefusal(player, refusal, template);
                return;
            }

            if (!BeginWork(player))
                return;
            int characterId = player.Identity.Instance;
            int instanceId = item.InstanceId, low = item.LowId, high = item.HighId, quality = item.Quality;
            int tileX = message.Tile.X, tileZ = message.Tile.Z, rotation = message.Rotation;
            CityManagePermission permission = ManagePermission();
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                CityWriteResult reserved;
                try
                {
                    reserved = _store.ReserveHouse(controller, _catalog, organizationId, characterId, template, tileX, tileZ, rotation,
                        low, high, quality, permission);
                }
                catch
                {
                    EndWork(player);
                    throw;
                }

                if (reserved.Status != CityWriteStatus.Done || reserved.State == null)
                {
                    EndWork(player);
                    Refresh(controller.PlayfieldId, controller.Index);
                    Dispatch(player, () =>
                    {
                        if (reserved.Status == CityWriteStatus.Refused)
                            SendRefusal(player, reserved.Refusal, template);
                        else
                            SendText(player, "The house could not be built.");
                    });
                    return;
                }

                Publish(reserved.State);
                long houseId = reserved.HouseId;
                DispatchOrElse(player, () => TakeHouseItem(player, controller, template, organizationId, houseId, slot, instanceId),
                    () => AbandonHouse(player, controller, houseId), TickStepTimeout);
            });
        }

        /// <summary>
        /// On the builder's tick, the tiles reserved: the house item is retired (memory now, its row in the builder's lane),
        /// and the house is confirmed in the lane behind that retire. An item no longer where it was gives the reservation up.
        /// </summary>
        void TakeHouseItem(Player player, CityControllerDefinition controller, HouseTemplateDefinition template, int organizationId,
            long houseId, Identity slot, int instanceId)
        {
            Container page = player.Inventory.Inventory;
            bool committed = false;
            if (!player.IsPersistenceQuarantined && player.Session != null && player.Organization?.Id == organizationId
                && !_trades.TryGetSession(player, out _)
                && page.Content.TryGetValue(slot.Instance, out Item? item) && item.InstanceId == instanceId && !item.Locked
                && item.StackCount == 1)
            {
                var graveyard = new Identity { Type = IdentityType.None, Instance = player.Identity.Instance };
                committed = _inventory.TryCommit(player,
                    [new InventoryRowChange(item, graveyard, item.InstanceId, item.StackCount, Retired: true)],
                    () => page.Content.TryGetValue(slot.Instance, out Item? current) && ReferenceEquals(current, item)
                        && item.InstanceId == instanceId && !item.Locked && item.StackCount == 1,
                    () =>
                    {
                        page.Content.Remove(slot.Instance);
                        player.Session?.Send(new CharacterActionMessage
                        {
                            Identity = player.Identity, Action = CharacterActionType.DeleteItem, Target = slot
                        });
                    });
            }

            if (!committed)
            {
                AbandonHouse(player, controller, houseId);
                SendText(player, "The house must be in your inventory.");
                return;
            }

            bool headquarters = template.Type == HouseType.Headquarters;
            // Behind the retire in the same lane: the house never stands while its item is still stored.
            _flush.Enqueue(player, () =>
            {
                bool confirmed;
                try
                {
                    confirmed = _store.ConfirmHouse(controller.PlayfieldId, controller.Index, houseId, organizationId, headquarters);
                }
                finally
                {
                    EndWork(player);
                }

                if (!confirmed)
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                        "City house reservation gone before its item was confirmed playfield={0} controller={1} house={2} character={3}",
                        controller.PlayfieldId, controller.Index, houseId, player.Identity.Instance));
                else
                    _logger.Info(string.Format(CultureInfo.InvariantCulture,
                        "City house built playfield={0} controller={1} house={2} template={3} character={4}",
                        controller.PlayfieldId, controller.Index, houseId, template.Id, player.Identity.Instance));
                Refresh(controller.PlayfieldId, controller.Index);
            });
        }

        void AbandonHouse(Player player, CityControllerDefinition controller, long houseId)
        {
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                try
                {
                    Publish(_store.AbandonHouse(controller.PlayfieldId, controller.Index, houseId));
                }
                finally
                {
                    EndWork(player);
                }
            });
        }

        /// <summary>The house item the request names: a main inventory slot (its page identity), or the item's own identity there.</summary>
        static bool TryFindHouseItem(Player player, Identity named, out Identity slot, out Item item)
        {
            slot = default;
            item = null!;
            if (!player.Inventory.IsHydrated)
                return false;
            Container page = player.Inventory.Inventory;
            if (named.Type == IdentityType.Inventory)
            {
                slot = named;
                return page.Content.TryGetValue(named.Instance, out item!) && !item.Locked && item.InstanceId > 0;
            }

            foreach (KeyValuePair<int, Item> entry in page.Content)
            {
                if (entry.Value.Identity.Equals(named) && !entry.Value.Locked && entry.Value.InstanceId > 0)
                {
                    slot = new Identity(IdentityType.Inventory, entry.Key);
                    item = entry.Value;
                    return true;
                }
            }

            return false;
        }

        HouseTemplateDefinition? TemplateOf(int id) => _catalog.TryGetTemplate(id, out HouseTemplateDefinition t) ? t : null;

        void SendRefusal(Player player, BuildRefusal refusal, HouseTemplateDefinition template)
        {
            switch (refusal)
            {
                case BuildRefusal.OneOfTypeOnly:
                    Send(player, Feedback(player, CityText.BuildCategory, template.Type switch
                    {
                        HouseType.Headquarters => CityText.HeadquartersBuilt,
                        HouseType.Grid => CityText.GridHouseBuilt,
                        HouseType.CloakingDevice => CityText.CloakingDeviceBuilt,
                        HouseType.TaxOffice => CityText.TaxOfficeBuilt,
                        HouseType.RadarStation => CityText.SatelliteUplinkBuilt,
                        _ => CityText.BuildingBuilt
                    }));
                    break;
                case BuildRefusal.OneOfTemplateOnly:
                    Send(player, Feedback(player, CityText.BuildCategory, CityText.BuildingBuilt));
                    break;
                case BuildRefusal.NeedsHeadquarters:
                    SendText(player, "Build the organization headquarters first.");
                    break;
                case BuildRefusal.TooManyHouses:
                    SendText(player, "This district holds no more houses.");
                    break;
                case BuildRefusal.MarketNotAllowed:
                    SendText(player, "A market can only be built in a private city.");
                    break;
                default:
                    SendText(player, "The house can not be built there.");
                    break;
            }
        }

        // ------------------------------------------------------------------------------------------------ demolish, cloak

        /// <summary>"Demolish Building" on a built house (ClientRequestDemolish). On the tick.</summary>
        public void Demolish(Player player, int cityInstance, CityTilePos tile)
        {
            if (!TryRequest(player, cityInstance, out _, out CityControllerDefinition controller, out CityPlotState state))
                return;
            int organizationId = player.Organization?.Id ?? 0;
            if (organizationId <= 0 || state.OrganizationId != organizationId || !state.IsActive)
                return;
            if (!CanManage(player, organizationId))
            {
                SendText(player, "Your rank in the organization is too low to demolish its houses.");
                return;
            }

            if (CityPlacement.HouseAt(state.Houses, tile.X, tile.Z) is not { Visible: true, DemolishAtUtc: null } || !BeginWork(player))
                return;
            int characterId = player.Identity.Instance;
            string name = player.Name ?? string.Empty;
            CityManagePermission permission = ManagePermission();
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                try
                {
                    CityWriteResult result = _store.StartDemolish(controller, _catalog, organizationId, characterId, tile.X, tile.Z, permission);
                    if (result.Status == CityWriteStatus.Done && result.State != null)
                    {
                        Publish(result.State);
                        _logger.Info(string.Format(CultureInfo.InvariantCulture,
                            "City house demolish started playfield={0} controller={1} house={2} character={3}",
                            controller.PlayfieldId, controller.Index, result.HouseId, characterId));
                        if (CityPlacement.HouseAt(result.State.Houses, tile.X, tile.Z) is { } house
                            && TemplateOf(house.TemplateId)?.Type == HouseType.Headquarters)
                            NotifyMembers(organizationId, CityText.NoticeCategory, CityText.HeadquartersRemovalStarted, name, PlayfieldName(controller));
                    }
                    else
                    {
                        Refresh(controller.PlayfieldId, controller.Index);
                    }
                }
                finally
                {
                    EndWork(player);
                }
            });
        }

        /// <summary>The cloaking device's toggle (ToggleCloak). On the tick.</summary>
        public void ToggleCloak(Player player, int cityInstance)
        {
            if (!TryRequest(player, cityInstance, out _, out CityControllerDefinition controller, out CityPlotState state))
                return;
            int organizationId = player.Organization?.Id ?? 0;
            if (organizationId <= 0 || state.OrganizationId != organizationId || !state.IsActive || !CanManage(player, organizationId))
                return;
            (int cloak, int timer) = CloakSignal(state, DateTime.UtcNow);
            if (cloak == 0 || timer >= 0 || RaidRunning(controller.PlayfieldId, controller.Index) || !BeginWork(player))
                return;
            int characterId = player.Identity.Instance;
            string name = player.Name ?? string.Empty;
            CityManagePermission permission = ManagePermission();
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                try
                {
                    CityWriteResult result = _store.ToggleCloak(controller, _catalog, organizationId, characterId, permission);
                    if (result.Status == CityWriteStatus.Done && result.State != null)
                    {
                        Publish(result.State);
                        NotifyMembers(organizationId, CityText.NoticeCategory, CityText.CloakToggled, name, PlayfieldName(controller));
                        if (result.State.CloakState == 0)
                            ScheduleRaid(controller, result.State, characterId);
                    }
                    else
                    {
                        Refresh(controller.PlayfieldId, controller.Index);
                    }
                }
                finally
                {
                    EndWork(player);
                }
            });
        }

        string PlayfieldName(CityControllerDefinition controller)
            => _catalog.TryGetPlayfield(controller.PlayfieldId, out CityPlayfieldDefinition city) && city.Name.Length > 0
                ? city.Name : controller.PlayfieldId.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------------------------------------ publishing

        /// <summary>Reads a district back after another change and publishes it. Off the tick.</summary>
        void Refresh(int playfieldId, int index)
        {
            try
            {
                Publish(_store.Load(playfieldId, index));
            }
            catch (Exception exception) when (exception is MySqlException or InvalidOperationException or TimeoutException)
            {
                _logger.Error(exception, "City district reload failed " + playfieldId + "/" + index);
            }
        }

        /// <summary>
        /// Keeps the newer version of a district and tells its playfield's clients what changed: houses added (signal 2),
        /// being demolished (8) and gone (9); and refreshes windows open on it and its owners' advantages.
        /// </summary>
        void Publish(CityPlotState next)
        {
            ArgumentNullException.ThrowIfNull(next);
            lock (_publishLock)
            {
                CityPlotState? previous = _plots.TryGetValue(next.Key, out CityPlotState? cached) ? cached : null;
                if (previous != null && previous.Version >= next.Version)
                    return;
                _plots[next.Key] = next;
                // Queued in publish order, so a playfield applies every district's versions in order.
                if (_playfields.Value.TryGet(next.PlayfieldId, out PlayfieldInstance? playfield) && playfield != null)
                    playfield.Dispatch(() => ApplyToPlayfield(playfield, previous, next));
                RefreshAdvantages(previous, next);
            }
        }

        void ApplyToPlayfield(PlayfieldInstance playfield, CityPlotState? previous, CityPlotState next)
        {
            var messages = new List<CityHouseData?>();
            var removed = new List<CityTilePos>();
            var started = new List<CityTilePos>();
            Dictionary<long, CityHouse> before = (previous?.Houses ?? []).Where(h => h.Visible).ToDictionary(h => h.HouseId);
            foreach (CityHouse house in next.Houses.Where(h => h.Visible))
            {
                if (!before.Remove(house.HouseId, out CityHouse? old))
                    messages.Add(HouseData(playfield, house));
                else if (old.DemolishAtUtc == null && house.DemolishAtUtc != null)
                    started.Add(new CityTilePos(house.TileX, house.TileZ));
            }

            foreach (CityHouse gone in before.Values)
                removed.Add(new CityTilePos(gone.TileX, gone.TileZ));

            var players = new List<Player>();
            playfield.GetRequiredService<DynelRegistry>().CopyPlayers(players);
            if (messages.Count + removed.Count + started.Count > 0)
            {
                foreach (Player player in players)
                {
                    if (player.Session is not { } session)
                        continue;
                    foreach (CityTilePos tile in removed)
                        session.Send(new AOTransportSignalMessage { Identity = player.Identity, Signal = CitySignal.HouseRemoved, Tile = tile });
                    foreach (CityHouseData? house in messages)
                        if (house != null)
                            session.Send(new AOTransportSignalMessage { Identity = player.Identity, Signal = CitySignal.HouseAdded, House = house });
                    foreach (CityTilePos tile in started)
                        session.Send(new AOTransportSignalMessage { Identity = player.Identity, Signal = CitySignal.HouseDemolishStarted, Tile = tile });
                }
            }

            SyncFixtures(playfield);

            if (!_catalog.TryGetController(next.PlayfieldId, next.ControllerIndex, out CityControllerDefinition controller))
                return;
            foreach (CityWindow window in _windows.Values)
            {
                if (window.PlayfieldId != next.PlayfieldId || window.ControllerIndex != next.ControllerIndex
                    || !ReferenceEquals(window.Player.Playfield, playfield))
                    continue;
                // The owner changed under an open window (bought, lost): reopen it for what the player may do now.
                if (previous?.OrganizationId != next.OrganizationId && ControllerDynel(playfield, controller) is { } dynel)
                {
                    window.Player.Session?.Send(new AOTransportSignalMessage
                    {
                        Identity = window.Player.Identity, Signal = CitySignal.CloseGuiSession, City = CityIdOf(playfield, controller)
                    });
                    _windows.TryRemove(new KeyValuePair<int, CityWindow>(window.Player.Identity.Instance, window));
                    if (IsWindowCurrent(window, controller))
                        OpenWindow(window.Player, dynel);
                }
                else
                {
                    SendWindowState(window.Player, controller, next);
                }
            }
        }

        /// <summary>
        /// The house as the client reads it: its corner tile's position (the client takes the tile back as floor(x / 4),
        /// floor(z / 4), City 10016317), on the ground there, its rotation, whether it is being demolished, its template.
        /// </summary>
        static CityHouseData HouseData(PlayfieldInstance playfield, CityHouse house)
        {
            float x = house.TileX * 4f, z = house.TileZ * 4f;
            float y = playfield.Geometry.Collision?.Terrain is { } terrain && terrain.TryGetHeight(x, z, out float ground) ? ground : 0f;
            return new CityHouseData
            {
                Position = new LostEden.Protocol.GameData.Vector3(x, y, z),
                Rotation = house.Rotation,
                Flag = house.DemolishAtUtc != null,
                Template = new Identity((IdentityType)HouseTemplateRecordType, house.TemplateId)
            };
        }

        /// <summary>Every shown house of the playfield's districts, for PlayfieldAllCities on zone-in. On the tick.</summary>
        public List<CityHouseData> HousesFor(PlayfieldInstance playfield)
        {
            var houses = new List<CityHouseData>();
            if (!Enabled || playfield.IsProxyInstance || !_catalog.TryGetPlayfield(playfield.Identity.Instance, out CityPlayfieldDefinition city))
                return houses;
            SyncFixtures(playfield);
            foreach (CityControllerDefinition controller in city.Controllers)
            {
                foreach (CityHouse house in Plot(city.PlayfieldId, controller.Index).Houses)
                {
                    if (!house.Visible)
                        continue;
                    if (houses.Count >= CityLimits.MaxHousesPerPlayfield)
                    {
                        _logger.Error("City houses exceed what PlayfieldAllCities carries playfield=" + city.PlayfieldId);
                        return houses;
                    }

                    houses.Add(HouseData(playfield, house));
                }
            }

            return houses;
        }

        // ------------------------------------------------------------------------------------------------ fixtures

        /// <summary>What a shown house spawned on a playfield: its doors (DoorData_t) and guards (GuardData_t).</summary>
        sealed class HouseFixtures
        {
            public readonly List<StaticDynel> Doors = [];
            public readonly List<NpcCharacter> Guards = [];
        }

        // Per playfield object, by house id. Read and written only on that playfield's tick.
        readonly ConditionalWeakTable<PlayfieldInstance, Dictionary<long, HouseFixtures>> _fixtures = new();

        /// <summary>
        /// Gives every shown house of the playfield's districts its doors and guards and takes them from houses no longer
        /// shown. A door is its item template spawned as a static with its walk-in pad (a Grid House's Grid Door,
        /// 297174 / 297182 / 297183, runs its OnTargetInVicinity TeleportProxy2 into the Grid); a guard is its NPC hash
        /// spawned at the level stored with it. Each is placed as House_c::CalculateGlobalDoorPosRot (City 10012c5b) and
        /// CalculateGlobalGuardPosRot (10012c97) place them. On the playfield's tick.
        /// </summary>
        void SyncFixtures(PlayfieldInstance playfield)
        {
            if (!Enabled || playfield.IsProxyInstance || !_catalog.TryGetPlayfield(playfield.Identity.Instance, out CityPlayfieldDefinition city))
                return;
            Dictionary<long, HouseFixtures> spawned = _fixtures.GetOrCreateValue(playfield);
            var shown = new Dictionary<long, CityHouse>();
            foreach (CityControllerDefinition controller in city.Controllers)
                foreach (CityHouse house in Plot(city.PlayfieldId, controller.Index).Houses)
                    if (house.Visible)
                        shown[house.HouseId] = house;

            foreach (long id in spawned.Keys.Where(id => !shown.ContainsKey(id)).ToList())
            {
                RemoveFixtures(playfield, id, spawned[id]);
                spawned.Remove(id);
            }

            foreach ((long id, CityHouse house) in shown)
                if (!spawned.ContainsKey(id))
                    spawned[id] = SpawnFixtures(playfield, house);
        }

        HouseFixtures SpawnFixtures(PlayfieldInstance playfield, CityHouse house)
        {
            var fixtures = new HouseFixtures();
            if (!_catalog.TryGetTemplate(house.TemplateId, out HouseTemplateDefinition template))
                return fixtures;
            SpawnService spawns = playfield.GetRequiredService<SpawnService>();
            float baseX = house.TileX * 4f, baseZ = house.TileZ * 4f;
            float baseY = playfield.Geometry.Collision?.Terrain is { } terrain && terrain.TryGetHeight(baseX, baseZ, out float ground) ? ground : 0f;

            foreach (HouseDoorDefinition door in template.Doors)
            {
                (AORebirth.Core.Vector.Vector3 position, AORebirth.Core.Vector.Quaternion heading) =
                    Place(template, house.Rotation, door.X, door.Y, door.Z, door.Turn, baseX, baseY, baseZ);
                StaticDynel? dynel = spawns.SpawnStaticTemplate(door.Template, position, heading);
                if (dynel != null)
                {
                    fixtures.Doors.Add(dynel);
                    if (template.InsideTemplate > 0)
                    {
                        // The way back out lands in front of this door; stepping into it goes in.
                        CityInsides.AddDoor(playfield.Identity.Instance, dynel.Identity.Instance,
                            WorldSimulation.PortalDoorLandingResolver.LandingInFront(position, heading, WorldSimulation.PortalDoorLandingResolver.ExitDoorClearance),
                            heading);
                        long houseId = house.HouseId;
                        int doorTemplate = door.Template;
                        playfield.WorldAccess.Instance?.RegisterEntryPad(dynel.Identity, doorTemplate, position.xf, position.yf, position.zf,
                            player => EnterInside(player, playfield, houseId, dynel));
                    }
                }
                else
                    _logger.Warn("City house " + house.HouseId + " door template " + door.Template + " is not in the item catalog");
            }

            foreach (HouseGuardDefinition guard in template.Guards)
            {
                (AORebirth.Core.Vector.Vector3 position, AORebirth.Core.Vector.Quaternion heading) =
                    Place(template, house.Rotation, guard.X, guard.Y, guard.Z, guard.Turn, baseX, baseY, baseZ);
                try
                {
                    // GuardData_t +0x30 taken as the spawn level: 300 at the guard houses, 90 at the small Neutral HQ.
                    fixtures.Guards.AddRange(spawns.SpawnBranches(guard.Hash, position, heading, guard.Value > 0 ? guard.Value : null));
                }
                catch (KeyNotFoundException)
                {
                    _logger.Warn("City house " + house.HouseId + " guard hash " + guard.Hash + " is not an NPC template");
                }
            }

            return fixtures;
        }

        void RemoveFixtures(PlayfieldInstance playfield, long houseId, HouseFixtures fixtures)
        {
            SpawnService spawns = playfield.GetRequiredService<SpawnService>();
            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            foreach (StaticDynel door in fixtures.Doors)
            {
                if (CityInsides.TryGetDoorReturn(playfield.Identity.Instance, door.Identity.Instance, out AORebirth.Core.Vector.Vector3 outside,
                        out AORebirth.Core.Vector.Quaternion outsideHeading))
                {
                    EvictInside(houseId, playfield.Identity.Instance, outside, outsideHeading);
                    CityInsides.RemoveDoor(playfield.Identity.Instance, door.Identity.Instance);
                }
                if (ReferenceEquals(door.Playfield, playfield))
                    spawns.DespawnStatic(door);
            }
            foreach (NpcCharacter guard in fixtures.Guards)
                if (registry.TryGet(guard.Identity, out ZoneEngine_New.Core.Entities.Dynel? live) && ReferenceEquals(live, guard))
                    spawns.DespawnNpc(guard);
        }

        /// <summary>
        /// A character stepping into a house's door: into the house's own copy of its inside (CityInsides), landing in front
        /// of the inside's door as a return-recording proxy lands, the house door recorded as the way back. Only the house's
        /// organization goes in: City.dll's HouseInside_c::TrackVisitors (10013059) starts teleporting out every visitor of
        /// another organization. On the city playfield's tick.
        /// </summary>
        void EnterInside(Player player, PlayfieldInstance source, long houseId, StaticDynel door)
        {
            if (player.Session == null || !ReferenceEquals(player.Playfield, source)
                || DescribeInside(houseId) is not { } inside || !CityInsides.TryCreate(houseId, out int target))
                return;
            if ((player.Organization?.Id ?? 0) != inside.OrganizationId)
                return;

            PlayfieldGeometryData geometry = _gameData.GetPlayfieldGeometry(inside.ResourceId);
            // The inside's way out: the door its placement marks as an exit (ExitInstance), as the client registers it;
            // the first door when none is marked.
            List<AODB.Common.RDBObjects.PlayfieldDynel> doors = geometry.Dynels?.Dynels?.Where(d => d.IdentityType == (int)IdentityType.Door).ToList() ?? [];
            AODB.Common.RDBObjects.PlayfieldDynel? entry =
                (Rules.InsideEntryDoors.TryGetValue(inside.ResourceId, out int configured) ? doors.FirstOrDefault(d => d.IdentityInstance == configured) : null)
                ?? doors.FirstOrDefault(d => WorldSimulation.PlayfieldWorldSimulation.ExitInstanceOf(d) > 0)
                ?? doors.FirstOrDefault();
            if (entry == null || !WorldSimulation.PortalDoorLandingResolver.TryResolveDoorLanding(geometry, entry.IdentityInstance,
                    WorldSimulation.PortalDoorLandingResolver.ProxyEntryDoorClearance, out AORebirth.Core.Vector.Vector3 landing,
                    out AORebirth.Core.Vector.Quaternion heading))
            {
                _logger.Warn("City house " + houseId + " inside " + inside.ResourceId + " has no door to land at");
                return;
            }

            player.Stats.Set(CharacterStat.ExternalPlayfieldInstance, source.Identity.Instance, StatDetail.Base, dirty: true);
            player.Stats.Set(CharacterStat.ExternalDoorInstance, door.Identity.Instance, StatDetail.Base, dirty: true);
            player.Rotation = heading;
            source.GetService<WorldSimulation.WorldSimulationAccess>()?.Instance?.ForgetCharacterTriggers(player.Identity.Instance);
            int entryDoor = entry.IdentityInstance;
            source.GetRequiredService<PlayfieldManager>().WithPlayfield(target, player, arrival =>
            {
                if (!ReferenceEquals(player.Playfield, source) || player.Session is not { IsTransferring: false } session)
                    return;
                if (arrival is ACGPlayfield acg)
                    acg.World?.RegisterExitProxyDoor(entryDoor);
                session.TransferToPlayfield(arrival, landing, heading);
            }, new World.RemoteArrival(landing, heading, AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport, entryDoor));
        }

        /// <summary>
        /// A house gone: everyone in its inside is put back in front of its door, as HouseInside_c::TrackVisitors (10013059)
        /// starts teleporting out every visitor once the house is demolished.
        /// </summary>
        void EvictInside(long houseId, int cityPlayfieldId, AORebirth.Core.Vector.Vector3 outside, AORebirth.Core.Vector.Quaternion heading)
        {
            if (!CityInsides.TryCreate(houseId, out int insideId) || !_playfields.Value.TryGet(insideId, out PlayfieldInstance? inside) || inside == null)
                return;
            inside.Dispatch(() =>
            {
                var players = new List<Player>();
                inside.GetRequiredService<DynelRegistry>().CopyPlayers(players);
                foreach (Player player in players)
                {
                    if (player.Session == null || !ReferenceEquals(player.Playfield, inside))
                        continue;
                    player.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 0, StatDetail.Base, dirty: true);
                    player.Stats.Set(CharacterStat.ExternalDoorInstance, 0, StatDetail.Base, dirty: true);
                    player.Rotation = heading;
                    inside.GetRequiredService<PlayfieldManager>().WithPlayfield(cityPlayfieldId, player, arrival =>
                    {
                        if (!ReferenceEquals(player.Playfield, inside) || player.Session is not { IsTransferring: false } session)
                            return;
                        session.TransferToPlayfield(arrival, outside, heading);
                    }, new World.RemoteArrival(outside, heading, AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport));
                }
            });
        }

        /// <summary>
        /// A house with an inside, as CityInsides describes it: the house's inside playfield, its city and organization, and
        /// the landing in front of its first door. Null for a house that is not shown or has no inside.
        /// </summary>
        CityInsides.Target? DescribeInside(long houseId)
        {
            foreach (CityPlotState plot in _plots.Values)
            {
                CityHouse? house = plot.Houses.FirstOrDefault(h => h.HouseId == houseId && h.Visible);
                if (house == null)
                    continue;
                if (!_catalog.TryGetTemplate(house.TemplateId, out HouseTemplateDefinition template) || template.InsideTemplate <= 0
                    || template.Doors.Count == 0)
                    return null;
                float baseX = house.TileX * 4f, baseZ = house.TileZ * 4f;
                float baseY = _gameData.GetPlayfieldGeometry(plot.PlayfieldId).Collision?.Terrain is { } terrain
                    && terrain.TryGetHeight(baseX, baseZ, out float ground) ? ground : 0f;
                HouseDoorDefinition door = template.Doors[0];
                (AORebirth.Core.Vector.Vector3 position, AORebirth.Core.Vector.Quaternion heading) =
                    Place(template, house.Rotation, door.X, door.Y, door.Z, door.Turn, baseX, baseY, baseZ);
                return new CityInsides.Target(houseId, template.InsideTemplate, plot.PlayfieldId, plot.OrganizationId ?? 0,
                    WorldSimulation.PortalDoorLandingResolver.LandingInFront(position, heading, WorldSimulation.PortalDoorLandingResolver.ExitDoorClearance),
                    heading);
            }

            return null;
        }

        /// <summary>
        /// A door's or guard's place: its house-local position turned as CityHouseTemplate_c::RotateLocalPos (1000e0db)
        /// turns it (less the unturned layout's centre, turned by -rotation about up, plus the turned centre's absolute
        /// components), plus the house's corner; its heading the turn about up by -((stored turn) + rotation) degrees
        /// (1000e09c), the stored turn being the record's negated (10010c07).
        /// </summary>
        static (AORebirth.Core.Vector.Vector3, AORebirth.Core.Vector.Quaternion) Place(HouseTemplateDefinition template, int rotation,
            float x, float y, float z, int turn, float baseX, float baseY, float baseZ)
        {
            double a = -rotation * Math.PI / 180.0, cos = Math.Cos(a), sin = Math.Sin(a);
            double cx = template.Footprint.TilesX * 4.0 * 0.5, cz = template.Footprint.TilesZ * 4.0 * 0.5;
            (double X, double Z) Turned(double px, double pz) => (px * cos + pz * sin, -px * sin + pz * cos);
            (double tx, double tz) = Turned(x - cx, z - cz);
            (double ccx, double ccz) = Turned(cx, cz);
            var position = new AORebirth.Core.Vector.Vector3(baseX + tx + Math.Abs(ccx), baseY + y, baseZ + tz + Math.Abs(ccz));
            double yaw = (turn - rotation) * Math.PI / 180.0;
            var heading = new AORebirth.Core.Vector.Quaternion(0, Math.Sin(yaw / 2.0), 0, Math.Cos(yaw / 2.0));
            return (position, heading);
        }

        // ------------------------------------------------------------------------------------------------ timers

        /// <summary>Reloads every district whose version moved: another instance's changes and the due sweep's.</summary>
        void Sync()
        {
            if (_disposed || Interlocked.Exchange(ref _syncing, 1) != 0)
                return;
            try
            {
                SweepTickSteps();
                foreach (((int playfieldId, int index), long version) in _store.Versions())
                {
                    if (_plots.TryGetValue((playfieldId, index), out CityPlotState? cached) && cached.Version >= version)
                        continue;
                    if (_catalog.TryGetController(playfieldId, index, out _))
                        Publish(_store.Load(playfieldId, index));
                }

                foreach ((int character, CityWindow window) in _windows)
                    if (window.Player.Session == null || !_playfields.Value.FindPlayer(character, out Player current) || !ReferenceEquals(current, window.Player))
                        _windows.TryRemove(new KeyValuePair<int, CityWindow>(character, window));
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "City sync failed");
            }
            finally
            {
                Volatile.Write(ref _syncing, 0);
            }
        }

        /// <summary>Bills, warnings, finished demolitions, lost districts, stale claims. Only World's duty holder sweeps.</summary>
        void SweepDue()
        {
            if (_disposed || (_world != null && !_world.IsDutyHolder) || Interlocked.Exchange(ref _sweeping, 1) != 0)
                return;
            try
            {
                foreach ((int playfieldId, int index) in _store.DuePlots(Rules))
                {
                    if (!_catalog.TryGetController(playfieldId, index, out CityControllerDefinition controller))
                        continue;
                    Queue((playfieldId, index), () =>
                    {
                        (IReadOnlyList<CityDueEvent> events, CityPlotState state) = _store.ProcessDue(controller, _catalog);
                        Publish(state);
                        foreach (CityDueEvent due in events)
                            Announce(due);
                    }).Wait();
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "City due sweep failed");
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }

        void Announce(CityDueEvent due)
        {
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "City {0} org={1} upkeep={2} bank={3}", due.Kind, due.OrganizationId, due.Upkeep, due.Bank));
            int upkeep = (int)Math.Min(int.MaxValue, due.Upkeep);
            int bank = (int)Math.Clamp(due.Bank, 0, int.MaxValue);
            switch (due.Kind)
            {
                case CityDueEventKind.UpkeepPaid:
                    NotifyMembersFormatted(due.OrganizationId, CityText.OrgCategory, CityText.UpkeepPaid, upkeep, due.Days);
                    break;
                case CityDueEventKind.UpkeepWarning:
                    NotifyMembersFormatted(due.OrganizationId, CityText.OrgCategory, CityText.UpkeepDue, upkeep, due.Hours);
                    break;
                case CityDueEventKind.DemolishedForUpkeep:
                    NotifyMembersFormatted(due.OrganizationId, CityText.OrgCategory, CityText.UpkeepDemolished, bank, upkeep);
                    break;
                case CityDueEventKind.ReleasedEmpty:
                    NotifyMembersText(due.OrganizationId, "Your organization's city district stood empty and has been lost.");
                    break;
            }
        }

        IEnumerable<Player> OnlineMembers(int organizationId)
            => _playfields.Value.SnapshotPlayers().Where(p => p.Organization?.Id == organizationId && p.Session != null);

        void NotifyMembers(int organizationId, int category, int id, params object[] args)
        {
            foreach (Player member in OnlineMembers(organizationId))
                Dispatch(member, () => ClientFeedback.SendFormattedInCategory(member, category, id, args));
        }

        void NotifyMembersFormatted(int organizationId, int category, int id, params object[] args)
            => NotifyMembers(organizationId, category, id, args);

        void NotifyMembersText(int organizationId, string text)
        {
            foreach (Player member in OnlineMembers(organizationId))
                Dispatch(member, () => SendText(member, text));
        }

        // ------------------------------------------------------------------------------------------------ advantages

        /// <summary>
        /// The advantages a district gives its organization's members: each standing house's house item names up to five
        /// advantage hashes (AdvantageHash1..5, stats 651..655), each an item family in ItemTemplates.json whose OnUse
        /// modifiers are the advantage. Inactive while the controller charge is below the threshold.
        /// </summary>
        public IReadOnlyList<CityAdvantage> Advantages(CityPlotState? state, out bool active)
        {
            active = false;
            var advantages = new List<CityAdvantage>();
            if (state == null || !state.IsActive)
                return advantages;
            active = state.ControllerCharge >= Rules.AdvantageChargeThreshold;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CityHouse house in state.StandingHouses)
            {
                if (!_templates.TryGet(house.ItemLowId, out ItemTemplate item))
                    continue;
                for (CharacterStat stat = CharacterStat.AdvantageHash1; stat <= CharacterStat.AdvantageHash5; stat++)
                {
                    if (!item.Stats.TryGetValue(stat, out int value) || value == 0 || HashText(value) is not { } hash || !seen.Add(hash))
                        continue;
                    if (!_gameData.TryGetHashInstance(hash, out HashInstance family)
                        || !HashItemCatalog.TrySelectIds(family, Rules.AdvantageQuality,
                            id => _templates.TryGet(id, out ItemTemplate t) ? t.Quality : 1, out int low, out int high))
                        continue;
                    ItemTemplate template = _items.CreateTemplate(low, high, Rules.AdvantageQuality);
                    advantages.Add(new CityAdvantage(hash, low, high, Rules.AdvantageQuality, template));
                }
            }

            return advantages;
        }

        /// <summary>A 4-character hash stored as an int, first character highest ("GIHO" = 0x4749484F).</summary>
        internal static string? HashText(int value)
        {
            Span<char> text = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                char c = (char)((value >> (24 - 8 * i)) & 0xFF);
                if (c is < '0' or > 'Z' || (c > '9' && c < 'A'))
                    return null;
                text[i] = c;
            }

            return new string(text);
        }

        /// <summary>
        /// CityAdvantages' N3 header byte. 1 marks the list the player asked for (OrgClient Benefits, the terminal's chat
        /// button; retail's reply carries 1), 0 a list the server pushed on its own (team request 33). The Lost Eden client
        /// applies every list it gets for its own character and writes only an asked-for one to chat.
        /// </summary>
        public const byte AdvantagesRequested = 1, AdvantagesPushed = 0;

        /// <summary>The list each player's client was last sent, so a district change that leaves it as it was sends nothing. Per login.</summary>
        readonly ConditionalWeakTable<Player, AdvantagesItem[]> _sentAdvantages = new();

        /// <summary>OrgClient Benefits (31), "Dump city advantages to chat": one line per advantage, Active or Disabled.</summary>
        public void SendAdvantages(Player player)
        {
            IReadOnlyList<CityAdvantage> advantages = Advantages(PlotOf(player.Organization?.Id ?? 0), out bool active);
            SendAdvantageList(player, WireAdvantages(advantages, active), AdvantagesRequested, always: true);
        }

        /// <summary>
        /// The player's organization changed (or was checked after login): its advantages follow, and its client gets the
        /// full list again when it is not the one it has (an empty one when it has none).
        /// </summary>
        void OnMembershipReconciled(Player player)
        {
            if (!Enabled)
                return;
            ApplyAdvantages(player, PlotOf(player.Organization?.Id ?? 0), always: false);
        }

        /// <summary>
        /// On the player's tick, after its client got its FullCharacter (login, a zone change, a reconnect): the client starts
        /// with no advantages, so it gets the full list again.
        /// </summary>
        public void ClientEntered(Player player)
        {
            if (!Enabled)
                return;
            ApplyAdvantages(player, PlotOf(player.Organization?.Id ?? 0), always: true);
        }

        /// <summary>
        /// On the player's tick: its city advantage layer from its organization's district, and the list pushed to its client
        /// (always, or only when it differs from the last one sent).
        /// </summary>
        void ApplyAdvantages(Player player, CityPlotState? plot, bool always)
        {
            IReadOnlyList<CityAdvantage> advantages = Advantages(plot, out bool active);
            player.SetCityAdvantages(active ? advantages.Select(a => a.Template).ToArray() : []);
            SendAdvantageList(player, WireAdvantages(advantages, active), AdvantagesPushed, always);
        }

        static AdvantagesItem[] WireAdvantages(IReadOnlyList<CityAdvantage> advantages, bool active)
            => advantages.Select(a => new AdvantagesItem { LowId = a.LowId, HighId = a.HighId, Ql = a.Quality, Status = active ? 1 : 0 }).ToArray();

        void SendAdvantageList(Player player, AdvantagesItem[] list, byte origin, bool always)
        {
            if (player.Session == null)
                return;
            if (!always && _sentAdvantages.TryGetValue(player, out AdvantagesItem[]? sent) && SameAdvantages(sent, list))
                return;
            _sentAdvantages.AddOrUpdate(player, list);
            player.Session.Send(new CityAdvantagesMessage { Identity = player.Identity, Unknown = origin, Advantages = list });
        }

        static bool SameAdvantages(AdvantagesItem[] a, AdvantagesItem[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].LowId != b[i].LowId || a[i].HighId != b[i].HighId || a[i].Ql != b[i].Ql || a[i].Status != b[i].Status)
                    return false;
            return true;
        }

        /// <summary>A district's owner or its standing houses changed: the members of the organizations concerned get their advantages again.</summary>
        void RefreshAdvantages(CityPlotState? previous, CityPlotState next)
        {
            var organizations = new HashSet<int>();
            if (previous?.OrganizationId is > 0 and int before)
                organizations.Add(before);
            if (next.OrganizationId is > 0 and int after)
                organizations.Add(after);
            if (organizations.Count == 0)
                return;
            bool sameHouses = previous != null && previous.OrganizationId == next.OrganizationId && previous.IsActive == next.IsActive
                && previous.ControllerCharge == next.ControllerCharge
                && previous.StandingHouses.Select(h => h.HouseId).SequenceEqual(next.StandingHouses.Select(h => h.HouseId));
            if (sameHouses)
                return;
            foreach (int organizationId in organizations)
                foreach (Player member in OnlineMembers(organizationId))
                    Dispatch(member, () => ApplyAdvantages(member, PlotOf(member.Organization?.Id ?? 0), always: false));
        }

        // ------------------------------------------------------------------------------------------------ messages

        static MessageBody Feedback(Player player, int category, int id) => OrgText.Feedback(player.Identity, category, id);

        static MessageBody TextFeedback(Player player, int category, int id, string argument)
            => new FormatFeedbackMessage
            {
                Identity = player.Identity, Unknown = 1, Unknown1 = 0, Unknown2 = 0,
                Message = ClientFeedback.FormatCategoryText(category, id, argument)
            };

        static MessageBody Text(Player player, string text)
            => new FormatFeedbackMessage
            {
                Identity = player.Identity, Unknown = 1, Unknown1 = 0, Unknown2 = 0,
                Message = ClientFeedback.FormatText(ClientFeedback.PlainText, text)
            };

        static void Send(Player player, MessageBody message) => player.Session?.Send(message);

        static void SendText(Player player, string text) => player.Session?.Send(Text(player, text));
    }

    /// <summary>One active or disabled advantage: its item family's hash, the ids and quality used, and the template applied.</summary>
    public sealed record CityAdvantage(string Hash, int LowId, int HighId, int Quality, ItemTemplate Template);

    /// <summary>Client text the city windows use (aodb text.mdb).</summary>
    internal static class CityText
    {
        public const int Category = 110;
        /// <summary>"You have full access to this city terminal." (retail capture 20260623-081344).</summary>
        public const int FullAccess = 8208531;
        /// <summary>"%s already has an open city terminal. You only have limited access to this city terminal."</summary>
        public const int TerminalInUse = 240791923;
        /// <summary>"This city district is already occupied."</summary>
        public const int AlreadyOccupied = 172745013;
        /// <summary>"Your organization already owns a city."</summary>
        public const int OrganizationOwnsCityShort = 81580153;

        public const int OrgCategory = 508;
        /// <summary>"Your organization already owns a city. You only have limited access to this city terminal."</summary>
        public const int OrganizationOwnsCity = 192779843;
        /// <summary>"%d credits have been deducted from the organization bank for the upkeep of your city. Next payment is due in %d days."</summary>
        public const int UpkeepPaid = 192652356;
        /// <summary>"Your city upkeep payment of %d credits is due in %d hour(s). ..."</summary>
        public const int UpkeepDue = 196585349;
        /// <summary>"Insufficient credits (%d) available in the organization bank to pay the city upkeep of %d credits. Your city has been demolished."</summary>
        public const int UpkeepDemolished = 254100788;

        public const int TerminalCategory = 10002;
        /// <summary>"You do not have access to this city terminal."</summary>
        public const int NoAccess = 29711188;

        public const int BuildCategory = 10000;
        public const int HeadquartersBuilt = 237124853;
        public const int GridHouseBuilt = 220762773;
        public const int CloakingDeviceBuilt = 143099062;
        public const int TaxOfficeBuilt = 172734357;
        public const int SatelliteUplinkBuilt = 3332485;
        /// <summary>"You have already built this building in this city."</summary>
        public const int BuildingBuilt = 206323989;

        public const int NoticeCategory = 1001;
        /// <summary>"%s turned the cloaking device in your city %s."</summary>
        public const int CloakToggled = 1;
        /// <summary>"%s has initiated removal of the HQ in %s! ... in 2 minutes. This will also cause the other houses in the city to be deleted!"</summary>
        public const int HeadquartersRemovalStarted = 7;
    }
}
