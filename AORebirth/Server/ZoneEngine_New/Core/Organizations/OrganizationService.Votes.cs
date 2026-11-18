namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// Organization votes: <c>/org startvote "question" minutes 1:a 2:b ...</c>, <c>/org vote info</c>, <c>/org vote n</c>,
    /// <c>/org stopvote [reason]</c>. A vote lives in org_votes until its end time; World's duty holder closes it and
    /// posts the results. Times come from the database clock, which every instance shares.
    /// </summary>
    public sealed partial class OrganizationService
    {
        readonly record struct VoteRequest(string Question, int Minutes, IReadOnlyList<string> Choices);

        void StartVote(Player player, string args)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            bool gm = player.IsGm;
            if (Cached(organizationId) is { } cached && !cached.IsLeader(player.Identity.Instance) && !gm)
            {
                Reply(player, OrgText.Org(OrgText.NotLeaderStartVote));
                return;
            }

            if (!TryParseVote(args, out VoteRequest request, out int error))
            {
                Reply(player, OrgText.Org(error));
                return;
            }

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId) && !gm)
                    return ActorLine(player, OrgText.Org(OrgText.NotLeaderStartVote));
                if (tx.OpenVote() != null)
                    return ActorLine(player, OrgText.Org(OrgText.VoteInProgress));
                DateTime now = tx.UtcNow();
                if (tx.LastVoteStartedAtUtc() is { } last)
                {
                    DateTime allowed = DateTime.SpecifyKind(last, DateTimeKind.Utc).AddMinutes(Rules.VoteMinMinutesBetween);
                    if (allowed > now)
                        return ActorLine(player, OrgText.Org(OrgText.VoteTooSoon, Rules.VoteMinMinutesBetween,
                            (int)Math.Ceiling((allowed - now).TotalMinutes)));
                }

                DateTime opens = now + Rules.VoteOpenDelay;
                tx.StartVote(request.Question, request.Choices, me.CharacterId, now, opens, opens.AddMinutes(request.Minutes));
                string choices = string.Join(" ", request.Choices.Select((choice, index) => (index + 1) + ":" + choice));
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.VoteMembersInformed) + OrgText.Org(OrgText.VoteBeginsSoon));
                outcome.OrgMessages.Add(OrgText.Org(OrgText.VoteNotice, request.Question, request.Minutes, choices));
                return outcome;
            }));
        }

        /// <summary>Parses <c>"question" minutes 1:a 2:b ...</c>; the error is the retail refusal for what is wrong.</summary>
        bool TryParseVote(string args, out VoteRequest request, out int error)
        {
            request = default;
            string text = args.Trim();
            int end = text.Length > 1 && text[0] == '"' ? text.IndexOf('"', 1) : -1;
            string question = end > 1 ? text[1..end].Trim() : string.Empty;
            if (question.Length == 0)
            {
                error = OrgText.VoteNeedsQuestion;
                return false;
            }

            string[] tokens = text[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (question.Length > Rules.VoteMaxQuestionLength || tokens.Length == 0
                || !int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            {
                error = OrgText.VoteUsage;
                return false;
            }

            if (minutes < Rules.VoteMinMinutes || minutes > Rules.VoteMaxMinutes)
            {
                error = OrgText.VoteDuration;
                return false;
            }

            int count = tokens.Length - 1;
            if (count < Rules.VoteMinChoices || count > Rules.VoteMaxChoices)
            {
                error = OrgText.VoteChoiceCount;
                return false;
            }

            var choices = new List<string>(count);
            for (int i = 1; i < tokens.Length; i++)
            {
                string prefix = i.ToString(CultureInfo.InvariantCulture) + ":";
                string choice = tokens[i].StartsWith(prefix, StringComparison.Ordinal) ? tokens[i][prefix.Length..] : string.Empty;
                if (choice.Length == 0 || choice.Length > Rules.VoteMaxChoiceLength || !choice.All(c => c is > ' ' and <= '~' and not ':'))
                {
                    error = OrgText.VoteChoiceFormat;
                    return false;
                }

                choices.Add(choice);
            }

            if (choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Count)
            {
                error = OrgText.VoteChoiceDuplicates;
                return false;
            }

            request = new VoteRequest(OrgText.Printable(question, Rules.VoteMaxQuestionLength), minutes, choices);
            error = 0;
            return true;
        }

        void VoteInfo(Player player)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            int characterId = player.Identity.Instance;
            Run(player, () =>
            {
                if (_store.LoadOpenVote(organizationId) is not { } vote)
                    return ActorLine(player, OrgText.Org(OrgText.NoVoteInProgress));
                OrgMember? me = Cached(organizationId)?.Member(characterId);
                string status;
                if (me != null && me.JoinedAtUtc > vote.StartedAtUtc)
                    status = OrgText.Org(OrgText.VoteJoinedAfterShort);
                else if (_store.LoadBallot(vote.VoteId, characterId) is int choice)
                    status = OrgText.Org(OrgText.VoteYouVotedFor, vote.ChoiceText(choice));
                else
                    status = OrgText.Org(OrgText.VoteNotVotedYet);
                int remaining = Math.Max(0, (int)Math.Ceiling((vote.EndsAtUtc - DateTime.UtcNow).TotalMinutes));
                // Assembled from the client's sentences in retail's order; the exact retail composition was not captured.
                return ActorLine(player, OrgText.Org(OrgText.VoteInfoHeader, vote.Question) + vote.ChoicesText
                    + OrgText.Org(OrgText.VoteStatus) + OrgText.Org(OrgText.VoteMinutesRemaining, remaining) + status);
            });
        }

        void Vote(Player player, string args)
        {
            if (string.Equals(args, "info", StringComparison.OrdinalIgnoreCase))
            {
                VoteInfo(player);
                return;
            }

            if (!RequireOrganization(player, out int organizationId))
                return;
            if (!int.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out int choice))
            {
                Reply(player, OrgText.Org(OrgText.VoteNotUnderstood));
                return;
            }

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (tx.OpenVote() is not { } vote)
                    return ActorLine(player, OrgText.Org(OrgText.NoVoteInProgress));
                DateTime now = tx.UtcNow();
                if (now < vote.OpensAtUtc)
                    return ActorLine(player, OrgText.Org(OrgText.VoteNotOpenYet));
                if (now >= vote.EndsAtUtc)
                    return ActorLine(player, OrgText.Org(OrgText.NoVoteInProgress));
                if (me.JoinedAtUtc > vote.StartedAtUtc)
                    return ActorLine(player, OrgText.Org(OrgText.VoteJoinedAfter));
                if (choice < 1 || choice > vote.Choices.Count)
                    return ActorLine(player, OrgText.Org(OrgText.VoteNumberTooHigh, args));
                if (!tx.CastBallot(vote.VoteId, me.CharacterId, choice))
                    return ActorLine(player, OrgText.Org(OrgText.VoteAlreadyCast));
                return ActorLine(player, OrgText.Org(OrgText.VoteCast, vote.ChoiceText(choice)));
            }));
        }

        void StopVote(Player player, string reason)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            bool gm = player.IsGm;
            string why = OrgText.Printable(reason, OrganizationRules.TextColumnLength);
            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId) && !gm)
                    return ActorLine(player, OrgText.Org(OrgText.NotLeaderStopVote));
                if (tx.OpenVote() is not { } vote || !tx.StopVote(vote.VoteId, why))
                    return ActorLine(player, OrgText.Org(OrgText.NoVoteInProgress));
                Outcome outcome = ActorLine(player, OrgText.Org(OrgText.VoteStopped));
                outcome.OrgMessages.Add(OrgText.Org(OrgText.VoteStoppedNotice, why));
                return outcome;
            }));
        }

        /// <summary>
        /// The results line: the question, how many voted, then each place ("First place ( 3 votes ):") with the choices
        /// that reached it. Assembled from the client's sentences; the exact retail composition was not captured.
        /// </summary>
        static string VoteResultsText(OrgVoteTally tally)
        {
            var text = new StringBuilder(OrgText.Org(OrgText.VoteResults, tally.Vote.Question));
            if (tally.Voters == 0)
                return text.Append(OrgText.Org(OrgText.VoteNoVotes)).ToString();

            text.Append(OrgText.Org(OrgText.VoteMembersVoted, tally.Voters));
            int place = 0;
            foreach (IGrouping<int, KeyValuePair<int, int>> group in tally.Counts.Where(pair => pair.Value > 0)
                         .GroupBy(pair => pair.Value).OrderByDescending(group => group.Key))
            {
                if (place >= OrganizationRules.MaxVotePlaces)
                    break;
                int[] choices = group.Select(pair => pair.Key).Order().ToArray();
                text.Append(' ')
                    .Append(OrgText.Org(choices.Length > 1 ? OrgText.VotePlaceTied : OrgText.VotePlace,
                        new OrgText.TextRef(OrgText.OrgCategory, OrgText.FirstPlace + place), group.Key))
                    .Append(' ')
                    .Append(string.Join(" ", choices.Select(tally.Vote.ChoiceText)));
                place++;
            }

            return text.ToString();
        }
    }
}
