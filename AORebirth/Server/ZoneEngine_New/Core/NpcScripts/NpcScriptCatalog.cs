namespace ZoneEngine_New.Core.NpcScripts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// The stable, readable identity of a <c>Spawns.json</c> point: <c>HASH@x,y,z</c> with the position rounded to
/// 0.1 m, plus <c>#n</c> for the n-th point stacked at the same place (RDB order). Derived at load from fields the
/// file already has; nothing is written to <c>Spawns.json</c>.
/// </summary>
public static class SpawnPointKey
{
    public static string Build(string hashText, float x, float y, float z, int ordinal)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"{hashText}@{x:F1},{y:F1},{z:F1}");
        return ordinal > 1 ? key + "#" + ordinal.ToString(CultureInfo.InvariantCulture) : key;
    }

    /// <summary>Re-formats an authored key so spacing and number formatting cannot make it miss; empty when malformed.</summary>
    public static string Normalize(string text)
    {
        string trimmed = text.Trim();
        int at = trimmed.IndexOf('@');
        if (at <= 0)
            return string.Empty;
        string hash = trimmed.Substring(0, at).Trim();
        string rest = trimmed.Substring(at + 1);
        int ordinal = 1;
        int hashMark = rest.IndexOf('#');
        if (hashMark >= 0)
        {
            if (!int.TryParse(rest.AsSpan(hashMark + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) || ordinal < 1)
                return string.Empty;
            rest = rest.Substring(0, hashMark);
        }

        string[] parts = rest.Split(',');
        if (parts.Length != 3 || hash.Length == 0)
            return string.Empty;
        var values = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return string.Empty;
        }

        return Build(hash, values[0], values[1], values[2], ordinal);
    }
}

/// <summary>
/// <c>GameData/NpcScripts/*.json</c>: programs and their bindings, compiled at load and indexed by hash, by
/// (playfield, spawn point key) and by (playfield, NPC key). A program or binding with a problem is logged and left
/// out; the rest load. Resolution at spawn, most specific first: spawn point, NPC key, hash on this playfield, hash.
/// </summary>
public sealed class NpcScriptCatalog
{
    public const string DirectoryName = "NpcScripts";
    public const string BindingsSuffix = ".bindings.json";

    static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    readonly IZoneLogger _logger;
    readonly Dictionary<string, NpcScriptProgram> _programs = new(StringComparer.Ordinal);
    readonly Dictionary<string, NpcScriptBinding> _byHash = new(StringComparer.Ordinal);
    readonly Dictionary<(int Playfield, string Key), NpcScriptBinding> _byHashOnPlayfield = new();
    readonly Dictionary<(int Playfield, string Key), NpcScriptBinding> _bySpawnPoint = new();
    readonly Dictionary<(int Playfield, string Key), NpcScriptBinding> _byNpcKey = new();

    public NpcScriptCatalog(IGameData gameData, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Root = Path.Combine(gameData.RootPath, DirectoryName);
        Result = Load(gameData);
        foreach (string problem in Result.Problems)
            _logger.Warn("NpcScripts: " + problem);
        _logger.Info(string.Format(
            CultureInfo.InvariantCulture,
            "NpcScripts loaded programs={0} bindings={1} problems={2} from {3}",
            Result.Programs,
            Result.Bindings,
            Result.Problems.Count,
            Root));
    }

    public string Root { get; }

    public NpcScriptLoadResult Result { get; }

    public int BindingCount => _byHash.Count + _byHashOnPlayfield.Count + _bySpawnPoint.Count + _byNpcKey.Count;

    public bool HasBindings => BindingCount > 0;

    public IEnumerable<NpcScriptProgram> Programs => _programs.Values;

    NpcScriptLoadResult Load(IGameData gameData)
    {
        var problems = new List<string>();
        if (!Directory.Exists(Root))
            return new NpcScriptLoadResult(0, 0, problems);

        var compiler = new NpcScriptCompiler(gameData);
        string[] files = Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        // Programs first, so a sibling .bindings.json can name any of them.
        var bindingFiles = new List<(string File, JsonDocument Document)>();
        var documents = new List<JsonDocument>();
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(Root, file).Replace('\\', '/');
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            }
            catch (JsonException ex)
            {
                problems.Add(name + ": " + ex.Message);
                continue;
            }

