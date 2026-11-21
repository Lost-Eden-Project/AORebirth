namespace AORebirth.Modules
{
    /// <summary>
    /// Optional (module API 1.18), for a module whose module.json sets <c>SupportsHotReload</c>. When the operator reloads
    /// the module, the host calls <see cref="ExportState"/> on each running entry just before <see cref="IZoneModule.Shutdown"/>
    /// and hands the text to the entry of the same type name in the new version, right after its
    /// <see cref="IZoneModule.Initialize"/>. The state crosses as text because the two versions' types are not the same
    /// types. An entry that does not implement this starts empty; whatever it held in memory is lost.
    /// </summary>
    public interface IZoneModuleStateTransfer
    {
        /// <summary>The entry's in-memory state, or null for none. Called on a background thread after the module stopped receiving events.</summary>
        string? ExportState();

        /// <summary>
        /// What the previous version exported. Called once, right after Initialize; events the entry subscribed to in
        /// Initialize may already be arriving on playfield threads.
        /// </summary>
        void ImportState(string state);
    }
}
