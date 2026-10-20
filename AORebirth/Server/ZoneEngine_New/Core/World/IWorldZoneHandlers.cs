namespace ZoneEngine_New.Core.World
{
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    /// <summary>What this zone tells World about itself, and does when World asks.</summary>
    public interface IWorldZoneHandlers
    {
        /// <summary>Players per loaded playfield, for heartbeats and assignment.</summary>
        IReadOnlyList<PlayfieldLoad> Load();

        /// <summary>Name, playfield and link-dead state of every player here.</summary>
        IReadOnlyList<PlayerPresence> Presence();

        /// <summary>Save, despawn and release a link-dead character; refuse one a client is playing.</summary>
        Task<ReleaseOutcome> ReleaseCharacterAsync(int characterId);

        LocateCharacterReply Locate(int characterId);

        /// <summary>Nobody owns these characters: clear their Online flag in the database.</summary>
        void ClearOnline(IReadOnlyList<int> characterIds);

        /// <summary>Another owner won the character: drop it here without saving.</summary>
        void Revoke(int characterId, string reason);

        /// <summary>World is unreachable and the lease deadline is close: save and log out everyone while still owned.</summary>
        void LogoutAll(string reason);

        void GameClockChanged(long offsetGameSeconds);

        void DutyChanged(bool holder);

        void Announce(string text);

        /// <summary>The teams this instance mirrors, for World's registration.</summary>
        IReadOnlyList<TeamState> Teams();
    }

    public readonly record struct PlayerPresence(int CharacterId, string Name, int PlayfieldId, bool LinkDead, int Level, int Profession,
        PlayerCard? Card = null);
}
