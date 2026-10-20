namespace ZoneEngine_New.Core.Knubot;

using System.Collections.Generic;

/// <summary>
/// One <see cref="KnubotCatalog"/> build: scripts and NPCs it installed, and one "file: reasons" line for every script
/// it left out.
/// </summary>
public sealed record KnubotLoadResult(
    int Loaded,
    int Npcs,
    IReadOnlyList<string> Problems,
    Dictionary<string, KnubotScript> ByNpc);
