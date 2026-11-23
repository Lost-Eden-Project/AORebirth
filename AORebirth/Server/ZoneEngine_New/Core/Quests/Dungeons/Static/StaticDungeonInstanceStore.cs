namespace ZoneEngine_New.Core.Quests.Dungeons.Static
{
    using System;

    using AORebirth.Database.Domain.Dungeons;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The durable record of live static dungeon instances, shared by every zone instance: which owner (team or
    /// character) holds which dungeon playfield at which entrance. A read that fails returns nothing and a write that
    /// fails returns false, both logged: the service then works from its own memory, so a database outage degrades
    /// instance sharing across zones, never entering.
    /// </summary>
    public interface IStaticDungeonInstanceStore
    {
        StaticDungeonInstanceRow? Find(uint entranceType, uint entranceInstance, int ownerKind, int ownerId);

        StaticDungeonInstanceRow? Load(int playfieldId);

        /// <summary>False when the owner already holds an instance at that entrance, the id is taken, or the write failed.</summary>
        bool TryInsert(StaticDungeonInstanceRow row);

        void Delete(int playfieldId);

        int PurgeOlderThan(long cutoffUtcTicks, int limit);
    }

    /// <summary><c>staticdungeoninstances</c> through the shared database library.</summary>
    public sealed class MySqlStaticDungeonInstanceStore : IStaticDungeonInstanceStore
    {
        readonly MySqlStaticDungeonInstanceDao _dao;
        readonly IZoneLogger _logger;

        public MySqlStaticDungeonInstanceStore(IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
            string connectionString = MySqlConnectionSettings.GetRequiredConnectionString();
            _dao = new MySqlStaticDungeonInstanceDao(() => new MySqlConnector.MySqlConnection(connectionString));
        }

        public StaticDungeonInstanceRow? Find(uint entranceType, uint entranceInstance, int ownerKind, int ownerId)
            => Run(() => _dao.Find(entranceType, entranceInstance, ownerKind, ownerId), "find");

        public StaticDungeonInstanceRow? Load(int playfieldId) => Run(() => _dao.Load(playfieldId), "load");

        public bool TryInsert(StaticDungeonInstanceRow row) => Run(() => _dao.TryInsert(row), "insert");

        public void Delete(int playfieldId) => Run(() => { _dao.Delete(playfieldId); return true; }, "delete");

        public int PurgeOlderThan(long cutoffUtcTicks, int limit) => Run(() => _dao.PurgeOlderThan(cutoffUtcTicks, limit), "purge");

        T? Run<T>(Func<T> action, string what)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Static dungeon instance " + what + " failed; continuing from memory");
                return default;
            }
        }
    }
}
