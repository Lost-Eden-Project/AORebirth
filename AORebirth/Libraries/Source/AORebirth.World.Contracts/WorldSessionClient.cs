namespace AORebirth.World.Contracts
{
    using System;
    using System.Threading;

    using AORebirth.World.Contracts.V1;

    /// <summary>
    /// LoginEngine's synchronous calls to World. Login runs its handlers on network threads that already block on
    /// the database, so short blocking calls with a deadline keep its flow unchanged.
    /// </summary>
    public sealed class WorldSessionClient : IDisposable
    {
        static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        /// <summary>The delete waits for the zone to save and despawn a link-dead character.</summary>
        static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(12);

        static WorldSessionClient? _shared;
        readonly WorldChannel _channel;

        public WorldSessionClient(string url)
        {
            _channel = new WorldChannel(url, "login");
        }

        /// <summary>One connection for the process, created on first use.</summary>
        public static WorldSessionClient Shared(string url)
        {
            WorldSessionClient? current = Volatile.Read(ref _shared);
            if (current != null) return current;
            var created = new WorldSessionClient(url);
            current = Interlocked.CompareExchange(ref _shared, created, null);
            if (current == null) return created;
            created.Dispose();
            return current;
        }

        public string Url => _channel.Url;

        public string BeginLogin(string account)
            => _channel.Session.BeginLogin(new BeginLoginRequest { Account = account }, _channel.Call(CallTimeout)).Generation;

        public SelectCharacterReply SelectCharacter(string account, string generation, int characterId, int storedPlayfield)
            => _channel.Session.SelectCharacter(
                new SelectCharacterRequest
                {
                    Account = account,
                    Generation = generation ?? string.Empty,
                    CharacterId = characterId,
                    StoredPlayfield = storedPlayfield
                },
                _channel.Call(CallTimeout));

        public DeleteState ReleaseForDelete(string account, int characterId)
            => _channel.Session.ReleaseForDelete(new DeleteRequest { Account = account, CharacterId = characterId },
                _channel.Call(DeleteTimeout)).State;

        public void CompleteDelete(string account, int characterId)
            => _channel.Session.CompleteDelete(new DeleteRequest { Account = account, CharacterId = characterId },
                _channel.Call(CallTimeout));

        /// <summary>Which of these characters anyone owns (a zone, a bot, or a delete in progress).</summary>
        public System.Collections.Generic.ISet<int> Owned(System.Collections.Generic.IEnumerable<int> characterIds)
        {
            var query = new LeaseQuery();
            query.CharacterIds.AddRange(characterIds);
            return new System.Collections.Generic.HashSet<int>(_channel.Leases.Query(query, _channel.Call(CallTimeout)).Owned);
        }

        public void Dispose() => _channel.Dispose();
    }
}
