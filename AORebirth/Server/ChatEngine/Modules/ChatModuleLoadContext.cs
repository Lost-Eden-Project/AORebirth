namespace ChatEngine.Modules
{
    using System;
    using System.Reflection;
    using System.Runtime.Loader;

    using AORebirth.ChatModules;

    /// <summary>
    /// One per module, so each module resolves its own dependencies from its own folder. The chat module API assembly
    /// always comes from the engine, so the module's IChatModule is the engine's IChatModule.
    /// </summary>
    internal sealed class ChatModuleLoadContext : AssemblyLoadContext
    {
        internal static readonly string SharedAssemblyName = typeof(IChatModule).Assembly.GetName().Name;

        private readonly AssemblyDependencyResolver resolver;

        public ChatModuleLoadContext(string moduleId, string mainAssemblyPath)
            : base("chat-module:" + moduleId, isCollectible: false)
        {
            this.resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SharedAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = this.resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : this.LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string path = this.resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null ? IntPtr.Zero : this.LoadUnmanagedDllFromPath(path);
        }
    }
}
