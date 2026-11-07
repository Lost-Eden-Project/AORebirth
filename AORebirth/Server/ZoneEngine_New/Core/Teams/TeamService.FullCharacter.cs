namespace ZoneEngine_New.Core.Teams
{
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    public sealed partial class TeamService
    {
        const int FullCharacterInTeam = 1;
        const int FullCharacterInRaid = 2;
        const int RaidTeamLists = 6;
        const int RaidGroupSize = 6;

        /// <summary>The rows a FullCharacter listed for one session: member → raid list (-1 in an ordinary team).</summary>
        private sealed record FullCharacterRoster(IZoneSession Session, int TeamId, bool IsRaid, Dictionary<int, int> Members);

        /// <summary>
        /// FullCharacter's team block (Gamecode.dll FUN_10073ba7), read every time the client enters a playfield. Live sends
        /// it filled (capture 2026-10-27T15:40:05Z: flags 3, the leader 50000:71A9545B, raid team 0, then six lists with
        /// the two members in the first), and the client rebuilds its team and raid windows from it; sent empty, a zone
        /// dropped the raid. <paramref name="flags"/>: bit 0 in a team, bit 1 a raid. Empty when not in a team.
        /// </summary>
        public byte[] BuildFullCharacterTeam(Player viewer, out int flags)
        {
            lock (_sync)
            {
                flags = 0;
                int viewerId = viewer.Identity.Instance;
                if (!_membership.TryGetValue(viewerId, out Team? team) || viewer.Session == null)
                {
                    _fullCharacterRosters.Remove(viewerId);
                    return [];
                }

                flags = FullCharacterInTeam | (team.IsRaid ? FullCharacterInRaid : 0);
                // The rows this block lists are what the client shows after it; the roster publish sends no second
                // TeamMember for them (the original client adds rows without checking for duplicates, Gamecode 1006624f).
                var listed = new Dictionary<int, int>();
                using var stream = new MemoryStream();
                // Live 20261104T005758Z zoning: another raid subgroup has no ordinary-team leader in this field.
                WriteIdentity(stream, !team.IsRaid || RaidListOf(team, viewerId) == RaidListOf(team, team.Leader)
                    ? CharacterIdentity(team.Leader) : Identity.None);
                if (!team.IsRaid)
                {
                    WriteMembers(stream, viewer, team.Members, -1, listed);
                    _fullCharacterRosters[viewerId] = new FullCharacterRoster(viewer.Session, team.Id, false, listed);
                    return stream.ToArray();
                }

                // A member without a raid team is listed with the first, as live lists an unsorted raid.
                var lists = new List<int>[RaidTeamLists];
                for (int i = 0; i < lists.Length; i++)
                    lists[i] = new List<int>();
                foreach (int id in team.Members)
                    lists[RaidListOf(team, id)].Add(id);

                WriteInt(stream, RaidListOf(team, viewerId));
                for (int i = 0; i < lists.Length; i++)
                    WriteMembers(stream, viewer, lists[i], i, listed);
                _fullCharacterRosters[viewerId] = new FullCharacterRoster(viewer.Session, team.Id, true, listed);
                return stream.ToArray();
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
        /// A 3F1 count, then per member: Identity, short-length name, int organization, byte side, short level, short
        /// profession. The client drops the whole packet for an entry that is not 50000-typed, has no name, a side above 7,
        /// a level above 999 or a profession above 15 (FUN_101260bd), so a member without a known row is left out. Only
        /// the viewer's own organization and side are read here: other members' stats belong to their own playfield.
        /// </summary>
        void WriteMembers(MemoryStream stream, Player viewer, List<int> members, int raidIndex, Dictionary<int, int> listed)
        {
            var rows = new List<(int Id, MemberData Data)>();
            foreach (int id in members)
            {
                if (TryLevel(id, out _) && DataOf(id) is { Name.Length: > 0 } data)
                {
                    rows.Add((id, data));
                    listed[id] = raidIndex;
                }
            }

            WriteInt(stream, (rows.Count + 1) * 0x3F1);
            foreach ((int id, MemberData data) in rows)
            {
                bool self = id == viewer.Identity.Instance;
                WriteIdentity(stream, CharacterIdentity(id));
                byte[] name = Encoding.Latin1.GetBytes(data.Name);
                WriteShort(stream, (short)name.Length);
                stream.Write(name);
                WriteInt(stream, self ? viewer.Stats.GetOrZero(CharacterStat.Clan) : 0);
                stream.WriteByte((byte)(self ? System.Math.Clamp(viewer.Stats.GetOrZero(CharacterStat.Side), 0, 7) : 0));
                WriteShort(stream, (short)data.Level);
                WriteShort(stream, (short)data.Profession);
            }
        }

        static void WriteIdentity(MemoryStream stream, Identity identity)
        {
            WriteInt(stream, (int)identity.Type);
            WriteInt(stream, identity.Instance);
        }

        static void WriteInt(MemoryStream stream, int value)
        {
            System.Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        static void WriteShort(MemoryStream stream, short value)
        {
            System.Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
