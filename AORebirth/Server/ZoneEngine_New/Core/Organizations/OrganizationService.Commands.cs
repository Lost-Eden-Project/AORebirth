namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Teams;

    /// <summary>The /org commands. Each runs on the sender's tick, checks what the tick can, and leaves the rest to the transaction.</summary>
    public sealed partial class OrganizationService
    {
        /// <summary>One OrgClient message from <paramref name="player"/>'s own accepted session, on its tick.</summary>
        public void Handle(Player player, OrgClientMessage message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            if (!Rules.Enabled || player.Session == null)
                return;

            string args = ((message.IOrgClientMessage as OrgClientCommandArgsMessage)?.CommandArgs ?? string.Empty).Trim();
            switch (message.Command)
            {
                case OrgClientCommand.Info: Info(player, message.Target); break;
                case OrgClientCommand.Ranks: Ranks(player); break;
                case OrgClientCommand.Create: Create(player, args); break;
                case OrgClientCommand.Name: Rename(player, args); break;
                case OrgClientCommand.Description: ChangeText(player, OrgTextField.Description, args); break;
                case OrgClientCommand.Objective: ChangeText(player, OrgTextField.Objective, args); break;
                case OrgClientCommand.History: ChangeText(player, OrgTextField.History, args); break;
                case OrgClientCommand.GoverningForm: ChangeGoverningForm(player, args); break;
                case OrgClientCommand.Promote: Promote(player, message.Target, confirmedByClient: message.Unknown1 == 0); break;
                case OrgClientCommand.Demote: Demote(player, message.Target); break;
                case OrgClientCommand.Invite: Invite(player, message.Target); break;
                case OrgClientCommand.Join: Join(player, message.Target); break;
                case OrgClientCommand.Leave: Leave(player); break;
                // 0x0C carries the selected target, 0x0D the name (captures 20261031 / 20261101).
                case OrgClientCommand.Unknown2: Kick(player, message.Target.Instance, null); break;
                case OrgClientCommand.Kick: Kick(player, 0, args); break;
                case OrgClientCommand.Disband: Disband(player); break;
                case OrgClientCommand.Bank: BankView(player); break;
                case OrgClientCommand.BankAdd: BankAdd(player, args); break;
                case OrgClientCommand.BankRemove: BankRemove(player, args); break;
                case OrgClientCommand.StartVote: StartVote(player, args); break;
                case OrgClientCommand.VoteInfo: VoteInfo(player); break;
                case OrgClientCommand.Vote: Vote(player, args); break;
                case OrgClientCommand.StopVote: StopVote(player, args); break;
                default:
                    // Tax, debt, paying members, contracts and cities are not part of AORebirth's organizations.
                    if (player.Organization == null && message.Command is OrgClientCommand.Debt or OrgClientCommand.Tax)
                        Reply(player, OrgText.Org(OrgText.NotInOrganization));
                    break;
            }
        }

        /// <summary>The actor's organization as this instance shows it; replies "not in an organization" when it has none.</summary>
        bool RequireOrganization(Player player, out int organizationId)
        {
            organizationId = player.Organization?.Id ?? 0;
            if (organizationId > 0)
                return true;
            Reply(player, OrgText.Org(OrgText.NotInOrganization));
            return false;
        }

        // ------------------------------------------------------------------------------------------------ read-only

        void Info(Player player, Identity target)
        {
            Player subject = player;
            if (target.Instance != 0 && target.Instance != player.Identity.Instance
                && (target.Type != IdentityType.SimpleChar || !InSight(player, target.Instance, out subject)))
                return;

            if (subject.Organization is not { } organization)
            {
                Reply(player, OrgText.Org(ReferenceEquals(subject, player) ? OrgText.InfoSelfNoOrganization : OrgText.InfoTargetNoOrganization));
                return;
            }

            bool sameOrganization = player.Organization?.Id == organization.Id;
            int subjectId = subject.Identity.Instance;
            if (Cached(organization.Id) is { } state)
            {
                player.Session?.Send(InfoFor(player.Identity, state, subjectId, sameOrganization));
                return;
            }

            Run(player, () =>
            {
                var outcome = new Outcome();
                if (Refresh(organization.Id) is { Disbanded: false } loaded)
                    outcome.Actor.Add(InfoFor(player.Identity, loaded, subjectId, sameOrganization));
                return outcome;
            });
        }

        void Ranks(Player player)
        {
            if (!RequireOrganization(player, out int organizationId) || Cached(organizationId) is not { } state)
                return;
            GoverningForm form = Rules.FormOf(state.GoverningForm);
            // Captured layout: the reference, then the ranks as plain text and a line feed.
            Reply(player, OrgText.Org(OrgText.RanksHeader, form.Name) + string.Join(", ", form.Ranks) + "\n");
        }

        // ------------------------------------------------------------------------------------------------ leader edits

        void Rename(Player player, string name)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (Cached(organizationId) is { } cached && !cached.IsLeader(player.Identity.Instance))
            {
                Reply(player, OrgText.Org(OrgText.NotLeaderName));
                return;
            }

            if (NameError(name) is int error)
            {
                ReplyFeedback(player, OrgText.DialogCategory, error);
                return;
            }

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId))
                    return ActorLine(player, OrgText.Org(OrgText.NotLeaderName));
                if (!tx.TrySetName(name))
                    return ActorFeedback(player, OrgText.DialogCategory, OrgText.NameTaken);
                return new Outcome { InfoUpdated = true };
            }));
        }

        void ChangeText(Player player, OrgTextField field, string text)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            int refusal = field switch
            {
                OrgTextField.Description => OrgText.NotLeaderDescription,
                OrgTextField.Objective => OrgText.NotLeaderObjective,
                _ => OrgText.NotLeaderHistory
            };
            if (Cached(organizationId) is { } cached && !cached.IsLeader(player.Identity.Instance))
            {
                Reply(player, OrgText.Org(refusal));
                return;
            }

            string value = OrgText.Printable(text, Rules.MaxTextLength);
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId))
                    return ActorLine(player, OrgText.Org(refusal));
                tx.SetText(field, value);
                return new Outcome { InfoUpdated = true };
            }));
        }

        void ChangeGoverningForm(Player player, string name)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (Cached(organizationId) is { } cached && !cached.IsLeader(player.Identity.Instance))
            {
                Reply(player, OrgText.Org(OrgText.NotLeaderGoverningForm));
                return;
            }

            GoverningForm? chosen = Rules.Forms.Values.FirstOrDefault(form => string.Equals(form.Name, name, StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
            {
                // No argument or an unknown form: retail lists the forms (the reference, the names, a full stop and a line feed).
                Reply(player, OrgText.Org(OrgText.InvalidGoverningForm) + string.Join(", ", Rules.FormListOrder.Select(form => form.Name)) + ".\n");
                return;
            }

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId))
                    return ActorLine(player, OrgText.Org(OrgText.NotLeaderGoverningForm));
                // Applying the current form again is allowed: it rebuilds the hierarchy (declared retail rule).
                tx.SetGoverningForm(chosen.Id, me.CharacterId, chosen.SingleRank ? 0 : chosen.LowestRank);
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.GoverningFormChanged));
                outcome.OrgMessages.Add(OrgText.Org(OrgText.GoverningFormNotice, me.Name, chosen.Name));
                return outcome;
            }));
        }

        // ------------------------------------------------------------------------------------------------ ranks

        void Promote(Player player, Identity target, bool confirmedByClient)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (target.Type != IdentityType.SimpleChar || !InSight(player, target.Instance, out Player targetPlayer))
            {
                Reply(player, OrgText.Org(OrgText.PromoteNeedsMember));
                return;
            }

            int targetId = targetPlayer.Identity.Instance;
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (state.Member(targetId) is not { } member)
                    return ActorLine(player, OrgText.Org(OrgText.PromoteNeedsMember));
                GoverningForm form = Rules.FormOf(state.GoverningForm);

                // The leader promoting someone to the top rank hands over the leadership (retail asks first with a
                // client dialog whose packet was not captured: here the leader confirms by promoting again in time).
                if (state.IsLeader(me.CharacterId) && (form.SingleRank || member.Rank == 1))
                {
                    if (!confirmedByClient && !TakeTransfer(me.CharacterId, state.Id, targetId))
                    {
                        _transfers[me.CharacterId] = new PendingTransfer(state.Id, targetId, DateTime.UtcNow + Rules.LeadershipConfirmWindow);
                        return ActorLine(player, OrgText.Ref(OrgText.DialogCategory, OrgText.LeadershipTransferQuestion, member.Name) + " "
                            + string.Format(CultureInfo.InvariantCulture, Rules.LeadershipTransferConfirmText, member.Name,
                                (int)Rules.LeadershipConfirmWindow.TotalSeconds));
                    }

                    tx.SetLeader(targetId);
                    tx.SetRank(targetId, 0);
                    tx.SetRank(me.CharacterId, form.SingleRank ? 0 : 1);
                    var transferred = new Outcome();
                    transferred.OrgMessages.Add(OrgText.Org(OrgText.LeadershipGiven, member.Name));
                    transferred.Notices.Add((targetId, OrgText.Ref(OrgText.FeedbackCategory, OrgText.LeadershipGivenToYou, me.Name)));
                    return transferred;
                }

                int rank = member.Rank - 1;
                if (form.SingleRank || rank <= me.Rank || rank < 0)
                    return ActorLine(player, OrgText.Org(OrgText.PromoteTooHigh));

                tx.SetRank(targetId, rank);
                string rankName = form.RankName(rank);
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.Promoted, member.Name, rankName));
                outcome.Notices.Add((targetId, OrgText.Org(OrgText.YouWerePromoted, me.Name, rankName)));
                return outcome;
            }));
        }

        bool TakeTransfer(int leaderId, int organizationId, int targetId)
            => _transfers.TryGetValue(leaderId, out PendingTransfer? pending)
               && pending.OrganizationId == organizationId && pending.TargetId == targetId && pending.ExpiresAtUtc > DateTime.UtcNow
               && _transfers.TryRemove(leaderId, out _);

        void Demote(Player player, Identity target)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (target.Type != IdentityType.SimpleChar || !InSight(player, target.Instance, out Player targetPlayer))
            {
                Reply(player, OrgText.Org(OrgText.DemoteNeedsMember));
                return;
            }

            int targetId = targetPlayer.Identity.Instance;
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (state.Member(targetId) is not { } member)
                    return ActorLine(player, OrgText.Org(OrgText.DemoteNeedsMember));
                GoverningForm form = Rules.FormOf(state.GoverningForm);
                if (form.SingleRank)
                    return ActorLine(player, OrgText.Org(OrgText.DemoteFilledUp, member.Name));
                if (state.IsLeader(targetId) || member.Rank <= me.Rank)
                    return ActorLine(player, OrgText.Org(OrgText.DemoteNotBelow));
                int rank = member.Rank + 1;
                // There is no rank below the lowest: retail's standard "filled up" message.
                if (rank > form.LowestRank)
                    return ActorLine(player, OrgText.Org(OrgText.DemoteFilledUp, member.Name));

                tx.SetRank(targetId, rank);
                string rankName = form.RankName(rank);
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.Demoted, member.Name, rankName));
                outcome.Notices.Add((targetId, OrgText.Org(OrgText.YouWereDemoted, me.Name, rankName)));
                return outcome;
            }));
        }

        // ------------------------------------------------------------------------------------------------ membership

        void Invite(Player player, Identity target)
        {
            if (!RequireOrganization(player, out int organizationId) || Cached(organizationId) is not { } state)
                return;
            if (state.Member(player.Identity.Instance) is not { } me)
            {
                Reply(player, OrgText.Org(OrgText.NotInOrganization));
                return;
            }

            if (target.Type != IdentityType.SimpleChar || !InSight(player, target.Instance, out Player invitee))
            {
                Reply(player, OrgText.Org(OrgText.InviteNeedsTarget));
                return;
            }

            if (!Rules.CanRecruit(state.GoverningForm, me.Rank, state.IsLeader(me.CharacterId)))
            {
                Reply(player, OrgText.Org(OrgText.InviteRankTooLow));
                return;
            }

            int side = player.Stats.GetOrZero(CharacterStat.Side, StatDetail.Base);
            if (invitee.Stats.GetOrZero(CharacterStat.Side, StatDetail.Base) != side)
            {
                Reply(player, OrgText.Org(OrgText.InviteOtherSide));
                return;
            }

            if (invitee.Organization != null)
            {
                Reply(player, OrgText.Org(OrgText.AlreadyInOrganization, invitee.Name ?? string.Empty));
                return;
            }

            // The inviter sees nothing; refusing the dialog sends nothing either (retail). The join re-checks everything.
            _invitations[invitee.Identity.Instance] = new Invitation(state.Id, me.CharacterId, side, DateTime.UtcNow + Rules.InvitationLifetime);
            if (invitee.Playfield is { } playfield)
                invitee.Session?.Send(OrgWire.Invite(invitee.Identity, playfield.ClientPlayfieldId, state.Id, state.Name));
        }

        void Join(Player player, Identity target)
        {
            int joinerId = player.Identity.Instance;
            // Only the invitation this instance sent counts; anything else from the client is ignored.
            if (target.Type != IdentityType.Organization || !_invitations.TryGetValue(joinerId, out Invitation? invitation)
                || invitation.OrganizationId != target.Instance || !_invitations.TryRemove(joinerId, out _))
                return;
            if (invitation.ExpiresAtUtc <= DateTime.UtcNow || player.Organization != null
                || player.Stats.GetOrZero(CharacterStat.Side, StatDetail.Base) != invitation.Side)
            {
                Reply(player, OrgText.Org(OrgText.CouldNotJoin));
                return;
            }

            string joinerName = player.Name ?? string.Empty;
            Run(player, () => _store.Transact(invitation.OrganizationId, tx =>
            {
                if (tx.Row == null)
                    return ActorLine(player, OrgText.Org(OrgText.CouldNotJoin));
                OrgState state = OrgState.From(tx.Row, tx.Members, Rules);
                GoverningForm form = Rules.FormOf(state.GoverningForm);
                if (state.Member(invitation.InviterId) is not { } inviter
                    || !Rules.CanRecruit(state.GoverningForm, inviter.Rank, state.IsLeader(inviter.CharacterId))
                    || !tx.AddMember(joinerId, form.SingleRank ? 0 : form.LowestRank))
                    return ActorLine(player, OrgText.Org(OrgText.CouldNotJoin));

                var after = tx.Reload();
                if (after is not { } row)
                    return ActorLine(player, OrgText.Org(OrgText.CouldNotJoin));
                OrgState joined = OrgState.From(row.Row, row.Members, Rules);
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.Joined));
                outcome.Actor.Add(InfoFor(player.Identity, joined, joinerId, showRank: true));
                outcome.OrganizationId = state.Id;
                outcome.Previous = state;
                outcome.State = joined;
                outcome.OrgMessages.Add(OrgText.Org(OrgText.MemberJoined, inviter.Name, joinerName));
                return outcome;
            }));
        }

        void Leave(Player player)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (state.IsLeader(me.CharacterId))
                {
                    if (state.Members.Count > 1)
                        return ActorLine(player, OrgText.Org(OrgText.LeaderCannotLeave));
                    // The last member leaving ends the organization.
                    return DisbandIn(tx, player, me);
                }

                tx.RemoveMember(me.CharacterId);
                Outcome outcome = ActorFeedback(player, OrgText.OrgCategory, OrgText.YouLeft);
                outcome.OrgMessages.Add(OrgText.Org(OrgText.MemberLeft, me.Name));
                return outcome;
            }));
        }

        /// <summary>
        /// A member whose side changed leaves its organization: one organization holds one side (invite, join and create
        /// all check it; retail 508/247974149 "All players must have the same side in your organization"). A leader with
        /// other members first hands the lead to the highest ranked of them (longest serving on a tie); a sole member's
        /// organization ends, as its last member leaving ends it. Unlike a command this is never dropped while another
        /// of the player's commands runs: it waits for that one, then runs in the organization's transaction.
        /// </summary>
        internal void LeaveForSideChange(Player player)
        {
            if (_disposed || !Rules.Enabled || player.Organization is not { } organization)
                return;
            int organizationId = organization.Id;
            int id = player.Identity.Instance;
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; !_busy.TryAdd(id, 0); attempt++)
                {
                    if (_disposed || attempt >= SideChangeBusyAttempts)
                    {
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Organization side-change leave skipped character={0} org={1}: a command stayed busy", id, organizationId));
                        return;
                    }

                    await Task.Delay(SideChangeBusyDelay).ConfigureAwait(false);
                }

                Outcome? outcome = null;
                try
                {
                    outcome = _store.Transact(organizationId, tx =>
                    {
                        // Not a member any more (left or kicked meanwhile): nothing to say.
                        if (tx.Row == null)
                            return new Outcome();
                        OrgState state = OrgState.From(tx.Row, tx.Members, Rules);
                        if (state.Member(id) is not { } me)
                            return new Outcome();

                        Outcome left = SideChangeLeaveIn(tx, player, state, me);
                        left.OrganizationId = organizationId;
                        if (tx.Changed)
                        {
                            var after = tx.Reload();
                            left.Previous = state;
                            left.State = after is { } row ? OrgState.From(row.Row, row.Members, Rules) : OrgState.Tombstone(organizationId, state.Name);
                        }

                        return left;
                    });
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Organization side-change leave failed character=" + id);
                }
                finally
                {
                    _busy.TryRemove(id, out _);
                }

                if (outcome != null)
                    Deliver(player, outcome);
            });
        }

        const int SideChangeBusyAttempts = 100;
        static readonly TimeSpan SideChangeBusyDelay = TimeSpan.FromMilliseconds(100);

        static Outcome SideChangeLeaveIn(OrgTransaction tx, Player player, OrgState state, OrgMember me)
        {
            if (state.IsLeader(me.CharacterId))
            {
                OrgMember? heir = state.Members.Values
                    .Where(member => member.CharacterId != me.CharacterId)
                    .OrderBy(member => member.Rank)
                    .ThenBy(member => member.JoinedAtUtc)
                    .ThenBy(member => member.CharacterId)
                    .FirstOrDefault();
                if (heir == null)
                    return DisbandIn(tx, player, me);

                tx.SetLeader(heir.CharacterId);
                tx.SetRank(heir.CharacterId, 0);
                tx.RemoveMember(me.CharacterId);
                Outcome handedOver = ActorFeedback(player, OrgText.OrgCategory, OrgText.YouLeft);
                handedOver.OrgMessages.Add(OrgText.Org(OrgText.MemberLeft, me.Name));
                handedOver.OrgMessages.Add(OrgText.Org(OrgText.LeadershipGiven, heir.Name));
                handedOver.Notices.Add((heir.CharacterId, OrgText.Ref(OrgText.FeedbackCategory, OrgText.LeadershipGivenToYou, me.Name)));
                return handedOver;
            }

            tx.RemoveMember(me.CharacterId);
            Outcome outcome = ActorFeedback(player, OrgText.OrgCategory, OrgText.YouLeft);
            outcome.OrgMessages.Add(OrgText.Org(OrgText.MemberLeft, me.Name));
            return outcome;
        }

        /// <param name="targetId">The selected character (0x0C), or 0 when kicking by <paramref name="name"/> (0x0D).</param>
        void Kick(Player player, int targetId, string? name)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            string targetName = string.Empty;
            if (name != null)
            {
                // Retail matches the exact case; character names always start upper case, so the first letter is raised.
                targetName = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
                if (targetName.Length is 0 or > 32 || !targetName.All(char.IsAsciiLetterOrDigit))
                {
                    Reply(player, OrgText.Org(OrgText.InvalidCharacterName));
                    return;
                }
            }
            else if (targetId <= 0)
                return;
            else if (_playfields.Value.FindPlayer(targetId, out Player selected))
                targetName = selected.Name ?? string.Empty;

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                OrgMember? member = name != null ? state.MemberNamed(targetName) : state.Member(targetId);
                if (member == null)
                {
                    if (name == null)
                        return ActorLine(player, OrgText.Org(OrgText.NotAMember, targetName));
                    return tx.FindCharacter(targetName) is { } character
                        ? ActorLine(player, OrgText.Org(OrgText.NotAMember, character.Name))
                        : ActorLine(player, OrgText.Org(OrgText.NoActiveCharacter, targetName));
                }

                if (member.CharacterId == me.CharacterId)
                    return ActorLine(player, OrgText.Org(OrgText.KickSelf));
                GoverningForm form = Rules.FormOf(state.GoverningForm);
                bool allowed = !state.IsLeader(member.CharacterId)
                    && (state.IsLeader(me.CharacterId) || form.SingleRank
                        || (Rules.CanRecruit(state.GoverningForm, me.Rank, false) && me.Rank < member.Rank));
                if (!allowed)
                    return ActorLine(player, OrgText.Org(OrgText.KickRankTooLow));

                tx.RemoveMember(member.CharacterId);
                var outcome = new Outcome();
                outcome.Notices.Add((member.CharacterId, OrgText.Ref(OrgText.FeedbackCategory, OrgText.YouWereKicked, me.Name)));
                outcome.OrgMessages.Add(OrgText.Org(OrgText.MemberKicked, me.Name, member.Name));
                return outcome;
            }));
        }

        void Disband(Player player)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            bool gm = player.IsGm;
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
                state.IsLeader(me.CharacterId) || gm
                    ? DisbandIn(tx, player, me)
                    : ActorLine(player, OrgText.Org(OrgText.NotLeaderDisband))));
        }

        /// <summary>Deletes the organization (its bank is lost, as retail warns). Members hear it before they leave the channel.</summary>
        static Outcome DisbandIn(OrgTransaction tx, Player actor, OrgMember me)
        {
            tx.Delete();
            Outcome outcome = ActorLine(actor, OrgText.Org(OrgText.Disbanded));
            outcome.OrgMessages.Add(OrgText.Org(OrgText.DisbandNotice, me.Name));
            outcome.OrgMessagesFirst = true;
            return outcome;
        }

        // ------------------------------------------------------------------------------------------------ create

        void Create(Player player, string name)
        {
            if (player.Organization != null)
            {
                ReplyFeedback(player, OrgText.DialogCategory, OrgText.CreateRequirements);
                return;
            }

            if (NameError(name) is int error)
            {
                ReplyFeedback(player, OrgText.DialogCategory, error);
                return;
            }

            int leaderId = player.Identity.Instance;
            IReadOnlyList<int>? team = CreatingTeam(player);
            if (team == null)
            {
                ReplyFeedback(player, OrgText.DialogCategory, OrgText.CreateRequirements);
                return;
            }

            GoverningForm form = Rules.FormOf(Rules.DefaultForm);
            var members = team.Select(id => (id, id == leaderId ? 0 : form.SingleRank ? 0 : form.LowestRank)).ToArray();
            Run(player, () =>
            {
                OrgCreateResult result = _store.Create(name, form.Id, leaderId, members);
                switch (result.Status)
                {
                    case OrgCreateStatus.NameTaken:
                        return ActorFeedback(player, OrgText.DialogCategory, OrgText.NameTaken);
                    case OrgCreateStatus.MemberAlreadyInOrganization:
                        return ActorFeedback(player, OrgText.DialogCategory, OrgText.CreateRequirements);
                }

                if (_store.Load(result.OrganizationId) is not { } loaded)
                    return new Outcome();
                OrgState state = OrgState.From(loaded.Row, loaded.Members, Rules);
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Organization created org={0} name={1} leader={2} members={3}",
                    state.Id, state.Name, leaderId, state.Members.Count));
                Outcome outcome = ActorFeedback(player, OrgText.DialogCategory, OrgText.Created);
                outcome.Actor.Add(InfoFor(player.Identity, state, leaderId, showRank: true));
                outcome.OrganizationId = state.Id;
                outcome.State = state;
                return outcome;
            });
        }

        /// <summary>
        /// The characters a new organization starts with: the leader's full team (Create.TeamSize members, the actor
        /// leading, one side, every member in play in the leader's zone, nobody in an organization; the store checks
        /// again), or the GM alone when the rules let a GM found one without a team. Null when neither applies.
        /// </summary>
        IReadOnlyList<int>? CreatingTeam(Player player)
        {
            int leaderId = player.Identity.Instance;
            int side = player.Stats.GetOrZero(CharacterStat.Side, StatDetail.Base);
            var roster = _playfields.Value.Teams.GetRoster(player);
            if (roster is { } team && !team.IsRaid && team.LeaderId == leaderId && team.Members.Count == Rules.CreateTeamSize
                && team.Members.All(m => m.Side == side && m.Name.Length > 0)
                && team.Members.All(m => InLeadersZone(player, m.CharacterId, out Player member) && member.Organization == null))
                return team.Members.Select(m => m.CharacterId).ToArray();
            if (player.IsGm && Rules.GmCreateWithoutTeam && (roster == null || roster.Value.Members.Count <= 1))
                return [leaderId];
            return null;
        }

        /// <summary>
        /// The team member <paramref name="characterId"/> when it is in play on this instance on the leader's playfield.
        /// A member on another zone or instance is not standing with the leader, so the team cannot found an organization.
        /// </summary>
        bool InLeadersZone(Player leader, int characterId, out Player member)
        {
            if (_playfields.Value.FindPlayer(characterId, out member) && member.Session != null
                && member.Playfield != null && ReferenceEquals(member.Playfield, leader.Playfield))
                return true;
            member = null!;
            return false;
        }

        /// <summary>Retail's name refusals (Feedback 501), in the order the captures show them; null for a valid name.</summary>
        int? NameError(string name)
        {
            if (name.Length < Rules.NameMinLength || name.Length > Rules.NameMaxLength)
                return OrgText.NameLength;
            foreach (char c in name)
                if (!char.IsAsciiLetterOrDigit(c) && Rules.NamePunctuation.IndexOf(c) < 0)
                    return OrgText.NameInvalidCharacter;
            if (name.Count(char.IsAsciiLetter) < Rules.NameMinLetters)
                return OrgText.NameLetters;
            if (char.IsAsciiLetterLower(name[0]))
                return OrgText.NameLowercase;
            if (!char.IsAsciiLetterUpper(name[0]) || name.Contains("  ", StringComparison.Ordinal) || name[^1] == ' ')
                return OrgText.NameInvalidCharacter;
            return null;
        }
    }
}
