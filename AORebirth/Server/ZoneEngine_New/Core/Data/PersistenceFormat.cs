namespace ZoneEngine_New.Core.Data
{
    /// <summary>
    /// The version of how this build stores what the schema alone does not describe: serialized blobs and the meaning
    /// of columns (inventory and bank layouts, buff and cooldown records, quest and mission state, module rows written
    /// through the host). Raise it whenever a build writes something an earlier build would read wrongly, or reads
    /// what an earlier build wrote differently, even without a migration. A zone swap needs two builds to read and write
    /// the same rows at once (and again on rollback), so <c>--compat-fingerprint</c> reports it and a different value
    /// makes the swap not viable.
    /// </summary>
    public static class PersistenceFormat
    {
        public const int Version = 1;
    }
}