            documents.Add(document);
            if (file.EndsWith(BindingsSuffix, StringComparison.OrdinalIgnoreCase))
            {
                bindingFiles.Add((name, document));
                continue;
            }

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add(name + ": root must be an object");
                continue;
            }

            NpcScriptProgram? program = compiler.CompileProgram(name, root, problems);
            if (program == null)
                continue;
            if (!_programs.TryAdd(program.Id, program))
            {
                problems.Add(name + ": program id '" + program.Id + "' is already defined in " + _programs[program.Id].File);
                continue;
            }

            if (root.TryGetProperty("Bindings", out JsonElement bindings))
                AddBindings(name, bindings, program, compiler, problems);
        }

        foreach ((string file, JsonDocument document) in bindingFiles)
        {
            JsonElement root = document.RootElement;
            NpcScriptProgram? program = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Script", out JsonElement scriptId) && scriptId.ValueKind == JsonValueKind.String)
            {
                if (!_programs.TryGetValue(scriptId.GetString()!.Trim(), out program))
                {
                    problems.Add(file + ": unknown Script '" + scriptId.GetString() + "'");
                    continue;
                }
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Bindings", out JsonElement bindings))
            {
                problems.Add(file + ": needs Bindings (and Script unless every binding is movement-only)");
                continue;
            }

            AddBindings(file, bindings, program, compiler, problems);
        }

        foreach (JsonDocument document in documents)
            document.Dispose();

        foreach (NpcScriptProgram program in _programs.Values)
        {
            if (!IsBound(program))
                _logger.Info("NpcScripts: program '" + program.Id + "' (" + program.File + ") has no bindings");
        }

        return new NpcScriptLoadResult(_programs.Count, BindingCount, problems);
    }

    bool IsBound(NpcScriptProgram program)
    {
        foreach (NpcScriptBinding binding in _byHash.Values)
            if (ReferenceEquals(binding.Program, program)) return true;
        foreach (NpcScriptBinding binding in _byHashOnPlayfield.Values)
            if (ReferenceEquals(binding.Program, program)) return true;
        foreach (NpcScriptBinding binding in _bySpawnPoint.Values)
            if (ReferenceEquals(binding.Program, program)) return true;
        foreach (NpcScriptBinding binding in _byNpcKey.Values)
            if (ReferenceEquals(binding.Program, program)) return true;
        return false;
    }

    void AddBindings(string file, JsonElement bindings, NpcScriptProgram? program, NpcScriptCompiler compiler, List<string> problems)
    {
        if (bindings.ValueKind != JsonValueKind.Array)
        {
            problems.Add(file + ": Bindings must be a list");
            return;
        }

        int index = 0;
        foreach (JsonElement element in bindings.EnumerateArray())
        {
            NpcScriptBinding? binding = compiler.CompileBinding(file, index++, element, program, problems);
            if (binding == null)
                continue;

            bool added;
            string where;
            switch (binding.Target)
            {
                case ScriptBindingTarget.SpawnPoint:
                    added = _bySpawnPoint.TryAdd((binding.PlayfieldId!.Value, binding.Key), binding);
                    where = "spawn point " + binding.Key + " on playfield " + binding.PlayfieldId;
                    break;
                case ScriptBindingTarget.NpcKey:
                    added = _byNpcKey.TryAdd((binding.PlayfieldId!.Value, binding.Key), binding);
                    where = "NPC " + binding.Key + " on playfield " + binding.PlayfieldId;
                    break;
                default:
                    if (binding.PlayfieldId is int pf)
                    {
                        added = _byHashOnPlayfield.TryAdd((pf, binding.Key), binding);
                        where = "hash " + binding.Key + " on playfield " + pf;
                    }
                    else
                    {
                        added = _byHash.TryAdd(binding.Key, binding);
                        where = "hash " + binding.Key;
                    }

                    break;
            }

            if (!added)
                problems.Add(file + " binding " + (index - 1) + ": " + where + " is already bound");
        }
    }

    /// <summary>The most specific binding for an NPC, or null.</summary>
    public NpcScriptBinding? Resolve(string? hash, int playfieldId, string? spawnPointKey, string? npcKey)
    {
        NpcScriptBinding? binding;
        if (spawnPointKey != null && _bySpawnPoint.TryGetValue((playfieldId, spawnPointKey), out binding))
            return binding;
        if (npcKey != null && _byNpcKey.TryGetValue((playfieldId, npcKey), out binding))
            return binding;
        if (hash == null)
            return null;
        if (_byHashOnPlayfield.TryGetValue((playfieldId, hash), out binding))
            return binding;
        return _byHash.TryGetValue(hash, out binding) ? binding : null;
    }

    /// <summary>
    /// Resolves and applies the binding for a freshly built NPC: plain movement onto its brain, the program as a
    /// runner, then <see cref="NpcScriptTrigger.Spawned"/>. Returns false when nothing is bound.
    /// </summary>
    public bool TryAttach(NpcCharacter npc, int playfieldId, string? spawnPointKey, string? npcKey)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (BindingCount == 0)
            return false;

        NpcScriptBinding? binding = Resolve(npc.SpawnHash ?? npc.MobTemplate?.Hash, playfieldId, spawnPointKey, npcKey);
        if (binding == null)
            return false;

        NpcBrain? brain = npc.Brain;
        if (brain != null)
        {
            if (binding.Patrol != null)
            {
                brain.PatrolWaypoints = binding.Patrol.Waypoints;
                brain.PatrolEnabled = true;
            }
            else if (binding.Wander != null)
            {
                Vector3 anchor = brain.Home ?? npc.Position;
                brain.EnableWander(anchor, binding.Wander.Radius, binding.Wander.MinPauseSeconds, binding.Wander.MaxPauseSeconds);
            }
        }

        if (binding.Program != null)
        {
            var runner = new NpcScriptRunner(binding.Program, binding.Args, npc);
            npc.AttachScript(runner);
            runner.Raise(NpcScriptTrigger.Spawned);
        }

        return true;
    }

    /// <summary>Logs every spawn point binding of a playfield whose key matched none of the playfield's points.</summary>
    public void ReportUnmatchedSpawnPoints(int playfieldId, IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (_bySpawnPoint.Count == 0)
            return;

        var known = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (KeyValuePair<(int Playfield, string Key), NpcScriptBinding> pair in _bySpawnPoint)
        {
            if (pair.Key.Playfield == playfieldId && !known.Contains(pair.Key.Key))
                _logger.Warn("NpcScripts: binding " + pair.Value.Describe() + " matches no spawn point (" + pair.Value.Program?.File + ")");
        }
    }
}
