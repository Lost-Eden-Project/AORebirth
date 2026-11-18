namespace AORebirth.Modules.Data
{
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The module's own tables in the server database (module API 1.14). Needs the <c>database</c> capability.
    /// <para>
    /// A module owns the tables named <see cref="TablePrefix"/> followed by lower-case letters, digits and '_'
    /// (<c>mod_&lt;module id in lower case&gt;_</c>). It creates and changes them only with migrations it ships in
    /// <c>Modules/&lt;Id&gt;/Migrations/NNNN_name.sql</c>, which DatabaseMigrationTool checks and applies with the server's
    /// own migrations (never the zone). A module whose migrations are not all applied, or were changed after they were
    /// applied, does not load. A migration may only create, alter, drop and fill the module's own tables.
    /// </para>
    /// <para>
    /// At run time the module reads and writes its tables through <see cref="OpenAsync"/>; it must not touch any other
    /// table (characters, items, accounts and the rest belong to the engine and change only through
    /// <see cref="World.IZoneActions"/>). Never on a playfield tick: event handlers, GM command handlers and message
    /// handlers hand database work to another thread.
    /// </para>
    /// </summary>
    public interface IModuleDatabase
    {
        /// <summary>The prefix every table of this module has, e.g. <c>mod_missionmodule_</c>.</summary>
        string TablePrefix { get; }

        /// <summary>
        /// The full name of the module's table <paramref name="name"/> (lower-case letters, digits and '_'), e.g.
        /// <c>Table("icc_purchases")</c> is <c>mod_missionmodule_icc_purchases</c>. Throws for an invalid name.
        /// </summary>
        string Table(string name);

        /// <summary>
        /// An open connection to the server database, from a pool of this module's own. Dispose it when done. Throws
        /// <see cref="System.InvalidOperationException"/> on a playfield tick thread or when the zone has no database.
        /// </summary>
        Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
    }
}
