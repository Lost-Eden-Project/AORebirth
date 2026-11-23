namespace ZoneEngine_New.Core.Teams
{
    using System;
    using System.Globalization;

    using AORebirth.Core.GameData;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.World;
    using ZoneEngine_New.Core.WorldSimulation;

    using IdentityType = LostEden.Protocol.GameData.IdentityType;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// The Engineer summons: SummonPlayer (Summon Player 154914) brings the teammate the caster has selected when the cast
    /// finishes, SummonTeamMates (Summon Team Members 154913) brings every other member of the caster's team, to the
    /// caster's position. Nothing is judged when the cast starts: the destination is judged when the nano lands, on the
    /// caster's tick, and each member on its own tick as the move begins (still a teammate, alive, not in a fight, not
    /// already zoning).
    /// Both ends must allow summons (<see cref="AllowsSummons"/>): no summon reaches into or out of the Shadowlands or a
    /// Gameplay.json <see cref="GameplaySettings.NoSummonPlayfields"/> playfield. A member on another zone instance is
    /// reached through World, and that instance judges the member's end.
    /// </summary>
    public sealed class SummonService
    {
        /// <summary>
        /// A summon for a character another zone instance runs: caster id, destination playfield, x, y, z, exit-return
        /// playfield, exit-return door.
        /// </summary>
        public const string RemoteCommand = "nano.summon";

        /// <summary>"You need to be in a team to perform this action!" (category 110).</summary>
        internal const int NeedTeam = 147918301;

        /// <summary>"You need to select a team member first!" (category 110).</summary>
        internal const int SelectTeamMember = 116451874;

        /// <summary>"The target must be in your team!" (category 110).</summary>
        internal const int TargetMustBeInTeam = 65806413;

        /// <summary>"You are not allowed to teleport players to this playfield." (category 110).</summary>
        internal const int NotAllowedToTeleportHere = 184106293;

        /// <summary>"%s cannot be summoned because it is in a fight." (category 110, FormatFeedback with the name).</summary>
        internal const int CannotSummonInFight = 107353316;

        readonly Lazy<PlayfieldManager> _playfields;
        readonly WorldCommands? _commands;

        public SummonService(Lazy<PlayfieldManager> playfields, WorldCommands? commands = null)
        {
            _playfields = playfields ?? throw new ArgumentNullException(nameof(playfields));
            _commands = commands;
            _commands?.Register(RemoteCommand, OnSummonedFromElsewhere);
        }

        /// <summary>Where a summon lands and the way out the summoned character gets there.</summary>
        internal readonly record struct SummonOrder(int CasterId, int PlayfieldId, Vector3 Landing, int ReturnPlayfield, int ReturnDoor);

        /// <summary>
        /// SummonPlayer as it lands, on the caster's tick: the caster's selection now (the last one made before the cast
        /// finished) must be a member of its team.
        /// </summary>
        internal static bool SummonSelected(Player caster)
        {
            if (caster.Playfield is not Playfield here || caster.Session == null)
                return false;

            TeamSnapshot? team = here.GetService<TeamService>()?.GetImmediateTeam(caster);
            if (team == null)
                return Refuse(caster, NeedTeam);

            LostEden.Protocol.GameData.Identity selected = caster.Target;
            if (selected.Type != IdentityType.SimpleChar || selected.Instance == 0
                || selected.Instance == caster.Identity.Instance)
                return Refuse(caster, SelectTeamMember);
            if (!Contains(team, selected.Instance))
                return Refuse(caster, TargetMustBeInTeam);
            if (!AllowsSummons(here))
                return Refuse(caster, NotAllowedToTeleportHere);

            return Send(here, OrderFrom(caster, here), selected.Instance);
        }

        /// <summary>SummonTeamMates as it lands, on the caster's tick: every other member of the caster's team.</summary>
        internal static bool SummonTeam(Player caster)
        {
            if (caster.Playfield is not Playfield here || caster.Session == null)
                return false;

            TeamSnapshot? team = here.GetService<TeamService>()?.GetImmediateTeam(caster);
            if (team == null)
                return Refuse(caster, NeedTeam);
            if (!AllowsSummons(here))
                return Refuse(caster, NotAllowedToTeleportHere);

            SummonOrder order = OrderFrom(caster, here);
            for (int i = 0; i < team.MemberIds.Count; i++)
            {
                int memberId = team.MemberIds[i];
                if (memberId != caster.Identity.Instance)
                    Send(here, order, memberId);
            }

            return true;
        }

        /// <summary>
        /// Whether a summon may start or end on <paramref name="playfield"/>: never in the Shadowlands, nor on a playfield
        /// Gameplay.json lists. Checked for the caster's playfield and for each summoned member's.
        /// </summary>
        internal static bool AllowsSummons(Playfield playfield)
        {
            if (IsShadowlands(playfield))
                return false;

            var blocked = playfield.GetService<GameplaySettings>()?.NoSummonPlayfields;
            if (blocked == null)
                return true;

            for (int i = 0; i < blocked.Count; i++)
            {
                if (blocked[i] == playfield.Identity.Instance || blocked[i] == playfield.ResourceId)
                    return false;
            }

            return true;
        }

        static bool IsShadowlands(Playfield playfield)
            => PlayfieldEntryFlags.ExpansionPlayfield(playfield.MetaData) == 1;

        static bool Contains(TeamSnapshot team, int characterId)
        {
            for (int i = 0; i < team.MemberIds.Count; i++)
            {
                if (team.MemberIds[i] == characterId)
                    return true;
            }

            return false;
        }

        static bool Refuse(Player caster, int messageId)
        {
            ClientFeedback.Send(caster, messageId);
            return false;
        }

        /// <summary>
        /// The caster's spot. Indoors (a dungeon, a proxy instance, a playfield without metadata) the summoned character
        /// gets the caster's own way out, as a GM summon into an interior does; outdoors there is none to keep.
        /// </summary>
        static SummonOrder OrderFrom(Player caster, Playfield here)
        {
            int returnPlayfield = 0;
            int returnDoor = 0;
            if (here.MetaData?.IsIndoor ?? true)
            {
                returnPlayfield = caster.Stats.GetOrZero(CharacterStat.ExternalPlayfieldInstance);
                returnDoor = caster.Stats.GetOrZero(CharacterStat.ExternalDoorInstance);
                if (returnPlayfield == here.Identity.Instance || returnDoor == 0)
                    returnPlayfield = returnDoor = 0;
            }

            // On the floor under the caster: a flying caster does not leave its teammates in the air.
            Vector3 landing = here.SnapFeetToFloor(new Vector3(caster.Position.xf, caster.Position.yf, caster.Position.zf));
            return new SummonOrder(caster.Identity.Instance, here.Identity.Instance, landing, returnPlayfield, returnDoor);
        }

        /// <summary>
        /// Hands the summon to the member's own tick: here when this instance runs it, else through World. False when the
        /// member is reachable neither way.
        /// </summary>
        static bool Send(Playfield here, SummonOrder order, int memberId)
        {
            PlayfieldManager? manager = here.GetService<PlayfieldManager>();
            if (manager != null && manager.FindPlayer(memberId, out Player member))
            {
                if (member.Playfield is not Playfield at)
                    return false;
                at.DispatchPlayerProjection(member, () => Arrive(member, at, order, here, manager));
                return true;
            }

            SummonService? service = here.GetService<SummonService>();
            if (service?._commands == null)
                return false;

            _ = service._commands.ToCharacterAsync(memberId, RemoteCommand,
                order.CasterId.ToString(CultureInfo.InvariantCulture),
                order.PlayfieldId.ToString(CultureInfo.InvariantCulture),
                order.Landing.xf.ToString(CultureInfo.InvariantCulture),
                order.Landing.yf.ToString(CultureInfo.InvariantCulture),
                order.Landing.zf.ToString(CultureInfo.InvariantCulture),
                order.ReturnPlayfield.ToString(CultureInfo.InvariantCulture),
                order.ReturnDoor.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>A summon from a caster on another zone instance, for a character this one runs.</summary>
        void OnSummonedFromElsewhere(AORebirth.World.Contracts.V1.InstanceCommand command)
        {
            if (command.Arguments.Count < 7
                || !int.TryParse(command.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int casterId)
                || !int.TryParse(command.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
                || !float.TryParse(command.Arguments[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(command.Arguments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(command.Arguments[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                || !int.TryParse(command.Arguments[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int returnPlayfield)
                || !int.TryParse(command.Arguments[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int returnDoor)
                || playfieldId <= 0)
                return;

            PlayfieldManager manager = _playfields.Value;
            if (!manager.FindPlayer(command.CharacterId, out Player member) || member.Playfield is not Playfield at)
                return;

            var order = new SummonOrder(casterId, playfieldId, new Vector3(x, y, z), returnPlayfield, returnDoor);
            at.DispatchPlayerProjection(member, () => Arrive(member, at, order, destination: null, manager));
        }

        /// <summary>
        /// On the member's tick: the summon still applies (same playfield as when it was sent, a teammate of the caster,
        /// alive, not zoning, on a playfield summons may leave, not fighting or being attacked), then the move. A member
        /// in a fight stays, and a caster on this instance is told so. <paramref name="destination"/> is the caster's
        /// playfield when this instance runs it; otherwise the playfield id is resolved here, possibly on another instance.
        /// </summary>
        static void Arrive(Player member, Playfield at, SummonOrder order, Playfield? destination, PlayfieldManager manager)
        {
            if (!ReferenceEquals(member.Playfield, at) || member.Session is not { IsTransferring: false } session
                || member.IsDead || !AllowsSummons(at))
                return;

            TeamSnapshot? team = at.GetService<TeamService>()?.GetImmediateTeam(member);
            if (team == null || !Contains(team, order.CasterId))
                return;

            // No summon out of a fight: it would be an escape from a losing one, PvP included.
            if (member.IsInCombat)
            {
                TellCaster(manager, order.CasterId, CannotSummonInFight, member.Name);
                return;
            }

            if (destination != null && ReferenceEquals(destination, at))
            {
                at.GetService<WorldSimulationAccess>()?.Instance?.ForgetCharacterTriggers(member.Identity.Instance);
                member.TeleportWithinPlayfield(order.Landing);
                return;
            }

            if (destination != null)
            {
                SetExitReturn(member, order);
                at.GetService<WorldSimulationAccess>()?.Instance?.ForgetCharacterTriggers(member.Identity.Instance);
                session.TransferToPlayfield(destination, order.Landing);
                return;
            }

            var remote = new RemoteArrival(order.Landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport,
                Prepare: () =>
                {
                    SetExitReturn(member, order);
                    return true;
                });
            manager.WithPlayfield(order.PlayfieldId, member, loaded =>
            {
                if (!ReferenceEquals(member.Playfield, at) || member.Session is not { IsTransferring: false } current)
                    return;
                SetExitReturn(member, order);
                at.GetService<WorldSimulationAccess>()?.Instance?.ForgetCharacterTriggers(member.Identity.Instance);
                if (ReferenceEquals(loaded, at))
                    member.TeleportWithinPlayfield(order.Landing);
                else
                    current.TransferToPlayfield(loaded, order.Landing);
            }, remote);
        }

        /// <summary>Formatted feedback to the caster on its own tick, when this instance runs it.</summary>
        static void TellCaster(PlayfieldManager manager, int casterId, int messageId, string name)
        {
            if (!manager.FindPlayer(casterId, out Player caster) || caster.Playfield is not Playfield casterAt)
                return;
            casterAt.DispatchPlayerProjection(caster, () => ClientFeedback.SendFormatted(caster, messageId, name));
        }

        static void SetExitReturn(Player member, SummonOrder order)
        {
            member.Stats.Set(CharacterStat.ExternalPlayfieldInstance, order.ReturnPlayfield, StatDetail.Base, dirty: true);
            member.Stats.Set(CharacterStat.ExternalDoorInstance, order.ReturnDoor, StatDetail.Base, dirty: true);
        }
    }
}
