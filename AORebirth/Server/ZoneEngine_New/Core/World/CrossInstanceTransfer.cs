namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Trade;

    using PlayfieldInstance = ZoneEngine_New.Core.Playfield.Playfield;
    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// A move to a playfield another zone instance runs: save here (as a logout does, with the character placed at
    /// the destination), let World move the lease, then redirect the client. The destination reloads the character
    /// from the database and applies the envelope (pets, the arrival's door). Until World accepts, nothing is lost:
    /// a refusal leaves the player where it was.
    /// </summary>
    public sealed class CrossInstanceTransfer
    {
        readonly WorldLink _world;
        readonly CharacterSnapshotService _snapshot;
        readonly InventoryFlushService _flush;
        readonly TradeService _trades;
        readonly IZoneLogger _logger;
        readonly ZoneEngine_New.Core.GameData.IGameData _gameData;

        public CrossInstanceTransfer(WorldLink world, CharacterSnapshotService snapshot, InventoryFlushService flush,
            TradeService trades, IZoneLogger logger, ZoneEngine_New.Core.GameData.IGameData gameData)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
            _trades = trades ?? throw new ArgumentNullException(nameof(trades));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        }

        /// <summary>Runs on the player's own playfield tick. False when the move could not start.</summary>
        public bool Start(Player player, int destinationPlayfield, Vector3 landing, Quaternion heading,
            ArrivalKind arrival = ArrivalKind.ArrivalZone, int exitProxyDoor = 0, PlayfieldSpawnState? spawnState = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(landing);
            ArgumentNullException.ThrowIfNull(heading);
            if (player.Playfield is not PlayfieldInstance source || player.Session is not ZoneSession session)
                return false;
            int characterId = player.Identity.Instance;
            if (!session.TryGetHandoffCookies(characterId, out uint cookie1, out uint cookie2) || !_world.IsServing)
            {
                Tell(player, "That destination is not available right now.");
                return false;
            }

            if (!session.TryBeginRemoteTransfer(player))
                return false;

            TransferEnvelope envelope;
            try
            {
                envelope = BuildEnvelope(player, destinationPlayfield, landing, heading, arrival, exitProxyDoor);
                if (source.GetRequiredService<PlayfieldManager>().Teams.SnapshotTeamState(player) is { } team)
                    envelope.Team = team;
                // The logout save, in the same order (SpawnService.DespawnPlayer).
                player.InterruptTimedActions(TimedActionInterrupt.LeavePlayfield);
                // Offered items go back before the save, as on logout.
                _trades.Cancel(player, "changed zone");
                if (player.Inventory.IsHydrated)
                    player.SnapshotActiveNanosForTransfer();
                // Also stores queued quest commits; an unhydrated player with none has nothing to flush.
                _flush.HardFlush(player);

                _snapshot.CommitForTransfer(player, destinationPlayfield, landing, heading);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Transfer save failed for character " + characterId.ToString(CultureInfo.InvariantCulture));
                Restore(player, session, "Your zone change failed.");
                return false;
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Cross-instance transfer character={0} from={1} to={2}", characterId, source.Identity.Instance, destinationPlayfield));

            // World answers off the tick; the outcome is applied back on the player's own tick.
            _ = Task.Run(async () =>
            {
                // Null: the outcome is unknown (timeout, dropped connection). World may have moved the lease.
                TransferReply? reply;
                try
                {
                    reply = await _world.BeginTransferAsync(characterId, cookie1, cookie2, envelope, spawnState).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Error("Transfer of character " + characterId.ToString(CultureInfo.InvariantCulture)
                        + ": no answer from World (" + exception.GetType().Name + ")");
                    reply = null;
                }

                source.DispatchPlayerProjection(player, () => Complete(player, session, source, reply, landing, heading, destinationPlayfield));
            });
            return true;
        }

        void Complete(Player player, ZoneSession session, PlayfieldInstance source, TransferReply? reply,
            Vector3 landing, Quaternion heading, int destinationPlayfield)
        {
            int characterId = player.Identity.Instance;
            if (reply == null)
            {
                // Never keep playing a character World might have handed on: two live copies could each save it.
                // It was saved for the move, so it simply logs in again (World routes it to the right instance).
                Tell(player, "Your zone change could not be confirmed. Please log in again.");
                if (ReferenceEquals(player.Playfield, source))
                    source.GetRequiredService<SpawnService>().DepartToInstance(player);
                session.Close();
                return;
            }

            if (!reply.Accepted)
            {
                _logger.Warn("Cross-instance transfer of character " + characterId.ToString(CultureInfo.InvariantCulture)
                    + " refused: " + reply.Reason);
                Restore(player, session, "That destination is not available right now.");
                return;
            }

            // World moved the lease: this instance must neither save nor release the character any more.
            _world.TransferredAway(characterId);
            try
            {
                if (IPAddress.TryParse(reply.Target.Address, out IPAddress? address) && reply.Target.Port is > 0 and <= ushort.MaxValue
                    && ReferenceEquals(player.Session, session))
                {
                    // A proxy instance hosted elsewhere: the teleport still names the interior the client loads.
                    // The client is told a door's proxy instance as the interior it copies (Playfield.ClientPlayfieldId).
                    int clientPlayfieldId = destinationPlayfield;
                    if (ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.IsProxy(destinationPlayfield)
                        && !ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.TryResolveResource(_gameData, destinationPlayfield, out clientPlayfieldId, out _, out _))
                        clientPlayfieldId = destinationPlayfield;
                    if (ZoneEngine_New.Core.Cities.CityInsides.TryDescribe(destinationPlayfield, out ZoneEngine_New.Core.Cities.CityInsides.Target inside))
                        clientPlayfieldId = inside.ResourceId;

                    foreach (byte[] packet in session.PrepareRemoteTransferPackets(player, landing, heading, clientPlayfieldId,
                        address, (ushort)reply.Target.Port))
                        session.Send(packet);
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Transfer redirect failed for character " + characterId.ToString(CultureInfo.InvariantCulture));
            }

            if (ReferenceEquals(player.Playfield, source))
                source.GetRequiredService<SpawnService>().DepartToInstance(player);

            // The client reconnects to the destination by itself; drop this socket once it has had time to.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => session.Close(), TaskScheduler.Default);
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Cross-instance transfer character={0} handed to {1} ({2}:{3})", characterId, reply.TargetInstance,
                reply.Target.Address, reply.Target.Port));
        }

        void Restore(Player player, ZoneSession session, string message)
        {
            try
            {
                if (!player.IsPersistenceQuarantined && _world.OwnsLease(player.Identity.Instance))
                    _snapshot.RestoreOnline(player.Identity.Instance);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Restoring Online failed for character " + player.Identity.Instance.ToString(CultureInfo.InvariantCulture));
            }

            session.AbortRemoteTransfer();
            Tell(player, message);
        }

        internal static TransferEnvelope BuildEnvelope(Player player, int destinationPlayfield, Vector3 landing, Quaternion heading,
            ArrivalKind arrival, int exitProxyDoor)
        {
            var envelope = new TransferEnvelope
            {
                DestinationPlayfield = destinationPlayfield,
                X = landing.xf,
                Y = landing.yf,
                Z = landing.zf,
                HeadingX = heading.xf,
                HeadingY = heading.yf,
                HeadingZ = heading.zf,
                HeadingW = heading.wf,
                Arrival = arrival,
                ExitProxyDoor = exitProxyDoor,
                SavedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            DateTime now = DateTime.UtcNow;
            foreach (NpcCharacter pet in player.OwnedPets.All.ToList())
            {
                // A charm holds only where it was cast.
                // A tower stays behind and waits (PetService.HoldStayBehindPets).
                if (pet.Pet is not { } controller || controller.IsCharmed || pet.IsDead || controller.HasExpired(now)
                    || Pets.PetTypes.StaysBehind(controller.Type))
                    continue;
                int max = pet.Stats.GetOrZero(CharacterStat.MaxHealth);
                var carried = new PetEnvelope
                {
                    Hash = controller.Hash,
                    Level = controller.Level,
                    HealthFraction = max > 0 ? Math.Clamp(pet.Stats.GetOrZero(CharacterStat.Health) / (float)max, 0.01f, 1f) : 1f,
                    RemainingSeconds = controller.ExpiresUtc is DateTime expires
                        ? Math.Max(1, (int)Math.Ceiling((expires - now).TotalSeconds))
                        : 0,
                    // An attack order's target stays on this instance: the pet goes on with what it did before it.
                    Mode = (int)(controller.Mode == Pets.PetMode.Attack ? controller.ModeBeforeAttack : controller.Mode),
                    AggDef = pet.Stats.GetOrZero(CharacterStat.AggDef, StatDetail.Base)
                };
                AddPetBuffs(carried, pet, now);
                envelope.Pets.Add(carried);
            }

            return envelope;
        }

        /// <summary>The pet's NCU with the time each buff has left, and what is left of its absorb pools.</summary>
        internal static void AddPetBuffs(PetEnvelope carried, NpcCharacter pet, DateTime nowUtc)
        {
            foreach (Nanos.Buff buff in pet.Buffs)
            {
                int remaining = buff.RemainingCentiseconds(nowUtc);
                if (remaining <= 0 || buff.NanoInstance <= 0)
                    continue;
                carried.Buffs.Add(new PetBuffEnvelope
                {
                    NanoId = buff.Id,
                    NanoInstance = buff.NanoInstance,
                    RemainingCentiseconds = remaining,
                    SourceType = (int)buff.Source.Type,
                    SourceInstance = buff.Source.Instance
                });
            }

            for (CharacterStat stat = CharacterStat.AbsorbProjectileAC; stat <= CharacterStat.AbsorbNanoAC; stat++)
            {
                int pool = pet.Stats.GetOrZero(stat, StatDetail.Base);
                if (pool > 0)
                    carried.AbsorbPools[(int)stat] = pool;
            }
        }

        static void Tell(Player player, string text)
            => player.Session?.Send(new LostEden.Protocol.Messages.N3Messages.ChatTextMessage
            {
                Identity = player.Identity,
                Text = text,
                Unknown1 = 0,
                Unknown2 = 0
            });
    }

    /// <summary>The arriving side of a cross-instance move.</summary>
    public static class EnvelopeApplier
    {
        /// <summary>
        /// Summons again the pets the character brought, beside it, with their health, remaining time, order and NCU.
        /// </summary>
        public static void SummonPets(PlayfieldInstance playfield, Player player, TransferEnvelope envelope, IZoneLogger logger)
        {
            if (envelope.Pets.Count == 0) return;
            var pets = playfield.GetRequiredService<Pets.PetService>();
            IItemBuilder items = playfield.GetRequiredService<IItemBuilder>();
            foreach (PetEnvelope carried in envelope.Pets)
            {
                try
                {
                    NpcCharacter? pet = pets.Summon(player, carried.Hash, carried.Level, carried.RemainingSeconds);
                    if (pet == null) continue;
                    RestorePet(pet, carried, items, DateTime.UtcNow);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Re-summoning pet " + carried.Hash + " for character "
                        + player.Identity.Instance.ToString(CultureInfo.InvariantCulture) + " failed");
                }
            }
        }

        /// <summary>
        /// Puts a freshly summoned pet back the way it left. Its buffs return first, with the time they had left and
        /// their absorb pools as they stood (never a fresh grant), so health is set against the buffed maximum.
        /// </summary>
        internal static void RestorePet(NpcCharacter pet, PetEnvelope carried, IItemBuilder items, DateTime nowUtc)
        {
            foreach (PetBuffEnvelope buff in carried.Buffs)
            {
                if (buff.NanoId <= 0 || buff.NanoInstance <= 0 || buff.RemainingCentiseconds <= 0)
                    continue;
                Nanos.NanoSpell spell = Nanos.NanoSpell.From(items.CreateTemplate(buff.NanoId, buff.NanoId, quality: 1));
                var source = new Identity { Type = (IdentityType)buff.SourceType, Instance = buff.SourceInstance };
                pet.TryRestoreBuff(spell, source, buff.NanoInstance,
                    nowUtc.AddMilliseconds(Math.Min(buff.RemainingCentiseconds, Nanos.Buff.MaxDurationCentiseconds) * 10L));
            }

            if (carried.Buffs.Count > 0)
            {
                for (CharacterStat stat = CharacterStat.AbsorbProjectileAC; stat <= CharacterStat.AbsorbNanoAC; stat++)
                    pet.Stats.Set(stat, carried.AbsorbPools.TryGetValue((int)stat, out int pool) ? Math.Max(0, pool) : 0, StatDetail.Base);
                // Each pool goes back to the buffs that grant it, up to their grant; what no buff accounts for is dropped.
                pet.ReconcileAbsorbs();
                pet.RebaseStats();
            }

            int max = pet.Stats.GetOrZero(CharacterStat.MaxHealth);
            if (max > 0)
                pet.Stats.Set(CharacterStat.Health, Math.Max(1, (int)Math.Round(max * carried.HealthFraction)), StatDetail.Base, dirty: true);
            if (carried.HasAggDef)
                pet.Stats.Set(CharacterStat.AggDef, carried.AggDef, StatDetail.Base, dirty: true);
            // An attack order never crosses (its target is on the other instance); an older sender may still send one.
            if (Enum.IsDefined(typeof(Pets.PetMode), carried.Mode) && (Pets.PetMode)carried.Mode != Pets.PetMode.Attack)
                pet.Pet?.Order((Pets.PetMode)carried.Mode);
        }
    }
}
