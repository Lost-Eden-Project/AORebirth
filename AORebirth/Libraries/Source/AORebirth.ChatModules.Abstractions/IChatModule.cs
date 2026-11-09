namespace AORebirth.ChatModules
{
    /// <summary>
    /// Entry point of an external ChatEngine module. The host creates every public non-abstract implementation in the
    /// module assembly with its parameterless constructor, calls <see cref="Initialize"/> once at startup before the chat
    /// listener opens, and <see cref="Shutdown"/> once when the engine stops. A module never references engine types.
    /// </summary>
    public interface IChatModule
    {
        void Initialize(IChatModuleContext context);

        void Shutdown();
    }
}
