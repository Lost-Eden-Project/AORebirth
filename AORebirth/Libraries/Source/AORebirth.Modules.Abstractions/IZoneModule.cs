namespace AORebirth.Modules
{
    /// <summary>
    /// Entry point of an external ZoneEngine_New module. The host creates every public non-abstract implementation in
    /// the module assembly with its parameterless constructor, calls <see cref="Initialize"/> once at startup before the
    /// zone listener opens, and <see cref="Shutdown"/> once when the engine stops. A module registers what it does
    /// (connection filter, event handlers) through the context; it never references engine types.
    /// </summary>
    public interface IZoneModule
    {
        void Initialize(IZoneModuleContext context);

        void Shutdown();
    }
}
