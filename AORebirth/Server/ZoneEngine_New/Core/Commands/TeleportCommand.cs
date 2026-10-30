namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;
    using System.IO;

    using AORebirth.Core.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Playfield;

    using CharacterStat = SmokeLounge.AOtomation.Messaging.GameData.CharacterStat;
    using IdentityType = SmokeLounge.AOtomation.Messaging.GameData.IdentityType;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    public sealed class TeleportCommand : IGmCommand
    {
        private readonly Lazy<PlayfieldManager> _playfieldManager;
        private readonly IGameData _gameData;
        private readonly ZoneEngine_New.Core.World.WorldLink? _world;
        private readonly ZoneEngine_New.Core.World.WorldCommands? _commands;

        /// <summary>A GM on another zone instance summons a player here: playfield, x, y, z, GM name.</summary>
        const string SummonCommand = "gm.summon";

        public TeleportCommand(Lazy<PlayfieldManager> playfieldManager, IGameData gameData,
            ZoneEngine_New.Core.World.WorldLink? world = null, ZoneEngine_New.Core.World.WorldCommands? commands = null)
        {
            ArgumentNullException.ThrowIfNull(playfieldManager);
            ArgumentNullException.ThrowIfNull(gameData);
            _playfieldManager = playfieldManager;
            _gameData = gameData;
            _world = world;
            _commands = commands;
            _commands?.Register(SummonCommand, OnSummonedFromElsewhere);
        }

        /// <summary>The summoned player is here; the GM is on another instance. Move it to the GM.</summary>
        void OnSummonedFromElsewhere(AORebirth.World.Contracts.V1.InstanceCommand command)
        {
            if (command.Arguments.Count < 4
                || !int.TryParse(command.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
                || !float.TryParse(command.Arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(command.Arguments[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(command.Arguments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                || !_playfieldManager.Value.FindPlayer(command.CharacterId, out Player subject)
                || subject.Playfield is not Playfield at)
                return;
            var landing = new Vector3(x, y, z);
            at.DispatchPlayerProjection(subject, () =>
            {
                if (subject.Session == null || !ReferenceEquals(subject.Playfield, at)) return;
                subject.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 0, StatDetail.Base, dirty: true);
                subject.Stats.Set(CharacterStat.ExternalDoorInstance, 0, StatDetail.Base, dirty: true);
                _playfieldManager.Value.WithPlayfield(playfieldId, subject,
                    destination => subject.Session?.TransferToPlayfield(destination, landing),
                    new ZoneEngine_New.Core.World.RemoteArrival(landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport));
            });
        }

        /// <summary>
        /// Not on this zone instance: World knows where the character is. Runs the answer on the GM's own tick.
        /// </summary>
        bool TryElsewhere(GmCommandContext context, string name, bool summon)
        {
            if (_world == null) return false;
            Player gm = context.Player;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                AORebirth.World.Contracts.V1.LocateReply where;
                try { where = await _world.LocateAsync(0, name).ConfigureAwait(false); }
                catch (Exception) { where = new AORebirth.World.Contracts.V1.LocateReply { Found = false }; }

                if (!where.Found || where.PlayfieldId <= 0)
                {
                    gm.Playfield?.DispatchPlayerProjection(gm, () =>
                        GmCommandFeedback.Send(context.Session, gm, "No online character named " + name + "."));
                    return;
                }

                if (summon)
                {
                    if (gm.Playfield is not Playfield here) return;
                    bool sent = _commands != null && await _commands.ToCharacterAsync(where.CharacterId, SummonCommand,
                        here.Identity.Instance.ToString(CultureInfo.InvariantCulture),
                        gm.Position.xf.ToString(CultureInfo.InvariantCulture),
                        gm.Position.yf.ToString(CultureInfo.InvariantCulture),
                        gm.Position.zf.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    gm.Playfield?.DispatchPlayerProjection(gm, () => GmCommandFeedback.Send(context.Session, gm,
                        sent ? "Summoning " + name + " from zone instance " + where.Instance + "."
                            : "Could not reach " + name + " on zone instance " + where.Instance + "."));
                    return;
                }

                var landing = new Vector3(where.X, where.Y, where.Z);
                gm.Playfield?.DispatchPlayerProjection(gm, () =>
                {
                    if (gm.Session == null) return;
                    GmCommandFeedback.Send(context.Session, gm, "Teleporting to " + name + " on zone instance " + where.Instance + ".");
                    gm.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 0, StatDetail.Base, dirty: true);
                    gm.Stats.Set(CharacterStat.ExternalDoorInstance, 0, StatDetail.Base, dirty: true);
                    _playfieldManager.Value.WithPlayfield(where.PlayfieldId, gm,
                        destination => gm.Session?.TransferToPlayfield(destination, landing),
                        new ZoneEngine_New.Core.World.RemoteArrival(landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport));
                });
            });
            return true;
        }

        public string Name => "tp";

        /// <summary>GM level that runs .tp; <see cref="OnlineCommand"/> offers [TP] links from it.</summary>
        public const int GmLevel = 1;

        public int RequiredGmLevel => GmLevel;

        public string Usage =>
            ".tp <x> <z> <playfieldId> or .tp <x> <y> <z> <playfieldId> (look-at player if selected) | .tp summon <character name> | .tp to <character name>";

        public GmCommandHelp Help { get; } = new(
            "Teleports to coordinates or between players.",
            [
                new(".tp <x> <z> <playfieldId>", "To coordinates; the height comes from the ground."),
                new(".tp <x> <y> <z> <playfieldId>", "To exact coordinates."),
                new(".tp summon <name>", "Brings a player to you."),
                new(".tp to <name>", "Takes you to a player."),
            ],
            [
                new(".tp 440 285 566", "To (440, 285) in playfield 566."),
                new(".tp 440 29 285 566", "With an exact height."),
                new(".tp summon Delmus", "Bring Delmus here."),
                new(".tp to Delmus", "Go to Delmus."),
            ],
            "Coordinate teleports move your look-at player when one is selected, otherwise you.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Args.Length >= 1 && string.Equals(context.Args[0], "summon", StringComparison.OrdinalIgnoreCase))
            {
                Summon(context);
                return;
            }

            if (context.Args.Length >= 1 && string.Equals(context.Args[0], "to", StringComparison.OrdinalIgnoreCase))
            {
                GoTo(context);
                return;
            }

            bool hasExplicitY = context.Args.Length >= 4;
            int zIndex = hasExplicitY ? 2 : 1;
            int playfieldIndex = hasExplicitY ? 3 : 2;
            if (context.Args.Length < 3
                || !float.TryParse(context.Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(context.Args[zIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                || !int.TryParse(context.Args[playfieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
                || playfieldId <= 0)
            {
                context.SendUsage();
                return;
            }

            string playfieldDir = Path.Combine(
                _gameData.RootPath,
                GameDataPaths.PlayfieldRelativeDirectory(playfieldId));
            if (!Directory.Exists(playfieldDir))
            {
                GmCommandFeedback.Send(
                    context.Session,
                    context.Player,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Unknown playfield id: {0}",
                        playfieldId));
                return;
            }

            // Look-at player is the subject; do not silently teleport self when target is set.
            if (!context.TryResolveSubject(out Player subject, requirePlayerTarget: true))
                return;

            Playfield? playfield = subject.Playfield;
            if (playfield == null)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Not on a playfield.");
                return;
            }

            float y = subject.Position.yf;
            if (hasExplicitY
                && !float.TryParse(context.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                context.SendUsage();
                return;
            }
            Vector3 landing = new Vector3(x, y, z);
            string who = ReferenceEquals(subject, context.Player)
                ? "self"
                : (string.IsNullOrEmpty(subject.Name)
                    ? subject.Identity.Instance.ToString(CultureInfo.InvariantCulture)
                    : subject.Name);

            if (playfieldId != playfield.Identity.Instance)
            {
                if (subject.Session == null)
                {
                    GmCommandFeedback.Send(context.Session, context.Player, "Target has no session.");
                    return;
                }

                // Building a playfield that is not loaded yet takes a moment (off every tick); say so before it starts.
                if (!_playfieldManager.Value.TryGet(playfieldId, out _))
                    GmCommandFeedback.Send(context.Session, context.Player, "Initializing playfield, please wait...");

                // A playfield another zone instance runs: the subject moves there with no exit return here.
                var remote = new ZoneEngine_New.Core.World.RemoteArrival(landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport, Prepare: () =>
                {
                    subject.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 0, StatDetail.Base, dirty: true);
                    subject.Stats.Set(CharacterStat.ExternalDoorInstance, 0, StatDetail.Base, dirty: true);
                    GmCommandFeedback.Send(context.Session, context.Player, string.Format(CultureInfo.InvariantCulture,
                        "Teleporting {0} to pf={1} on another zone instance", who, playfieldId));
                    return true;
                });
                if (!_playfieldManager.Value.WithPlayfield(playfieldId, subject, destination => Relocate(context, subject, destination, landing, who), remote))
                    GmCommandFeedback.Send(context.Session, context.Player, "Target is already changing playfield.");
                return;
            }

            Relocate(context, subject, playfield, landing, who);
        }

        /// <summary>
        /// <c>.tp summon &lt;name&gt;</c>: brings an online character (matched by name, ignoring case) to the issuer's
        /// playfield and position, including into a quest dungeon the issuer is in.
        /// </summary>
        void Summon(GmCommandContext context)
        {
            if (context.Args.Length < 2)
            {
                context.SendUsage("Usage: .tp summon <character name>");
                return;
            }

            string name = string.Join(" ", context.Args, 1, context.Args.Length - 1).Trim();
            Player? subject = null;
            foreach (Player online in _playfieldManager.Value.SnapshotPlayers())
            {
                if (string.Equals(online.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    subject = online;
                    break;
                }
            }

            if (subject == null)
            {
                if (!TryElsewhere(context, name, summon: true))
                    GmCommandFeedback.Send(context.Session, context.Player, "No online character named " + name + ".");
                return;
            }

            if (ReferenceEquals(subject, context.Player))
            {
                GmCommandFeedback.Send(context.Session, context.Player, "You cannot summon yourself.");
                return;
            }

            if (context.Player.Playfield is not Playfield destination)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Not on a playfield.");
                return;
            }

            if (subject.Playfield == null)
            {
                GmCommandFeedback.Send(context.Session, context.Player, subject.Name + " is not on a playfield.");
                return;
            }

            Vector3 landing = new Vector3(context.Player.Position.xf, context.Player.Position.yf, context.Player.Position.zf);
            Relocate(context, subject, destination, landing, subject.Name, context.Player);
        }

        /// <summary>
        /// <c>.tp to &lt;name&gt;</c>: takes the issuer to an online character (matched by name, ignoring case): their
        /// playfield and position, including a quest dungeon they are in. The reverse of <see cref="Summon"/>.
        /// </summary>
        void GoTo(GmCommandContext context)
        {
            if (context.Args.Length < 2)
            {
                context.SendUsage("Usage: .tp to <character name>");
                return;
            }

            string name = string.Join(" ", context.Args, 1, context.Args.Length - 1).Trim();
            Player? target = null;
            foreach (Player online in _playfieldManager.Value.SnapshotPlayers())
            {
                if (string.Equals(online.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    target = online;
                    break;
                }
            }

            if (target == null)
            {
                if (!TryElsewhere(context, name, summon: false))
                    GmCommandFeedback.Send(context.Session, context.Player, "No online character named " + name + ".");
                return;
            }

            if (ReferenceEquals(target, context.Player))
            {
                GmCommandFeedback.Send(context.Session, context.Player, "You cannot teleport to yourself.");
                return;
            }

            if (target.Playfield is not Playfield destination)
            {
                GmCommandFeedback.Send(context.Session, context.Player, target.Name + " is not on a playfield.");
                return;
            }

            Vector3 at = target.Position;
            Vector3 landing = new Vector3(at.xf, at.yf, at.zf);
            Relocate(context, context.Player, destination, landing, "self", target);
        }

        /// <summary>
        /// Moves <paramref name="subject"/> to <paramref name="landing"/> on <paramref name="destination"/>: a zone
        /// transfer to another playfield, or a position update announced where they are. Tells the issuer and, when it
        /// is someone else, the subject.
        /// </summary>
        void Relocate(GmCommandContext context, Player subject, Playfield destination, Vector3 landing, string who, Player? returnFrom = null)
        {
            Playfield? current = subject.Playfield;
            if (current == null)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Not on a playfield.");
                return;
            }

            int playfieldId = destination.Identity.Instance;
            string toIssuer = string.Format(CultureInfo.InvariantCulture, "Teleported {0} to ({1}, {2}, {3}) pf={4}",
                who, landing.xf, landing.yf, landing.zf, playfieldId);
            string toSubject = string.Format(CultureInfo.InvariantCulture, "Teleported to ({0}, {1}, {2}) pf={3}",
                landing.xf, landing.yf, landing.zf, playfieldId);

            if (!ReferenceEquals(destination, current))
            {
                if (subject.Session == null)
                {
                    GmCommandFeedback.Send(context.Session, context.Player, "Target has no session.");
                    return;
                }

                SetExitReturn(subject, current, destination, returnFrom);

                GmCommandFeedback.Send(context.Session, context.Player, toIssuer);
                if (!ReferenceEquals(subject, context.Player))
                    GmCommandFeedback.Send(subject.Session, subject, toSubject);

                if (!subject.Session.TransferToPlayfield(destination, landing))
                    GmCommandFeedback.Send(context.Session, context.Player, who + " is already zoning; the teleport did not start.");
                return;
            }

            // Same playfield: a soft teleport, no zone transfer.
            subject.TeleportWithinPlayfield(landing);

            GmCommandFeedback.Send(context.Session, context.Player, toIssuer);
            if (!ReferenceEquals(subject, context.Player) && subject.Session != null)
                GmCommandFeedback.Send(subject.Session, subject, toSubject);
        }

        /// <summary>
        /// Gives a GM jump the exit-proxy return a door entry would have recorded, so the destination's exits lead
        /// somewhere instead of declining. Joining <paramref name="returnFrom"/> (already in the destination) shares
        /// their way out; otherwise the way out is the door nearest the subject's departure point. Outdoor
        /// destinations (playfield metadata) clear it, so a stale door cannot pull the character back later.
        /// </summary>
        void SetExitReturn(Player subject, Playfield source, Playfield destination, Player? returnFrom)
        {
            int returnPlayfield = 0;
            int returnDoor = 0;
            int destinationId = destination.Identity.Instance;
            if (_gameData.GetPlayfieldMetaData(destinationId) == null)
            {
                int sharedPlayfield = returnFrom?.Stats.GetOrZero(CharacterStat.ExternalPlayfieldInstance) ?? 0;
                int sharedDoor = returnFrom?.Stats.GetOrZero(CharacterStat.ExternalDoorInstance) ?? 0;
                if (sharedPlayfield > 0 && sharedPlayfield != destinationId && sharedDoor != 0)
                {
                    returnPlayfield = sharedPlayfield;
                    returnDoor = sharedDoor;
                }
                else if (TryFindNearestDoor(source.Identity.Instance, subject.Position, out int door))
                {
                    returnPlayfield = source.Identity.Instance;
                    returnDoor = door;
                }
            }

            subject.Stats.Set(CharacterStat.ExternalPlayfieldInstance, returnPlayfield, StatDetail.Base, dirty: true);
            subject.Stats.Set(CharacterStat.ExternalDoorInstance, returnDoor, StatDetail.Base, dirty: true);
        }

        bool TryFindNearestDoor(int playfieldId, Vector3 position, out int doorInstance)
        {
            doorInstance = 0;
            var dynels = _gameData.GetPlayfieldGeometry(playfieldId).Dynels?.Dynels;
            if (dynels == null)
                return false;

            float best = float.MaxValue;
            foreach (var d in dynels)
            {
                if (d.IdentityType != (int)IdentityType.Door)
                    continue;

                float dx = d.Position.X - position.xf;
                float dy = d.Position.Y - position.yf;
                float dz = d.Position.Z - position.zf;
                float distanceSquared = dx * dx + dy * dy + dz * dz;
                if (distanceSquared < best)
                {
                    best = distanceSquared;
                    doorInstance = d.IdentityInstance;
                }
            }

            return doorInstance != 0;
        }
    }
}
