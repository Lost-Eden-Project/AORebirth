namespace ZoneEngine_New.Core.Teams
{
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;
    using TeamMember = LostEden.Protocol.Messages.N3Messages.FullCharacterMessage.TeamMember;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    public sealed partial class TeamService
    {
        const int RaidTeamLists = FullCharacterMessage.RaidTeamCount;
        const int RaidGroupSize = 6;

        /// <summary>The rows a FullCharacter listed for one session: member → raid list (-1 in an ordinary team).</summary>
        private sealed record FullCharacterRoster(IZoneSession Session, int TeamId, bool IsRaid, Dictionary<int, int> Members);

        /// <summary>
        /// FullCharacter's team block (Gamecode.dll FUN_10073ba7), read every time the client enters a playfield. Live sends
        /// it filled (capture 2026-10-27T15:40:05Z: flags 3, the leader 50000:71A9545B, raid team 0, then six lists with
        /// the two members in the first), and the client rebuilds its team and raid windows from it; sent empty, a zone
        /// dropped the raid. Fills <paramref name="message"/>'s TeamFlags (bit 0 in a team, bit 1 a raid), TeamLeader,
        /// Teams and RaidGroupIndex; they stay empty when the viewer is not in a team.
        /// </summary>
        public void FillFullCharacterTeam(Player viewer, FullCharacterMessage message)
        {
            lock (_sync)
            {
                message.TeamFlags = 0;
                message.TeamLeader = Identity.None;
                message.Teams = [];
                message.RaidGroupIndex = 0;
                int viewerId = viewer.Identity.Instance;
                if (!_membership.TryGetValue(viewerId, out Team? team) || viewer.Session == null)
                {
                    _fullCharacterRosters.Remove(viewerId);
                    return;
                }

                message.InTeam = true;
                message.InRaid = team.IsRaid;
                // The rows this block lists are what the client shows after it; the roster publish sends no second
                // TeamMember for them (the original client adds rows without checking for duplicates, Gamecode 1006624f).
                var listed = new Dictionary<int, int>();
                // Live 20261104T005758Z zoning: another raid subgroup has no ordinary-team leader in this field.
                message.TeamLeader = !team.IsRaid || RaidListOf(team, viewerId) == RaidListOf(team, team.Leader)
                    ? CharacterIdentity(team.Leader) : Identity.None;
                if (!team.IsRaid)
                {
                    message.Teams = [Members(viewer, team.Members, -1, listed)];
                    _fullCharacterRosters[viewerId] = new FullCharacterRoster(viewer.Session, team.Id, false, listed);
                    return;
                }

                // A member without a raid team is listed with the first, as live lists an unsorted raid.
                var lists = new List<int>[RaidTeamLists];
                for (int i = 0; i < lists.Length; i++)
                    lists[i] = new List<int>();
                foreach (int id in team.Members)
                    lists[RaidListOf(team, id)].Add(id);

                message.RaidGroupIndex = RaidListOf(team, viewerId);
                var teams = new TeamMember[RaidTeamLists][];
                for (int i = 0; i < lists.Length; i++)
                    teams[i] = Members(viewer, lists[i], i, listed);
                message.Teams = teams;
                _fullCharacterRosters[viewerId] = new FullCharacterRoster(viewer.Session, team.Id, true, listed);
            }
        }

        /// <summary>
        /// The roster the viewer's current session already received in FullCharacter for this team, as a published
        /// roster to continue from with deltas; null when none applies. Consumed once. Caller holds _sync.
        /// </summary>
        private PublishedRoster? TakeFullCharacterRoster(Player viewer, Team team)
        {
            int viewerId = viewer.Identity.Instance;
            if (!_fullCharacterRosters.Remove(viewerId, out FullCharacterRoster? listed)
                || listed.TeamId != team.Id || viewer.Session == null || !ReferenceEquals(listed.Session, viewer.Session))
                return null;
            var published = new PublishedRoster(team.Id) { IsRaid = listed.IsRaid };
            foreach ((int id, int raidIndex) in listed.Members)
                published.Members[id] = raidIndex;
            return published;
        }

        static int RaidListOf(Team team, int characterId)
            => team.RaidTeamIndex.GetValueOrDefault(characterId, -1) is int index and >= 0 and < RaidTeamLists ? index : 0;

        /// <summary>The first raid group with room for a joiner (groups fill in order), or -1 when all six are full.</summary>
        static int FirstRaidGroupWithRoom(Team team)
        {
            System.Span<int> counts = stackalloc int[RaidTeamLists];
            foreach (int id in team.Members)
                counts[RaidListOf(team, id)]++;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] < RaidGroupSize) return i;
            }
            return -1;
        }

        /// <summary>
        /// One list of the block: per member its identity, name, organization, side, level and profession. The client
        /// drops the whole packet for an entry that is not 50000-typed, has no name, a side above 7, a level above 999 or
        /// a profession above 15 (FUN_101260bd), so a member without a known row is left out. Only the viewer's own
        /// organization and side are read here: other members' stats belong to their own playfield.
        /// </summary>
        TeamMember[] Members(Player viewer, List<int> members, int raidIndex, Dictionary<int, int> listed)
        {
            var rows = new List<TeamMember>();
            foreach (int id in members)
            {
                if (!TryLevel(id, out _) || DataOf(id) is not { Name.Length: > 0 } data)
                    continue;

                listed[id] = raidIndex;
                bool self = id == viewer.Identity.Instance;
                rows.Add(new TeamMember
                {
                    Identity = CharacterIdentity(id),
                    Name = data.Name,
                    Unknown1 = self ? viewer.Stats.GetOrZero(CharacterStat.Clan) : 0,
                    Unknown2 = (byte)(self ? System.Math.Clamp(viewer.Stats.GetOrZero(CharacterStat.Side), 0, 7) : 0),
                    Level = (short)data.Level,
                    Profession = (short)data.Profession
                });
            }

            return rows.ToArray();
        }
    }
}
