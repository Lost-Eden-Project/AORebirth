namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>What a player shows of its organization: the id (Clan stat, 0xDEAA identity) and the name.</summary>
    public sealed record PlayerOrganization(int Id, string Name);

    /// <summary>A character's membership as stored, read at login: the organization, its name, form and leader, the stored rank.</summary>
    public sealed record OrgMembershipRow(int OrganizationId, string Name, int GoverningForm, int LeaderId, int StoredRank);

    /// <summary>An org_organizations row.</summary>
    public sealed record OrgRow(
        int OrganizationId, string Name, int GoverningForm, int LeaderId, string Description, string Objective,
        string History, long Bank, long Version);

    /// <summary>An org_members row with the character's name.</summary>
    public sealed record OrgMemberRow(int CharacterId, string Name, int StoredRank, DateTime JoinedAtUtc);

    /// <summary>One member as the rules read it: <see cref="Rank"/> is the effective rank (<see cref="OrganizationRules.EffectiveRank"/>).</summary>
    public sealed record OrgMember(int CharacterId, string Name, int Rank, DateTime JoinedAtUtc);

    /// <summary>
    /// An organization at one <see cref="Version"/>, immutable: zones cache it and replace it whole with a newer version.
    /// A disbanded organization is a tombstone (no members) so a late reload of an older version cannot bring it back.
    /// </summary>
    public sealed class OrgState
    {
        OrgState(int id, string name, int form, int leaderId, string description, string objective, string history,
            long bank, long version, IReadOnlyDictionary<int, OrgMember> members, bool disbanded)
        {
            Id = id;
            Name = name;
            GoverningForm = form;
            LeaderId = leaderId;
            Description = description;
            Objective = objective;
            History = history;
            Bank = bank;
            Version = version;
            Members = members;
            Disbanded = disbanded;
        }

        public int Id { get; }
        public string Name { get; }
        public int GoverningForm { get; }
        public int LeaderId { get; }
        public string Description { get; }
        public string Objective { get; }
        public string History { get; }
        public long Bank { get; }
        public long Version { get; }
        public IReadOnlyDictionary<int, OrgMember> Members { get; }
        public bool Disbanded { get; }

        public string LeaderName => Members.TryGetValue(LeaderId, out OrgMember? leader) ? leader.Name : string.Empty;

        public bool IsLeader(int characterId) => !Disbanded && LeaderId == characterId;

        public OrgMember? Member(int characterId)
            => !Disbanded && Members.TryGetValue(characterId, out OrgMember? member) ? member : null;

        public OrgMember? MemberNamed(string name)
            => Disbanded ? null : Members.Values.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public static OrgState From(OrgRow row, IReadOnlyList<OrgMemberRow> members, OrganizationRules rules)
        {
            var map = new Dictionary<int, OrgMember>(members.Count);
            foreach (OrgMemberRow member in members)
                map[member.CharacterId] = new OrgMember(member.CharacterId, member.Name,
                    rules.EffectiveRank(row.GoverningForm, member.StoredRank, member.CharacterId == row.LeaderId),
                    member.JoinedAtUtc);
            return new OrgState(row.OrganizationId, row.Name, row.GoverningForm, row.LeaderId, row.Description,
                row.Objective, row.History, row.Bank, row.Version, map, disbanded: false);
        }

        public static OrgState Tombstone(int id, string name)
            => new(id, name, 0, 0, string.Empty, string.Empty, string.Empty, 0, long.MaxValue,
                new Dictionary<int, OrgMember>(), disbanded: true);
    }

    /// <summary>An open (or just closed) vote.</summary>
    public sealed record OrgVote(
        long VoteId, int OrganizationId, string Question, IReadOnlyList<string> Choices, int StartedBy,
        DateTime StartedAtUtc, DateTime OpensAtUtc, DateTime EndsAtUtc, DateTime? ClosedAtUtc, string? StopReason)
    {
        public const char ChoiceSeparator = '\n';

        public string ChoicesText => string.Join(" ", Choices.Select((choice, index) => (index + 1) + ":" + choice));

        public string ChoiceText(int choice) => choice >= 1 && choice <= Choices.Count ? choice + ":" + Choices[choice - 1] : string.Empty;
    }

    /// <summary>A vote's ballots counted: per choice (1-based), and how many members voted.</summary>
    public sealed record OrgVoteTally(OrgVote Vote, IReadOnlyDictionary<int, int> Counts, int Voters);
}
