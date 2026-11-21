namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    using PlayfieldInstance = ZoneEngine_New.Core.Playfield.Playfield;

    /// <summary>
    /// Carries out World's <see cref="HandOver"/> (an operator's zone swap), one playfield at a time: when nobody on a
    /// playfield is in combat (or the combat wait is over), all of its players start their cross-instance moves on that
    /// playfield's same tick (saved first, one loading screen). World hands the playfield to the new instance as the
    /// first move begins, so the two instances never both run it with players in it. Whoever cannot be moved then (dead,
    /// link-dead, a move that does not start, or still here a moment later) is saved and logged out; logging in again
    /// lands on the new instance. Quest dungeons follow the dungeon policy. At the deadline everyone left is logged out.
    /// </summary>
    public sealed class ZoneHandOver
    {
        static readonly TimeSpan Pass = TimeSpan.FromSeconds(1);

        const string Moving = "Server update: reloading the zone.";

        const string LoggedOut = "Server update: you were logged out. You can log in again right away.";

        /// <summary>How long a playfield's moves may take once started before its stragglers are logged out.</summary>
        static readonly TimeSpan MoveGrace = TimeSpan.FromSeconds(15);

        readonly Lazy<PlayfieldManager> _playfields;
        readonly IZoneLogger _logger;
        readonly object _gate = new();
        CancellationTokenSource? _run;

        public ZoneHandOver(Lazy<PlayfieldManager> playfields, IZoneLogger logger)
        {
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Apply(HandOver handOver)
        {
            ArgumentNullException.ThrowIfNull(handOver);
            lock (_gate)
            {
                _run?.Cancel();
                _run = null;
                if (handOver.Cancel)
                {
                    _logger.Warn("Zone swap " + handOver.SwapId + ": hand-over cancelled; players not yet moved stay here");
                    return;
                }

                var run = new CancellationTokenSource();
                _run = run;
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Zone swap {0}: handing {1} playfield(s) to {2}, deadline {3}s, combat wait {4}s, dungeons {5}",
                    handOver.SwapId, handOver.PlayfieldIds.Count, handOver.TargetInstance, handOver.DeadlineSeconds,
                    handOver.CombatWaitSeconds, handOver.Dungeons));
                _ = Task.Run(() => RunAsync(handOver, run.Token));
            }
        }

        async Task RunAsync(HandOver handOver, CancellationToken stop)
        {
            var playfields = new HashSet<int>(handOver.PlayfieldIds);
            DateTime started = DateTime.UtcNow;
            int seconds = Math.Max(10, handOver.DeadlineSeconds);
            DateTime deadline = started.AddSeconds(seconds);
            DateTime combatUntil = started.AddSeconds(Math.Max(0, handOver.CombatWaitSeconds));
            // Dungeon players are left alone until close to the deadline, so most finish or leave on their own.
            DateTime dungeonsMoveAt = deadline - TimeSpan.FromSeconds(Math.Min(30, seconds / 2));
            // Playfields whose moves have started, and when: anyone still on one after the grace is logged out.
            var moved = new Dictionary<int, DateTime>();
            var told = new HashSet<int>();
            var loggedOut = new ConcurrentDictionary<int, byte>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    DateTime now = DateTime.UtcNow;
                    bool final = now >= deadline;
                    PlayfieldManager manager = _playfields.Value;
                    var groups = manager.SnapshotPlayers()
                        .Where(p => p.Playfield is PlayfieldInstance pf && playfields.Contains(pf.Identity.Instance))
                        .GroupBy(p => (PlayfieldInstance)p.Playfield!)
                        .ToList();
                    if (groups.Count == 0)
                    {
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Zone swap {0}: hand-over to {1} finished in {2:F0}s ({3} saved and logged out)", handOver.SwapId,
                            handOver.TargetInstance, (DateTime.UtcNow - started).TotalSeconds, loggedOut.Count));
                        return;
                    }

                    foreach (IGrouping<PlayfieldInstance, Player> group in groups)
                    {
                        PlayfieldInstance playfield = group.Key;
                        int id = playfield.Identity.Instance;
                        List<Player> players = group.ToList();
                        foreach (Player player in players)
                            if (told.Add(player.Identity.Instance))
                                Dispatch(playfield, player, stop, () => Tell(player, Moving));

                        if (final || (moved.TryGetValue(id, out DateTime movedAt) && now - movedAt > MoveGrace))
                        {
                            foreach (Player player in players)
                                Dispatch(playfield, player, stop, () => LogOut(playfield, player, loggedOut, LoggedOut));
                            continue;
                        }

                        if (moved.ContainsKey(id))
                            continue;
                        bool dungeon = ZoneEngine_New.Core.Quests.Dungeons.QuestDungeonIds.IsDungeonPlayfield(id);
                        if (dungeon && handOver.Dungeons == HandOverDungeons.Wait && now < dungeonsMoveAt)
                            continue;
                        // One move for the whole playfield: nobody stays behind fighting what the others left.
                        if (!dungeon && now < combatUntil && players.Any(p => p.IsInCombat))
                            continue;

                        moved[id] = now;
                        bool logout = dungeon && handOver.Dungeons == HandOverDungeons.Logout;
                        // Every player here runs on this playfield's tick: one dispatch moves them all in the same tick.
                        playfield.Dispatch(() =>
                        {
                            if (stop.IsCancellationRequested)
                                return;
                            foreach (Player player in players)
                            {
                                if (!ReferenceEquals(player.Playfield, playfield))
                                    continue;
                                if (logout)
                                    LogOut(playfield, player, loggedOut, LoggedOut);
                                else
                                    Move(manager, playfield, player, dungeon, loggedOut);
                            }
                        });
                    }

                    if (final)
                    {
                        await Task.Delay(Pass * 3, stop).ConfigureAwait(false);
                        int left = manager.SnapshotPlayers().Count(p => p.Playfield is PlayfieldInstance pf && playfields.Contains(pf.Identity.Instance));
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Zone swap {0}: hand-over to {1} ended at the deadline ({2} saved and logged out, {3} still here)",
                            handOver.SwapId, handOver.TargetInstance, loggedOut.Count, left));
                        return;
                    }

                    await Task.Delay(Pass, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Zone swap " + handOver.SwapId + ": hand-over failed");
            }
        }

        /// <summary>On the playfield tick: moves one player where they stand, or logs them out when that is not possible.</summary>
        void Move(PlayfieldManager manager, PlayfieldInstance playfield, Player player, bool dungeon, ConcurrentDictionary<int, byte> loggedOut)
        {
            if (player.Session is not IZoneSession session || player.ConnectionPhase == PlayerConnectionPhase.LinkDead || player.IsDead)
            {
                // No client to redirect, or nothing to stand on: saved; logging in again lands on the new instance.
                LogOut(playfield, player, loggedOut, LoggedOut);
                return;
            }

            if (session.IsTransferring)
                return;
            CrossInstanceTransfer? transfer = manager.CrossInstance;
            var landing = new AORebirth.Core.Vector.Vector3(player.Position.x, player.Position.y, player.Position.z);
            if (transfer == null || !transfer.Start(player, playfield.Identity.Instance, landing, player.Rotation,
                dungeon ? ArrivalKind.ArrivalDungeon : ArrivalKind.ArrivalZone))
                LogOut(playfield, player, loggedOut, LoggedOut);
        }

        static void LogOut(PlayfieldInstance playfield, Player player, ConcurrentDictionary<int, byte> loggedOut, string message)
        {
            if (!ReferenceEquals(player.Playfield, playfield))
                return;
            if (player.Session is IZoneSession { IsTransferring: true })
                return;
            Tell(player, message);
            playfield.GetRequiredService<SpawnService>().LogoutPlayer(player);
            loggedOut.TryAdd(player.Identity.Instance, 0);
        }

        static void Dispatch(PlayfieldInstance playfield, Player player, CancellationToken stop, Action action)
            => playfield.DispatchPlayerProjection(player, () =>
            {
                if (!stop.IsCancellationRequested && ReferenceEquals(player.Playfield, playfield))
                    action();
            });

        static void Tell(Player player, string text)
            => player.Session?.Send(new LostEden.Protocol.Messages.N3Messages.ChatTextMessage
            {
                Identity = player.Identity,
                Text = text,
                Unknown1 = 0,
                Unknown2 = 0
            });
    }
}
