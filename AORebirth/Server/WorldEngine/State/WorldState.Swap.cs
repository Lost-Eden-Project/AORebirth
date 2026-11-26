namespace WorldEngine.State
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;

    using AORebirth.World.Contracts.V1;

    using Microsoft.Extensions.Logging;

    /// <summary>
    /// A same-host zone swap, only ever started by an operator through the admin API (World SwapEnabled). A grant lets
    /// exactly one new instance on exactly the named build and GameData register beside the running zone; once it is
    /// ready the old zone is drained, and a cutover hands every playfield and its players to the new one (each player
    /// is saved and zones across: one loading screen, no logout). Rollback is the same hand-over in reverse. A failed
    /// preflight changes nothing.
    /// </summary>
    public sealed partial class WorldState
    {
        /// <summary>The Register capability of a zone build that carries out a HandOver.</summary>
        public const string SwapCapability = "swap/1";

        static readonly Regex SwapInstanceName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

        /// <summary>How long a hand-over may take, at most and by default.</summary>
        const int MaxHandOverSeconds = 1800;

        ZoneSwap? _swap;

        /// <summary>How long a handed-over playfield's spawn state waits for the taking instance to build it.</summary>
        const long SpawnStateLifetimeMs = 5 * 60_000;

        // Spawn state the leaving instance sent with a hand-over's moves, by playfield, until the taking instance builds it.
        readonly Dictionary<int, (PlayfieldSpawnState State, string For, long ExpiresMs)> _handedSpawnState = new();

        public enum SwapPhase
        {
            AwaitingRegistration,
            Ready,
            HandingOff,
            Drained,
            RollingBack,
            RolledBack,
            Completed,
            Aborted
        }

        sealed class ZoneSwap
        {
            public required string Id { get; init; }

            public required string From { get; init; }

            public required string To { get; init; }

            public required string Build { get; init; }

            public required string GameData { get; init; }

            public required string RequestedBy { get; init; }

            public required DateTime StartedUtc { get; init; }

            public required long ExpiresMs { get; init; }

            public SwapPhase Phase { get; set; }

            public string? Note { get; set; }

            /// <summary>The instance players are leaving and the one they go to in the current hand-over.</summary>
            public string HandingFrom { get; set; } = string.Empty;

            public string HandingTo { get; set; } = string.Empty;

            public long HandOverDeadlineMs { get; set; }

            /// <summary>The characters on the leaving instance when the hand-over started, by id, with their names.</summary>
            public Dictionary<int, string> Moving { get; } = new();

            /// <summary>Moving characters whose zone released them without storing their latest changes.</summary>
            public HashSet<int> ReleasedUnsaved { get; } = new();

            /// <summary>
            /// Occupied playfields still assigned to the leaving instance. Each moves to the taking one when the leaving
            /// one starts moving its players (BeginTransfer), so the two never both run it with players in it.
            /// </summary>
            public HashSet<int> Handing { get; } = new();

            /// <summary>Notice of the current state still to be sent to both instances.</summary>
            public bool NoticePending { get; set; }

            /// <summary>Rebuilt from a zone's notice after World restarted: per-player results are unknown.</summary>
            public bool Adopted { get; set; }

            public bool Active => Phase is not (SwapPhase.Completed or SwapPhase.Aborted);
        }

        public readonly record struct SwapReason(string Code, string Detail);

        public sealed record SwapOutcome(bool Accepted, IReadOnlyList<SwapReason> Reasons, object? Swap);

        /// <summary>Hand-over policy for quest dungeon playfields, as the admin API names it.</summary>
        public static bool TryParseDungeons(string? text, out HandOverDungeons dungeons)
        {
            switch ((text ?? "wait").Trim().ToLowerInvariant())
            {
                case "wait": dungeons = HandOverDungeons.Wait; return true;
                case "move": dungeons = HandOverDungeons.Move; return true;
                case "logout": dungeons = HandOverDungeons.Logout; return true;
                default: dungeons = HandOverDungeons.Wait; return false;
            }
        }

        // ------------------------------------------------------------------------------------------ preflight

        /// <summary>Whether <paramref name="from"/> could be swapped now, and why not. Changes nothing.</summary>
        public SwapOutcome SwapPreflight(string? from)
        {
            lock (_sync)
            {
                List<SwapReason> reasons = PreflightLocked(from, Now, out ServiceInstance? instance);
                return new SwapOutcome(reasons.Count == 0, reasons, new
                {
                    viable = reasons.Count == 0,
                    reasons = reasons.Select(r => new { code = r.Code, detail = r.Detail }).ToList(),
                    world = new { epoch = Epoch, recovering = Recovering(Now) },
                    from = instance == null ? null : new
                    {
                        name = instance.Name,
                        build = instance.Build,
                        gameData = instance.GameData,
                        players = instance.Players,
                        playfields = instance.PlayfieldLoads.Count,
                        rssBytes = instance.RssBytes,
                        capabilities = instance.Capabilities.OrderBy(c => c, StringComparer.Ordinal).ToList()
                    },
                    zones = _instances.Values.Count(i => !i.Removed && i.Role == ServiceRole.RoleZone && IsLive(i.Name, Now))
                });
            }
        }

        List<SwapReason> PreflightLocked(string? from, long now, out ServiceInstance? instance)
        {
            var reasons = new List<SwapReason>();
            instance = string.IsNullOrWhiteSpace(from) ? null : Instance(from.Trim());
            if (!_options.SwapEnabled)
                reasons.Add(new("swap_disabled", "World SwapEnabled is off (it also needs AdminPort)"));
            if (Recovering(now))
                reasons.Add(new("world_recovering", "World restarted moments ago and is not yet authoritative"));
            if (_swap is { Active: true } current)
                reasons.Add(new("swap_in_progress", "swap " + current.Id + " is " + Describe(current.Phase)));
            if (!_instances.Values.Any(i => !i.Removed && i.Role == ServiceRole.RoleChat && i.Ready && IsAlive(i, now)))
                reasons.Add(new("chat_not_ready", "ChatEngine is not registered and ready"));

            if (instance == null || instance.Role != ServiceRole.RoleZone)
            {
                reasons.Add(new("from_not_found", "no zone instance named " + (from ?? string.Empty)));
                return reasons;
            }

            if (!IsAlive(instance, now))
                reasons.Add(new("from_not_alive", instance.Name + " is not connected and heartbeating"));
            else if (!instance.Ready)
                reasons.Add(new("from_not_ready", instance.Name + " has not finished starting"));
            if (!instance.Capabilities.Contains(SwapCapability))
                reasons.Add(new("from_lacks_capability", instance.Name + "'s build cannot hand over players (" + SwapCapability
                    + "); this release needs the normal deploy"));
            if (instance.Draining)
                reasons.Add(new("from_draining", instance.Name + " is already out of rotation (" + (instance.DrainedBy ?? "?") + ")"));
            if (instance.ShutdownDueUtc != null)
                reasons.Add(new("from_shutdown_pending", instance.Name + " has a shutdown pending"));

            ServiceInstance self = instance;
            List<string> others = _instances.Values.Where(i => !i.Removed && i.Role == ServiceRole.RoleZone
                && !ReferenceEquals(i, self) && IsLive(i.Name, now)).Select(i => i.Name).ToList();
            if (others.Count > 0)
                reasons.Add(new("other_zone_registered", "only a single-zone world can be swapped; also running: " + string.Join(", ", others)));
            return reasons;
        }

        // ------------------------------------------------------------------------------------------ operations

        /// <summary>
        /// Grants <paramref name="to"/> (a name this World has never seen) the right to register on exactly
        /// <paramref name="build"/> and <paramref name="gameData"/> beside <paramref name="from"/>. Nothing is drained
        /// until it is ready.
        /// </summary>
        public SwapOutcome StartSwap(string? from, string? to, string? build, string? gameData, int expiresSeconds, string requestedBy)
        {
            long now = Now;
            lock (_sync)
            {
                List<SwapReason> reasons = PreflightLocked(from, now, out ServiceInstance? instance);
                string target = (to ?? string.Empty).Trim();
                if (!SwapInstanceName.IsMatch(target))
                    reasons.Add(new("bad_request", "to must be 1-64 letters, digits, '.', '_' or '-'"));
                else if (_slots.ContainsKey(target) || _instances.ContainsKey(target))
                    reasons.Add(new("instance_name_used", target + " was already used in this World; every swap needs a new name"));
                if (string.IsNullOrWhiteSpace(build))
                    reasons.Add(new("bad_request", "build is required (the candidate's zoneBuild)"));
                if (string.IsNullOrWhiteSpace(gameData))
                    reasons.Add(new("bad_request", "gamedata is required (the candidate's GameData fingerprint)"));
                if (expiresSeconds < 30 || expiresSeconds > 3600)
                    reasons.Add(new("bad_request", "expiresSeconds must be between 30 and 3600"));
                if (reasons.Count > 0)
                    return new SwapOutcome(false, reasons, null);

                _swap = new ZoneSwap
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    From = instance!.Name,
                    To = target,
                    Build = build!.Trim(),
                    GameData = gameData!.Trim(),
                    RequestedBy = requestedBy,
                    StartedUtc = DateTime.UtcNow,
                    ExpiresMs = now + expiresSeconds * 1000L,
                    Phase = SwapPhase.AwaitingRegistration,
                    NoticePending = true
                };
                _log.LogWarning("Zone swap {Swap} started by {By}: {From} -> {To} build={Build} gameData={GameData}; waiting for {To} to register",
                    _swap.Id, requestedBy, _swap.From, _swap.To, _swap.Build, Short(_swap.GameData), _swap.To);
                return new SwapOutcome(true, Array.Empty<SwapReason>(), SwapViewLocked(now));
            }
        }

        /// <summary>Moves every playfield and player from the old zone to the new one. Needs the swap to be ready.</summary>
        public SwapOutcome Cutover(int timeoutSeconds, int combatWaitSeconds, HandOverDungeons dungeons, string requestedBy)
        {
            long now = Now;
            lock (_sync)
            {
                if (_swap is not { Active: true } swap)
                    return Refused("no_swap", "no swap is in progress");
                if (swap.Phase != SwapPhase.Ready)
                    return Refused("not_ready", "the swap is " + Describe(swap.Phase) + "; cutover needs it ready");
                if (BadTimes(timeoutSeconds, combatWaitSeconds) is SwapOutcome bad)
                    return bad;
                if (Instance(swap.From) is not ServiceInstance from || !IsRoutable(from, now))
                    return Refused("from_not_alive", swap.From + " is not reachable");
                if (Instance(swap.To) is not ServiceInstance to || !IsRoutable(to, now) || !to.Ready)
                    return Refused("to_not_ready", swap.To + " is not reachable and ready");

                StartHandOverLocked(swap, from, to, timeoutSeconds, combatWaitSeconds, dungeons, now);
                swap.Phase = SwapPhase.HandingOff;
                swap.NoticePending = true;
                _log.LogWarning("Zone swap {Swap} cutover by {By}: {Players} player(s) from {From} to {To}, deadline {Seconds}s",
                    swap.Id, requestedBy, swap.Moving.Count, swap.From, swap.To, timeoutSeconds);
                return new SwapOutcome(true, Array.Empty<SwapReason>(), SwapViewLocked(now));
            }
        }

        /// <summary>Puts the old zone back in rotation and hands everything on the new one back to it.</summary>
        public SwapOutcome Rollback(int timeoutSeconds, int combatWaitSeconds, HandOverDungeons dungeons, bool force, string requestedBy)
        {
            long now = Now;
            lock (_sync)
            {
                if (_swap is not { Active: true } swap)
                    return Refused("no_swap", "no swap is in progress");
                if (swap.Phase is not (SwapPhase.Ready or SwapPhase.HandingOff or SwapPhase.Drained))
                    return Refused("cannot_roll_back", "the swap is " + Describe(swap.Phase));
                if (BadTimes(timeoutSeconds, combatWaitSeconds) is SwapOutcome bad)
                    return bad;
                if (Instance(swap.From) is not ServiceInstance from || !IsRoutable(from, now) || !from.Ready)
                    return Refused("from_not_alive", swap.From + " is not reachable and ready; nothing to roll back to");
                // Items, quests and missions handed out on the new GameData may mean nothing to the old one.
                if (!force && swap.Phase != SwapPhase.Ready && Differs(from.GameData, swap.GameData))
                    return Refused("content_changed", "the new instance runs other GameData; players may already hold what the old "
                        + "one does not know. Pass force=true to roll back anyway");

                if (swap.Phase == SwapPhase.HandingOff)
                {
                    CancelHandOverLocked(swap, Instance(swap.HandingFrom));
                    // Never handed: those playfields simply stay where their players are.
                    swap.Handing.Clear();
                }
                from.Draining = false;
                from.DrainedBy = null;
                if (Instance(swap.To) is ServiceInstance to && IsRoutable(to, now))
                {
                    to.Draining = true;
                    to.DrainedBy = "swap:" + swap.Id;
                    StartHandOverLocked(swap, to, from, timeoutSeconds, combatWaitSeconds, dungeons, now);
                    swap.Phase = SwapPhase.RollingBack;
                }
                else
                {
                    swap.Phase = SwapPhase.RolledBack;
                }

                AssignDuty();
                swap.NoticePending = true;
                _log.LogWarning("Zone swap {Swap} rolled back by {By}: {From} back in rotation, {Players} player(s) to hand back",
                    swap.Id, requestedBy, swap.From, swap.Moving.Count);
                return new SwapOutcome(true, Array.Empty<SwapReason>(), SwapViewLocked(now));
            }
        }

        /// <summary>Stops the swap where it is: no further hand-overs; both instances keep what they hold.</summary>
        public SwapOutcome AbortSwap(string requestedBy)
        {
            long now = Now;
            lock (_sync)
            {
                if (_swap is not { Active: true } swap)
                    return Refused("no_swap", "no swap is in progress");
                if (swap.Phase is SwapPhase.HandingOff or SwapPhase.RollingBack)
                {
                    CancelHandOverLocked(swap, Instance(swap.HandingFrom));
                    swap.Handing.Clear();
                }
                else if (swap.Phase is SwapPhase.AwaitingRegistration or SwapPhase.Ready)
                {
                    RevertBeforeCutover(swap);
                }

                FinishLocked(swap, SwapPhase.Aborted, "aborted by " + requestedBy);
                return new SwapOutcome(true, Array.Empty<SwapReason>(), SwapViewLocked(now));
            }
        }

        /// <summary>Once the leaving instance holds nobody, asks it to shut down and closes the swap.</summary>
        public SwapOutcome CompleteSwap(int seconds, string requestedBy)
        {
            long now = Now;
            lock (_sync)
            {
                if (_swap is not { Active: true } swap)
                    return Refused("no_swap", "no swap is in progress");
                if (swap.Phase is not (SwapPhase.Drained or SwapPhase.RolledBack))
                    return Refused("not_drained", "the swap is " + Describe(swap.Phase) + "; complete needs it drained or rolled back");
                if (seconds < 1 || seconds > 600)
                    return Refused("bad_request", "seconds must be between 1 and 600");
                string leaving = swap.Phase == SwapPhase.Drained ? swap.From : swap.To;
                int held = _leases.Values.Count(l => string.Equals(l.Owner, leaving, StringComparison.OrdinalIgnoreCase));
                if (held > 0)
                    return Refused("players_remaining", leaving + " still holds " + held.ToString(CultureInfo.InvariantCulture) + " character(s)");
                if (Instance(leaving) is ServiceInstance instance && instance.Connected)
                    RequestShutdown(leaving, seconds, "Zone update finished.", "swap:" + swap.Id);
                FinishLocked(swap, SwapPhase.Completed, leaving + " asked to shut down by " + requestedBy);
                return new SwapOutcome(true, Array.Empty<SwapReason>(), SwapViewLocked(now));
            }
        }

        /// <summary>The current (or last) swap for the admin API; null when there never was one.</summary>
        public object? SwapView()
        {
            lock (_sync)
                return SwapViewLocked(Now);
        }

        // ------------------------------------------------------------------------------------------ hooks

        /// <summary>
        /// The one exception to the build and GameData check (<see cref="CheckZoneRegistration"/>): true when this fresh
        /// registration is the granted swap target. Throws for a registration a swap in progress forbids.
        /// </summary>
        bool SwapGrantsRegistration(string name, Register message, List<ServiceInstance> live)
        {
            if (_swap is not { Active: true } swap || message.LastEpoch != 0)
                return false;
            if (string.Equals(name, swap.To, StringComparison.OrdinalIgnoreCase))
            {
                if (swap.Phase != SwapPhase.AwaitingRegistration)
                    throw new ZoneMismatchException("swap_grant_used: the grant for " + name + " was already used");
                if (!string.Equals(message.Build, swap.Build, StringComparison.Ordinal)
                    || !string.Equals(message.GameData, swap.GameData, StringComparison.Ordinal))
                    throw new ZoneMismatchException("swap_grant_mismatch: " + name + " runs build " + message.Build + " GameData "
                        + Short(message.GameData) + " but swap " + swap.Id + " granted build " + swap.Build + " GameData " + Short(swap.GameData));
                if (live.Any(i => !string.Equals(i.Name, swap.From, StringComparison.OrdinalIgnoreCase)))
                    throw new ZoneMismatchException("swap_grant: another zone than " + swap.From + " is running");
                _log.LogWarning("Zone swap {Swap}: {To} registers on its granted build {Build}", swap.Id, name, message.Build);
                return true;
            }

            if (!string.Equals(name, swap.From, StringComparison.OrdinalIgnoreCase))
                throw new ZoneMismatchException("swap_in_progress: swap " + swap.Id + " allows no other zone to start");
            return false;
        }

        /// <summary>The granted instance just reported ready: drain the old one, which moves the duty too.</summary>
        void SwapInstanceReady(ServiceInstance instance)
        {
            if (_swap is not { Phase: SwapPhase.AwaitingRegistration } swap || !instance.Ready
                || !string.Equals(instance.Name, swap.To, StringComparison.OrdinalIgnoreCase))
                return;
            if (Instance(swap.From) is ServiceInstance from)
            {
                from.Draining = true;
                from.DrainedBy = "swap:" + swap.Id;
            }

            swap.Phase = SwapPhase.Ready;
            swap.NoticePending = true;
            _log.LogWarning("Zone swap {Swap}: {To} is ready; {From} drained, waiting for cutover", swap.Id, swap.To, swap.From);
            AssignDuty();
        }

        /// <summary>Called from Sweep: readiness, expiry, lost instances and finished hand-overs; then the notices.</summary>
        void SweepSwap(long now)
        {
            ExpireHandedSpawnState(now);
            if (_swap is not ZoneSwap swap)
                return;
            if (swap.Active)
                AdvanceSwap(swap, now);
            SendSwapNotices(swap);
        }

        void AdvanceSwap(ZoneSwap swap, long now)
        {
            ServiceInstance? from = Instance(swap.From);
            ServiceInstance? to = Instance(swap.To);
            switch (swap.Phase)
            {
                case SwapPhase.AwaitingRegistration:
                    if (to != null && to.Ready && IsAlive(to, now))
                        SwapInstanceReady(to);
                    else if (now >= swap.ExpiresMs)
                    {
                        RevertBeforeCutover(swap);
                        FinishLocked(swap, SwapPhase.Aborted, swap.To + " did not register and become ready in time"
                            + (to != null ? "; it is out of rotation, stop it" : string.Empty));
                    }

                    break;
                case SwapPhase.Ready:
                    if (to == null)
                    {
                        // Nothing was handed over yet: the old zone goes back in rotation, as before the swap.
                        RevertBeforeCutover(swap);
                        FinishLocked(swap, SwapPhase.Aborted, swap.To + " went away before cutover; " + swap.From + " is back in rotation");
                    }

                    break;
                case SwapPhase.HandingOff:
                    if (to == null)
                    {
                        // The playfields not yet handed stay with the instance their players are on.
                        swap.Handing.Clear();
                        FinishLocked(swap, SwapPhase.Aborted, swap.To + " went away during the hand-over; roll back or resume " + swap.From);
                    }
                    else if (from == null || !HoldsAnyone(swap.From))
                    {
                        HandRemainingPlayfields(swap, now);
                        swap.Phase = SwapPhase.Drained;
                        swap.NoticePending = true;
                        _log.LogWarning("Zone swap {Swap}: {From} holds nobody any more; complete the swap to stop it", swap.Id, swap.From);
                    }
                    else if (now > swap.HandOverDeadlineMs + 10_000)
                    {
                        // Past the deadline the leaving instance has logged out whoever it could not move.
                        HandRemainingPlayfields(swap, now);
                    }

                    break;
                case SwapPhase.RollingBack:
                    if (from == null)
                    {
                        swap.Handing.Clear();
                        FinishLocked(swap, SwapPhase.Aborted, swap.From + " went away during the rollback");
                    }
                    else if (to == null || !HoldsAnyone(swap.To))
                    {
                        HandRemainingPlayfields(swap, now);
                        swap.Phase = SwapPhase.RolledBack;
                        swap.NoticePending = true;
                        _log.LogWarning("Zone swap {Swap}: rolled back; {To} holds nobody any more", swap.Id, swap.To);
                    }
                    else if (now > swap.HandOverDeadlineMs + 10_000)
                    {
                        HandRemainingPlayfields(swap, now);
                    }

                    break;
            }
        }

        /// <summary>
        /// Tells both instances what the swap is now. They give it back when they register, so a World that restarts in
        /// the middle of a swap takes it up again instead of serving two builds unnoticed.
        /// </summary>
        void SendSwapNotices(ZoneSwap swap)
        {
            if (!swap.NoticePending)
                return;
            swap.NoticePending = false;
            var notice = new SwapNotice
            {
                SwapId = swap.Id,
                From = swap.From,
                To = swap.To,
                Build = swap.Build,
                GameData = swap.GameData,
                Phase = Describe(swap.Phase)
            };
            foreach (string name in new[] { swap.From, swap.To })
                Instance(name)?.Send(new WorldToService { Swap = notice.Clone() });
        }

        /// <summary>
        /// A zone re-registering after a World restart says it is part of a swap World no longer knows: take it up
        /// again in that phase (per-player results are gone), with the drains it implies.
        /// </summary>
        void AdoptSwap(SwapNotice? notice, long now)
        {
            if (notice == null || string.IsNullOrEmpty(notice.SwapId) || (_swap is { Active: true }))
                return;
            if (_swap != null && string.Equals(_swap.Id, notice.SwapId, StringComparison.Ordinal))
                return;
            SwapPhase? phase = notice.Phase switch
            {
                "awaiting_registration" => SwapPhase.AwaitingRegistration,
                "ready" => SwapPhase.Ready,
                "handing_off" => SwapPhase.HandingOff,
                "drained" => SwapPhase.Drained,
                "rolling_back" => SwapPhase.RollingBack,
                "rolled_back" => SwapPhase.RolledBack,
                _ => null
            };
            if (phase == null)
                return;
            _swap = new ZoneSwap
            {
                Id = notice.SwapId,
                From = notice.From,
                To = notice.To,
                Build = notice.Build,
                GameData = notice.GameData,
                RequestedBy = "adopted after World restart",
                StartedUtc = DateTime.UtcNow,
                ExpiresMs = now + 600_000,
                Phase = phase.Value,
                Adopted = true,
                HandingFrom = phase is SwapPhase.RollingBack or SwapPhase.RolledBack ? notice.To : notice.From,
                HandingTo = phase is SwapPhase.RollingBack or SwapPhase.RolledBack ? notice.From : notice.To,
                HandOverDeadlineMs = now
            };
            _log.LogWarning("Zone swap {Swap} taken up again after World restart: {From} -> {To}, {Phase}", notice.SwapId, notice.From,
                notice.To, notice.Phase);
        }

        /// <summary>The drain an adopted swap's phase implies, for each instance as it re-registers.</summary>
        void ApplySwapDrain(ServiceInstance instance)
        {
            if (_swap is not { Active: true, Adopted: true } swap)
                return;
            bool from = string.Equals(instance.Name, swap.From, StringComparison.OrdinalIgnoreCase);
            bool to = string.Equals(instance.Name, swap.To, StringComparison.OrdinalIgnoreCase);
            bool drain = (from && swap.Phase is SwapPhase.Ready or SwapPhase.HandingOff or SwapPhase.Drained)
                || (to && swap.Phase is SwapPhase.AwaitingRegistration or SwapPhase.RollingBack or SwapPhase.RolledBack);
            if (drain && (from || to))
            {
                instance.Draining = true;
                instance.DrainedBy = "swap:" + swap.Id;
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        bool HoldsAnyone(string instance)
            => _leases.Values.Any(l => string.Equals(l.Owner, instance, StringComparison.OrdinalIgnoreCase));

        /// <summary>Gives every playfield of <paramref name="leaving"/> to <paramref name="taking"/> and tells the leaving one to move its players.</summary>
        void StartHandOverLocked(ZoneSwap swap, ServiceInstance leaving, ServiceInstance taking, int timeoutSeconds,
            int combatWaitSeconds, HandOverDungeons dungeons, long now)
        {
            swap.HandingFrom = leaving.Name;
            swap.HandingTo = taking.Name;
            swap.HandOverDeadlineMs = now + timeoutSeconds * 1000L;
            swap.Moving.Clear();
            foreach (CharacterLease lease in _leases.Values.Where(l => string.Equals(l.Owner, leaving.Name, StringComparison.OrdinalIgnoreCase)))
                swap.Moving[lease.CharacterId] = lease.Name;

            var playfields = new HashSet<int>(leaving.PlayfieldLoads.Keys);
            foreach (PlayfieldAssignment assignment in _playfields.Values.Where(p => string.Equals(p.Instance, leaving.Name,
                StringComparison.OrdinalIgnoreCase)))
                playfields.Add(assignment.PlayfieldId);

            // An empty playfield goes over now. An occupied one stays where its players are until the leaving instance
            // starts moving them (BeginTransfer): otherwise both would run it with players in it, each with its own NPCs
            // and loot, for the length of the hand-over.
            swap.Handing.Clear();
            foreach (int playfield in playfields)
            {
                bool occupied = leaving.PlayfieldLoads.TryGetValue(playfield, out int players) && players > 0;
                if (occupied)
                {
                    swap.Handing.Add(playfield);
                    if (!_playfields.TryGetValue(playfield, out PlayfieldAssignment? current)
                        || !string.Equals(current.Instance, leaving.Name, StringComparison.OrdinalIgnoreCase))
                        _playfields[playfield] = new PlayfieldAssignment(playfield, leaving.Name, ++_assignment, now);
                }
                else
                {
                    _playfields[playfield] = new PlayfieldAssignment(playfield, taking.Name, ++_assignment, now);
                }
            }

            var handOver = new HandOver
            {
                SwapId = swap.Id,
                TargetInstance = taking.Name,
                DeadlineSeconds = timeoutSeconds,
                CombatWaitSeconds = combatWaitSeconds,
                Dungeons = dungeons
            };
            handOver.PlayfieldIds.AddRange(playfields.OrderBy(p => p));
            leaving.Send(new WorldToService { HandOver = handOver });
            swap.NoticePending = true;
        }

        /// <summary>
        /// BeginTransfer from the leaving instance into a playfield it is handing over: the playfield moves to the taking
        /// instance now, as its players start to leave. Everyone resolving it from here on is sent there.
        /// </summary>
        void HandPlayfieldOnTransfer(ServiceInstance instance, int playfieldId, long now)
        {
            if (_swap is not { Active: true } swap || swap.Phase is not (SwapPhase.HandingOff or SwapPhase.RollingBack)
                || !string.Equals(swap.HandingFrom, instance.Name, StringComparison.OrdinalIgnoreCase))
                return;
            // A swap taken up after a World restart lost the list: every move out of the leaving instance hands its playfield.
            if (!swap.Handing.Remove(playfieldId) && !swap.Adopted)
                return;
            _playfields[playfieldId] = new PlayfieldAssignment(playfieldId, swap.HandingTo, ++_assignment, now);
        }

        /// <summary>
        /// Keeps the spawn state a hand-over move carried (only from the leaving instance, only for the playfield being
        /// moved into) for the taking instance, which gets it when it resolves that playfield to build it.
        /// </summary>
        void KeepHandedSpawnState(ServiceInstance instance, TransferRequest request, long now)
        {
            PlayfieldSpawnState? state = request.SpawnState;
            if (state == null || _swap is not { Active: true } swap
                || swap.Phase is not (SwapPhase.HandingOff or SwapPhase.RollingBack)
                || !string.Equals(swap.HandingFrom, instance.Name, StringComparison.OrdinalIgnoreCase)
                || state.PlayfieldId != request.Envelope.DestinationPlayfield)
                return;
            if (!_handedSpawnState.ContainsKey(state.PlayfieldId))
                _handedSpawnState[state.PlayfieldId] = (state, swap.HandingTo, now + SpawnStateLifetimeMs);
        }

        /// <summary>The kept spawn state of <paramref name="playfieldId"/>, once, when the instance it was kept for asks.</summary>
        PlayfieldSpawnState? TakeHandedSpawnState(int playfieldId, string? asking)
        {
            if (string.IsNullOrEmpty(asking) || !_handedSpawnState.TryGetValue(playfieldId, out var kept)
                || !string.Equals(kept.For, asking, StringComparison.OrdinalIgnoreCase))
                return null;
            _handedSpawnState.Remove(playfieldId);
            return kept.State;
        }

        void ExpireHandedSpawnState(long now)
        {
            foreach (int playfield in _handedSpawnState.Where(p => now >= p.Value.ExpiresMs).Select(p => p.Key).ToList())
                _handedSpawnState.Remove(playfield);
        }

        /// <summary>The hand-over ended: whatever is still assigned to the leaving instance goes over too.</summary>
        void HandRemainingPlayfields(ZoneSwap swap, long now)
        {
            foreach (int playfield in swap.Handing)
                if (_playfields.TryGetValue(playfield, out PlayfieldAssignment? current)
                    && string.Equals(current.Instance, swap.HandingFrom, StringComparison.OrdinalIgnoreCase))
                    _playfields[playfield] = new PlayfieldAssignment(playfield, swap.HandingTo, ++_assignment, now);
            swap.Handing.Clear();
        }

        /// <summary>
        /// A swap that ends before cutover leaves things as they were: the old instance back in rotation, the new one out
        /// of it (it stays registered until an operator stops it), so a world never serves two builds unnoticed.
        /// </summary>
        void RevertBeforeCutover(ZoneSwap swap)
        {
            if (Instance(swap.From) is ServiceInstance from && from.DrainedBy == "swap:" + swap.Id)
            {
                from.Draining = false;
                from.DrainedBy = null;
            }

            if (Instance(swap.To) is ServiceInstance to)
            {
                to.Draining = true;
                to.DrainedBy = "swap:" + swap.Id;
            }

            AssignDuty();
        }

        /// <summary>A zone released a character without storing its latest changes; a swap moving it reports that.</summary>
        void NoteUnsavedReleaseLocked(ServiceInstance instance, int characterId)
        {
            _log.LogWarning("Character {Character} released by {Instance} without its latest changes stored", characterId, instance.Name);
            if (_swap is { } swap && swap.Moving.ContainsKey(characterId))
                swap.ReleasedUnsaved.Add(characterId);
        }

        static void CancelHandOverLocked(ZoneSwap swap, ServiceInstance? handing)
            => handing?.Send(new WorldToService { HandOver = new HandOver { SwapId = swap.Id, Cancel = true } });

        void FinishLocked(ZoneSwap swap, SwapPhase phase, string note)
        {
            swap.Phase = phase;
            swap.Note = note;
            swap.NoticePending = true;
            _log.LogWarning("Zone swap {Swap} {Phase}: {Note}", swap.Id, Describe(phase), note);
        }

        static SwapOutcome? BadTimes(int timeoutSeconds, int combatWaitSeconds)
        {
            if (timeoutSeconds < 10 || timeoutSeconds > MaxHandOverSeconds)
                return Refused("bad_request", "timeoutSeconds must be between 10 and " + MaxHandOverSeconds.ToString(CultureInfo.InvariantCulture));
            if (combatWaitSeconds < 0 || combatWaitSeconds >= timeoutSeconds)
                return Refused("bad_request", "combatWaitSeconds must be at least 0 and below timeoutSeconds");
            return null;
        }

        static SwapOutcome Refused(string code, string detail) => new(false, new[] { new SwapReason(code, detail) }, null);

        static string Describe(SwapPhase phase) => phase switch
        {
            SwapPhase.AwaitingRegistration => "awaiting_registration",
            SwapPhase.Ready => "ready",
            SwapPhase.HandingOff => "handing_off",
            SwapPhase.Drained => "drained",
            SwapPhase.RollingBack => "rolling_back",
            SwapPhase.RolledBack => "rolled_back",
            SwapPhase.Completed => "completed",
            _ => "aborted"
        };

        object? SwapViewLocked(long now)
        {
            if (_swap is not ZoneSwap swap)
                return null;
            int moved = 0, loggedOut = 0, loggedOutUnsaved = 0, pending = 0, failed = 0;
            var results = new List<object>(swap.Moving.Count);
            bool overdue = swap.HandOverDeadlineMs > 0 && now > swap.HandOverDeadlineMs + 15_000;
            foreach ((int character, string name) in swap.Moving.OrderBy(m => m.Key))
            {
                string outcome;
                string? reason = null;
                if (!_leases.TryGetValue(character, out CharacterLease? lease))
                {
                    loggedOut++;
                    if (swap.ReleasedUnsaved.Contains(character))
                    {
                        outcome = "logged_out_unsaved";
                        reason = "its zone released it without storing its latest changes (a save failed)";
                        loggedOutUnsaved++;
                    }
                    else
                    {
                        outcome = "logged_out_saved";
                    }
                }
                else if (string.Equals(lease.Owner, swap.HandingTo, StringComparison.OrdinalIgnoreCase) && lease.State == LeaseState.InPlay)
                {
                    outcome = "moved";
                    moved++;
                }
                else if (overdue)
                {
                    outcome = "failed";
                    reason = "still on " + lease.Owner + " (" + lease.State + ") after the deadline";
                    failed++;
                }
                else
                {
                    outcome = "pending";
                    pending++;
                }

                results.Add(new { character, name, outcome, reason });
            }

            int On(string instance) => _leases.Values.Count(l => string.Equals(l.Owner, instance, StringComparison.OrdinalIgnoreCase));
            return new
            {
                swapId = swap.Id,
                phase = Describe(swap.Phase),
                from = swap.From,
                to = swap.To,
                build = swap.Build,
                gameData = swap.GameData,
                requestedBy = swap.RequestedBy,
                startedUtc = swap.StartedUtc,
                note = swap.Note,
                handingFrom = swap.HandingFrom.Length == 0 ? null : swap.HandingFrom,
                handingTo = swap.HandingTo.Length == 0 ? null : swap.HandingTo,
                deadlineInSeconds = swap.HandOverDeadlineMs == 0 ? (long?)null : Math.Max(0, (swap.HandOverDeadlineMs - now) / 1000),
                players = new { onFrom = On(swap.From), onTo = On(swap.To), moved, loggedOut, loggedOutUnsaved, failed, pending },
                results
            };
        }
    }
}
