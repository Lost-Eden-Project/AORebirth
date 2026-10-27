namespace ZoneEngine_New.Core.Data
{
    using Dapper;

    using MySqlConnector;

    /// <summary>The login table's Flags (LoginEngine admits an account only while it is 0).</summary>
    public interface ILoginFlagsRepository
    {
        /// <summary>Writes Flags for the named account; returns the number of accounts updated (0 when none has that name).</summary>
        int SetLoginFlags(string username, int flags);
    }

    public sealed class MySqlLoginFlagsRepository : ILoginFlagsRepository
    {
        public int SetLoginFlags(string username, int flags)
        {
            using var connection = new MySqlConnection(MySqlConnectionSettings.GetRequiredConnectionString());
            return connection.Execute(
                "UPDATE login SET Flags=@Flags WHERE Username=@Username LIMIT 1",
                new { Username = username, Flags = flags });
        }
    }
}
