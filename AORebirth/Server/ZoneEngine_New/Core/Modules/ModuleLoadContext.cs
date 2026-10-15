namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Reflection;
    using System.Runtime.Loader;

    using AORebirth.Modules;

    /// <summary>
    /// One per module, so each module resolves its own dependencies from its own folder. The module API assembly always
    /// comes from the engine, so the module's IZoneModule is the engine's IZoneModule.
    /// </summary>
    internal sealed class ModuleLoadContext : AssemblyLoadContext
    {
        internal static readonly string SharedAssemblyName = typeof(IZoneModule).Assembly.GetName().Name!;

        private readonly AssemblyDependencyResolver _resolver;

        public ModuleLoadContext(string moduleId, string mainAssemblyPath)
            : base("module:" + moduleId, isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SharedAssemblyName, StringComparison.OrdinalIgnoreCase))
                return null;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
