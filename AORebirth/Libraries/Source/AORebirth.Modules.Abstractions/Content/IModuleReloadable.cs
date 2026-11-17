namespace AORebirth.Modules.Content
{
    using System.Collections.Generic;

    /// <summary>
    /// A module's own data that can be read again while the zone runs (module API 1.13), registered with
    /// <see cref="IZoneModuleContext.RegisterReloadable"/>. The zone lists it beside its GameData catalogs as
    /// <c>&lt;module id&gt;/&lt;Name&gt;</c>, and every GameData reload path (<see cref="IZoneGameData.Reload"/>, the
    /// debug tools) reaches it under that name.
    /// </summary>
    /// <remarks>
    /// <see cref="Reload"/> runs on a worker thread, never on a playfield tick, one reload at a time, while the
    /// module's handlers keep running on their own threads. It reads its files again and swaps an immutable result in
    /// whole (one reference write), so a handler sees the old data or the new, never a mix. A file that fails to read
    /// or parse keeps the old data and returns <c>Ok = false</c> with the reason in <c>Problems</c>.
    /// </remarks>
    public interface IModuleReloadable
    {
        /// <summary>1-32 letters, digits, '-' or '_', unique within the module, e.g. "IccShop".</summary>
        string Name { get; }

        /// <summary>The files or folders it reads, relative to the module folder.</summary>
        IReadOnlyList<string> Files { get; }

        /// <summary>Reads the files again and swaps the result in, or keeps the old data and says why.</summary>
        GameDataReloadResult Reload();
    }
}
