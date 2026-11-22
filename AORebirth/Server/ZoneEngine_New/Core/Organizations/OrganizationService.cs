namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;

    using ZoneEngine_New.Core.Chat;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.Teams;
    using ZoneEngine_New.Core.World;

    /// <summary>
    /// Organizations for the players on this zone instance.
    /// <para>
    /// The database (<see cref="IOrganizationStore"/>) is the only authority. A command runs off the tick as one
    /// transaction holding the organization's row lock, re-checking membership and ranks there, so commands from any
    /// number of zone instances never race. Each instance caches the organizations of its players by
    /// <see cref="OrgState.Version"/>; after a change the acting instance tells the others through World
    /// (<see cref="ChangedCommand"/>), each reloads the organization and brings its own players up to date on their own
    /// ticks (the Clan and ClanLevel stats, the name others see). A minute's version check repairs anything missed.
    /// </para>
    /// <para>
    /// Chat: the acting instance moves characters between organization channels and writes the Org Msg lines, in that
    /// order on the World stream. The Clan and ClanLevel stats are a projection of org_members, never read back.
    /// </para>
    /// </summary>
    public sealed partial class OrganizationService : IDisposable
    {
        /// <summary>World command to every other instance: an organization changed (id, flags, then notices).</summary>
        public const string ChangedCommand = "org.changed";

        static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
        static readonly TimeSpan VoteSweepInterval = TimeSpan.FromSeconds(15);

        readonly IOrganizationStore _store;
        readonly IGameData _gameData;
        readonly Lazy<PlayfieldManager> _playfields;
        readonly IChatEngineLink _chat;
        readonly WorldCommands? _commands;
        readonly WorldLink? _world;
        readonly IZoneLogger _logger;

        readonly ConcurrentDictionary<int, OrgState> _cache = new();
        // One command at a time per character: a client spamming /org commands queues no database work.
        readonly ConcurrentDictionary<int, byte> _busy = new();
        readonly ConcurrentDictionary<int, Invitation> _invitations = new();
        readonly ConcurrentDictionary<int, PendingTransfer> _transfers = new();
        readonly Dictionary<int, (Player Player, Action<CharacterStat, int, int, bool> Observer)> _sideObservers = new();
        Timer? _syncTimer;
        Timer? _voteTimer;
        int _syncing;
        int _sweeping;
        int _started;
        volatile bool _disposed;

        public OrganizationService(IOrganizationStore store, IGameData gameData, Lazy<PlayfieldManager> playfields,
            IChatEngineLink chat, IZoneLogger logger, WorldCommands? commands = null, WorldLink? world = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _commands = commands;
            _world = world;
        }

        OrganizationRules Rules => _gameData.Organizations;

        sealed record Invitation(int OrganizationId, int InviterId, int Side, DateTime ExpiresAtUtc);

        sealed record PendingTransfer(int OrganizationId, int TargetId, DateTime ExpiresAtUtc);

        /// <summary>What a command did, delivered once it has committed.</summary>
        sealed class Outcome
        {
            public int OrganizationId;
            /// <summary>The organization before and after the change; both null when nothing changed.</summary>
            public OrgState? Previous;
            public OrgState? State;
            /// <summary>Sent to the actor on its tick, after its own membership is brought up to date.</summary>
            public readonly List<MessageBody> Actor = [];
            /// <summary>Run on the actor's tick before <see cref="Actor"/> (credits paid out of the bank).</summary>
            public Action<Player>? OnActorTick;
            /// <summary>Lines for one member each, delivered by whichever instance has it.</summary>
            public readonly List<(int CharacterId, string Text)> Notices = [];
            /// <summary>Org Msg channel lines.</summary>
            public readonly List<string> OrgMessages = [];
            /// <summary>Org Msg lines go out before members leave the channel (a disband), otherwise after joins and leaves.</summary>
            public bool OrgMessagesFirst;
            /// <summary>Every online member gets "Organization info updated."</summary>
            public bool InfoUpdated;
        }

        // ------------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Hooks player registration, other instances' changes and the timers. Call once PlayfieldManager exists.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            _playfields.Value.PlayerRegistered += OnPlayerRegistered;
            _commands?.Register(ChangedCommand, OnChangedElsewhere);
            _syncTimer = new Timer(_ => Sync(), null, SyncInterval, SyncInterval);
            _voteTimer = new Timer(_ => SweepVotes(), null, VoteSweepInterval, VoteSweepInterval);
        }

        public void Dispose()
        {
            _disposed = true;
            _syncTimer?.Dispose();
            _voteTimer?.Dispose();
            if (_started != 0)
                _playfields.Value.PlayerRegistered -= OnPlayerRegistered;
        }

        /// <summary>
        /// Login: the membership read with the character, applied before its first FullCharacter and spawn. Runs on the
        /// hydration thread; the player is not in play yet.
        /// </summary>
        public static void ApplyHydratedMembership(Player player, OrgMembershipRow? membership, OrganizationRules rules)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (membership == null || !rules.Enabled)
            {
                player.Organization = null;
                player.Stats.Set(CharacterStat.Clan, 0, StatDetail.Base);
                return;
            }

            int rank = rules.EffectiveRank(membership.GoverningForm, membership.StoredRank,
                membership.LeaderId == player.Identity.Instance);
            player.Organization = new PlayerOrganization(membership.OrganizationId, membership.Name,
                rules.FormOf(membership.GoverningForm).RankName(rank));
            player.Stats.Set(CharacterStat.Clan, membership.OrganizationId, StatDetail.Base);
            player.Stats.Set(CharacterStat.ClanLevel, rank, StatDetail.Base);
        }

        // A player is now this instance's: read its membership again (anything that changed between the login read and
        // now reached no instance that held it) and make sure its organization is cached here.
        void OnPlayerRegistered(Player player)
        {
            if (_disposed || !Rules.Enabled)
                return;
            WatchSide(player);
            _ = Task.Run(() =>
            {
                try
                {
                    OrgMembershipRow? membership = _store.LoadMembership(player.Identity.Instance);
                    int previous = player.Organization?.Id ?? 0;
                    if (membership != null)
                    {
                        OrgState? state = _cache.TryGetValue(membership.OrganizationId, out OrgState? cached) && !cached.Disbanded
                            ? cached
                            : Refresh(membership.OrganizationId);
                        if (state != null)
                            Dispatch(player, () => ReconcileOnTick(player, state.Id));
                    }

                    if (previous > 0 && previous != membership?.OrganizationId)
                    {
                        Refresh(previous);
                        Dispatch(player, () => ReconcileOnTick(player, previous));
                    }
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Organization membership check failed character=" + player.Identity.Instance);
                }
            });
        }

        /// <summary>
        /// Watches the player's Side once per Player object (a zone change registers the same one again): a change after
        /// login takes the player out of its organization (<see cref="LeaveForSideChange"/>).
        /// </summary>
        void WatchSide(Player player)
        {
            int id = player.Identity.Instance;
            lock (_sideObservers)
            {
                if (_sideObservers.TryGetValue(id, out var watched))
                {
                    if (ReferenceEquals(watched.Player, player))
                        return;
                    watched.Player.Stats.StatChanged -= watched.Observer;
                }

                Action<CharacterStat, int, int, bool> observer = (stat, previous, current, initial) =>
                {
                    if (stat == CharacterStat.Side && !initial && previous != current)
                        LeaveForSideChange(player);
                };
                _sideObservers[id] = (player, observer);
                player.Stats.StatChanged += observer;
            }
        }

        /// <summary>
        /// Stops watching players that have left this instance (no session) and are not in its snapshot. The session test
        /// keeps a player registered after the snapshot was taken watched.
        /// </summary>
        void ForgetGoneSideWatches(IReadOnlyList<Player> players)
        {
            lock (_sideObservers)
            {
                foreach ((int id, (Player player, Action<CharacterStat, int, int, bool> observer)) in _sideObservers.ToArray())
                {
                    if (player.Session != null || players.Any(p => ReferenceEquals(p, player)))
                        continue;
                    player.Stats.StatChanged -= observer;
                    _sideObservers.Remove(id);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ cache

        /// <summary>Keeps the newer version; a disbanded organization stays disbanded (its id is never reused).</summary>
        OrgState Publish(OrgState state)
            => _cache.AddOrUpdate(state.Id, state,
                (_, old) => old.Disbanded ? old : state.Disbanded || state.Version > old.Version ? state : old);

        /// <summary>Loads the organization from the database into the cache; a missing one becomes a tombstone. Blocking.</summary>
        OrgState? Refresh(int organizationId)
        {
            if (organizationId <= 0)
                return null;
            var loaded = _store.Load(organizationId);
            if (loaded is { } found)
                return Publish(OrgState.From(found.Row, found.Members, Rules));
            string name = _cache.TryGetValue(organizationId, out OrgState? old) ? old.Name : string.Empty;
            return Publish(OrgState.Tombstone(organizationId, name));
        }

        /// <summary>The cached organization, null when unknown or disbanded.</summary>
        OrgState? Cached(int organizationId)
            => organizationId > 0 && _cache.TryGetValue(organizationId, out OrgState? state) && !state.Disbanded ? state : null;

        // ------------------------------------------------------------------------------------------------ delivery

        /// <summary>Runs a command's database work off the tick, then delivers what it did. Drops it while the actor has one running.</summary>
        void Run(Player actor, Func<Outcome> work)
        {
            int id = actor.Identity.Instance;
            if (!_busy.TryAdd(id, 0))
                return;
            _ = Task.Run(() =>
            {
                Outcome? outcome = null;
                try
                {
                    outcome = work();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Organization command failed character=" + id);
                }
                finally
                {
                    _busy.TryRemove(id, out _);
                }

                if (outcome != null)
                    Deliver(actor, outcome);
            });
        }

        void Deliver(Player? actor, Outcome outcome)
        {
            try
            {
                DeliverCore(actor, outcome);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Organization outcome delivery failed org=" + outcome.OrganizationId);
            }
        }

        void DeliverCore(Player? actor, Outcome outcome)
        {
            int organizationId = outcome.OrganizationId;
            OrgState? state = outcome.State != null ? Publish(outcome.State) : null;

            if (outcome.OrgMessagesFirst)
                SendOrgMessages(organizationId, outcome.OrgMessages);
            if (outcome.State != null)
                PublishChatMembership(outcome.Previous, outcome.State);
            if (!outcome.OrgMessagesFirst)
                SendOrgMessages(organizationId, outcome.OrgMessages);

            if (state != null)
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Organization change org={0} version={1} members={2} disbanded={3} by={4}", organizationId,
                    state.Disbanded ? "-" : state.Version.ToString(CultureInfo.InvariantCulture), state.Members.Count,
                    state.Disbanded, actor?.Identity.Instance ?? 0));

            ReconcileLocal(organizationId, outcome.State != null, outcome.Notices, outcome.InfoUpdated, actor, outcome);

            if (organizationId > 0 && (outcome.State != null || outcome.Notices.Count > 0 || outcome.InfoUpdated))
                Broadcast(organizationId, outcome);
        }

        void SendOrgMessages(int organizationId, List<string> lines)
        {
            foreach (string line in lines)
                _chat.SendOrganizationMessage(organizationId, line);
        }

        // ChatEngine puts characters in and out of organization channels by id, wherever they play.
        void PublishChatMembership(OrgState? previous, OrgState current)
        {
            IReadOnlyDictionary<int, OrgMember> before = previous?.Members ?? new Dictionary<int, OrgMember>();
            foreach (int characterId in before.Keys)
                if (current.Member(characterId) == null)
                    _chat.SendOrganizationMembership(characterId, 0, string.Empty);
            if (current.Disbanded)
                return;
            foreach (int characterId in current.Members.Keys)
                if (!before.ContainsKey(characterId))
                    _chat.SendOrganizationMembership(characterId, current.Id, current.Name);
            if (previous != null && !string.Equals(previous.Name, current.Name, StringComparison.Ordinal))
                _chat.SendOrganizationRenamed(current.Id, current.Name);
        }

        /// <summary>
        /// Brings this instance's players up to date with the cached organization, each on its own tick, and hands them
        /// their notices (and the actor its replies). Players not concerned are not touched.
        /// </summary>
        void ReconcileLocal(int organizationId, bool reconcile, List<(int CharacterId, string Text)> notices, bool infoUpdated,
            Player? actor, Outcome? outcome)
        {
            ILookup<int, string> byCharacter = notices.ToLookup(n => n.CharacterId, n => n.Text);
            OrgState? state = organizationId > 0 && _cache.TryGetValue(organizationId, out OrgState? cached) ? cached : null;
            bool actorHandled = false;
            foreach (Player player in _playfields.Value.SnapshotPlayers())
            {
                int id = player.Identity.Instance;
                bool isActor = ReferenceEquals(player, actor);
                bool concerned = isActor || byCharacter.Contains(id)
                    || (organizationId > 0 && (player.Organization?.Id == organizationId || state?.Member(id) != null));
                if (!concerned)
                    continue;
                actorHandled |= isActor;
                string[] mine = byCharacter[id].ToArray();
                Dispatch(player, () =>
                {
                    if (reconcile && organizationId > 0)
                        ReconcileOnTick(player, organizationId);
                    if (isActor && outcome != null)
                    {
                        outcome.OnActorTick?.Invoke(player);
                        foreach (MessageBody message in outcome.Actor)
                            player.Session?.Send(message);
                    }

                    foreach (string text in mine)
                        player.Session?.Send(OrgText.Line(player.Identity, text));
                    if (infoUpdated && player.Organization?.Id == organizationId)
                        player.Session?.Send(OrgText.Plain(player.Identity, OrgText.InfoUpdated));
                });
            }

            // The actor left this instance meanwhile: its replies have nobody to go to, but credits must not be lost.
            if (actor != null && !actorHandled && outcome?.OnActorTick != null)
                Dispatch(actor, () => outcome.OnActorTick(actor));
        }

        /// <summary>On the player's tick: its Clan and ClanLevel stats and visible organization from the cached organization.</summary>
        void ReconcileOnTick(Player player, int organizationId)
        {
            if (player.Session == null || !_cache.TryGetValue(organizationId, out OrgState? state))
                return;
            int id = player.Identity.Instance;
            OrgMember? member = state.Member(id);
            PlayerOrganization? before = player.Organization;
            bool visibleChange = false;
            if (member != null)
            {
                string rankName = Rules.FormOf(state.GoverningForm).RankName(member.Rank);
                if (before == null || before.Id != state.Id || !string.Equals(before.Name, state.Name, StringComparison.Ordinal))
                {
                    player.Organization = new PlayerOrganization(state.Id, state.Name, rankName);
                    visibleChange = true;
                }
                else if (!string.Equals(before.RankName, rankName, StringComparison.Ordinal))
                {
                    // A promotion or demotion: the info window's rank line only; the spawn carries no rank.
                    player.Organization = before with { RankName = rankName };
                }

                SetStat(player, CharacterStat.Clan, state.Id);
                SetStat(player, CharacterStat.ClanLevel, member.Rank);
            }
            else if (before?.Id == state.Id)
            {
                player.Organization = null;
                SetStat(player, CharacterStat.Clan, 0);
                visibleChange = true;
                // Retail: an empty OrgInfo with the Clan stat, so the client's organization window empties.
                player.Session.Send(OrgWire.Info(player.Identity, null, string.Empty, string.Empty));
            }

            player.FlushDirtyStats();
            // The organization tag others see is in the spawn packet only.
            if (visibleChange && player.Playfield is Playfield playfield)
                playfield.GetRequiredService<PlayfieldLocality>().RespawnForObservers(player, except: player);
        }

        static void SetStat(Player player, CharacterStat stat, int value)
        {
            if (player.Stats.GetOrZero(stat, StatDetail.Base) != value)
                player.Stats.Set(stat, value, StatDetail.Base, dirty: true);
        }

        /// <summary>Runs <paramref name="action"/> on the player's tick while it is still this instance's player in play.</summary>
        void Dispatch(Player player, Action action)
        {
            if (player.Playfield is not Playfield playfield)
                return;
            playfield.DispatchPlayerProjection(player, () =>
            {
                if (player.Playfield == null || player.Session == null
                    || !_playfields.Value.FindPlayer(player.Identity.Instance, out Player current) || !ReferenceEquals(current, player))
                    return;
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Organization update failed character=" + player.Identity.Instance);
                }
            });
        }

        // ------------------------------------------------------------------------------------------------ other instances

        void Broadcast(int organizationId, Outcome outcome)
        {
            if (_commands == null)
                return;
            var arguments = new List<string>(2 + outcome.Notices.Count * 2)
            {
                organizationId.ToString(CultureInfo.InvariantCulture),
                outcome.InfoUpdated ? "1" : "0"
            };
            foreach ((int characterId, string text) in outcome.Notices)
            {
                arguments.Add(characterId.ToString(CultureInfo.InvariantCulture));
                arguments.Add(text);
            }

            _ = _commands.BroadcastAsync(ChangedCommand, arguments.ToArray());
        }

        void OnChangedElsewhere(InstanceCommand command)
        {
            if (_disposed || !Rules.Enabled || command.Arguments.Count < 2
                || !int.TryParse(command.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int organizationId)
                || organizationId <= 0)
                return;
            bool infoUpdated = command.Arguments[1] == "1";
            var notices = new List<(int, string)>();
            for (int i = 2; i + 1 < command.Arguments.Count; i += 2)
                if (int.TryParse(command.Arguments[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int characterId) && characterId > 0)
                    notices.Add((characterId, command.Arguments[i + 1]));

            // Only an instance that has a member here, or a member the change names, needs the organization.
            IReadOnlyList<Player> players = _playfields.Value.SnapshotPlayers();
            bool local = _cache.ContainsKey(organizationId)
                || players.Any(p => p.Organization?.Id == organizationId)
                || notices.Any(n => players.Any(p => p.Identity.Instance == n.Item1));
            OrgState? previous = Cached(organizationId);
            OrgState? state = Refresh(organizationId);
            if (!local && state != null && !state.Disbanded)
                local = players.Any(p => state.Member(p.Identity.Instance) != null);
            if (!local)
            {
                _cache.TryRemove(organizationId, out _);
                return;
            }

            ReconcileLocal(organizationId, previous == null || state == null || state.Version != previous.Version || state.Disbanded,
                notices, infoUpdated, null, null);
        }

        // ------------------------------------------------------------------------------------------------ timers

        /// <summary>Versions of every organization this instance shows; reloads and reconciles any that moved, drops the rest.</summary>
        void Sync()
        {
            if (_disposed || !Rules.Enabled || Interlocked.Exchange(ref _syncing, 1) != 0)
                return;
            try
            {
                DateTime now = DateTime.UtcNow;
                foreach ((int id, Invitation invitation) in _invitations)
                    if (invitation.ExpiresAtUtc <= now)
                        _invitations.TryRemove(id, out _);
                foreach ((int id, PendingTransfer transfer) in _transfers)
                    if (transfer.ExpiresAtUtc <= now)
                        _transfers.TryRemove(id, out _);

                IReadOnlyList<Player> players = _playfields.Value.SnapshotPlayers();
                ForgetGoneSideWatches(players);
                var shown = new HashSet<int>(players.Select(p => p.Organization?.Id ?? 0).Where(id => id > 0));
                foreach (int id in _cache.Keys)
                    if (!shown.Contains(id))
                        _cache.TryRemove(id, out _);
                if (shown.Count == 0)
                    return;

                IReadOnlyDictionary<int, long> versions = _store.Versions(shown);
                foreach (int id in shown)
                {
                    OrgState? cached = _cache.TryGetValue(id, out OrgState? found) ? found : null;
                    bool stale = versions.TryGetValue(id, out long version)
                        ? cached == null || cached.Disbanded || cached.Version != version
                        : cached is not { Disbanded: true };
                    if (!stale)
                        continue;
                    Refresh(id);
                    ReconcileLocal(id, true, [], false, null, null);
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Organization sync failed");
            }
            finally
            {
                Volatile.Write(ref _syncing, 0);
            }
        }

        /// <summary>Ends votes whose time is up and posts their results. Only World's duty holder sweeps.</summary>
        void SweepVotes()
        {
            if (_disposed || !Rules.Enabled || (_world != null && !_world.IsDutyHolder)
                || Interlocked.Exchange(ref _sweeping, 1) != 0)
                return;
            try
            {
                foreach (long voteId in _store.DueVotes(25))
                {
                    OrgVoteTally? tally = _store.CloseDueVote(voteId);
                    if (tally != null)
                        _chat.SendOrganizationMessage(tally.Vote.OrganizationId, VoteResultsText(tally));
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Organization vote sweep failed");
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        static Outcome ActorLine(Player actor, string text)
        {
            var outcome = new Outcome();
            outcome.Actor.Add(OrgText.Line(actor.Identity, text));
            return outcome;
        }

        static Outcome ActorFeedback(Player actor, int category, int id)
        {
            var outcome = new Outcome();
            outcome.Actor.Add(OrgText.Feedback(actor.Identity, category, id));
            return outcome;
        }

        static void Reply(Player player, string text) => player.Session?.Send(OrgText.Line(player.Identity, text));

        static void ReplyFeedback(Player player, int category, int id) => player.Session?.Send(OrgText.Feedback(player.Identity, category, id));

        /// <summary>
        /// Runs <paramref name="body"/> in the organization's transaction once the actor is confirmed a member there;
        /// a committed change comes back with the organization before and after.
        /// </summary>
        Outcome InOrganization(Player actor, int organizationId, Func<OrgTransaction, OrgState, OrgMember, Outcome> body)
            => _store.Transact(organizationId, tx =>
            {
                if (tx.Row == null)
                    return ActorLine(actor, OrgText.Org(OrgText.NotInOrganization));
                OrgState state = OrgState.From(tx.Row, tx.Members, Rules);
                OrgMember? me = state.Member(actor.Identity.Instance);
                if (me == null)
                    return ActorLine(actor, OrgText.Org(OrgText.NotInOrganization));
                Outcome outcome = body(tx, state, me);
                outcome.OrganizationId = organizationId;
                if (tx.Changed)
                {
                    var after = tx.Reload();
                    outcome.Previous = state;
                    outcome.State = after is { } row ? OrgState.From(row.Row, row.Members, Rules) : OrgState.Tombstone(organizationId, state.Name);
                }

                return outcome;
            });

        /// <summary>The player <paramref name="characterId"/> when it stands on the actor's playfield within interaction range.</summary>
        bool InSight(Player actor, int characterId, out Player target)
        {
            target = null!;
            if (characterId <= 0 || characterId == actor.Identity.Instance
                || !_playfields.Value.FindPlayer(characterId, out Player found)
                || found.Session == null || !ReferenceEquals(found.Playfield, actor.Playfield)
                || actor.GetEdgeDistanceTo(found) > Rules.InteractionRange)
                return false;
            target = found;
            return true;
        }

        /// <summary>The info window for <paramref name="recipient"/>, with <paramref name="subjectId"/>'s rank when <paramref name="showRank"/>.</summary>
        LostEden.Protocol.Messages.N3Messages.OrgServerMessage InfoFor(Identity recipient, OrgState state, int subjectId, bool showRank)
        {
            GoverningForm form = Rules.FormOf(state.GoverningForm);
            string rank = showRank && state.Member(subjectId) is { } member ? form.RankName(member.Rank) : string.Empty;
            return OrgWire.Info(recipient, state, rank, form.Name);
        }
    }
}
