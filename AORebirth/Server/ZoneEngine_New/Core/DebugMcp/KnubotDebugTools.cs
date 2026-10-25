namespace ZoneEngine_New.Core.DebugMcp
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using ModelContextProtocol.Server;

    using ZoneEngine_New.Core.Knubot;

    /// <summary>
    /// Knubot dialog scripts while the zone runs: list, read and check them, write one and reload. A reload swaps the
    /// scripts in whole and re-announces spawned NPCs whose script presence changed; a chat already open keeps the
    /// script it started with, so changes show on the next chat.
    /// </summary>
    [McpServerToolType]
    public sealed class KnubotDebugTools
    {
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Largest script the write tool accepts; the biggest shipped script is well under this.</summary>
        const int MaxScriptBytes = 512 * 1024;

        readonly KnubotCatalog _catalog;
        readonly DebugMcpEndpoint _endpoint;

        public KnubotDebugTools(KnubotCatalog catalog, DebugMcpEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(endpoint);
            _catalog = catalog;
            _endpoint = endpoint;
        }

        [McpServerTool(Name = "knubot_list")]
        [System.ComponentModel.Description("Loaded Knubot dialog scripts: id (path under GameData/Knubot without .json), NPC hashes, line/reply/trade counts, dead-end replies, and the file each is read from. Read-only.")]
        public string List()
        {
            var scripts = _catalog.LoadedScripts().Values.Select(script => new
            {
                id = script.Id,
                npcs = script.Npcs,
                lines = script.Lines.Count,
                replies = script.Lines.Values.SelectMany(line => line.Replies).Select(reply => reply.Id).Distinct().Count(),
                trades = script.Trades.Length,
                deadEnds = KnubotCatalog.DeadEnds(script),
                file = _catalog.FileFor(script.Id)
            }).ToArray();
            return Json(new { root = _catalog.Root, overrideRoot = _catalog.OverrideRoot, count = scripts.Length, scripts });
        }

        [McpServerTool(Name = "knubot_read")]
        [System.ComponentModel.Description("The JSON text of one Knubot script file by id (e.g. BrandonThorn), from the override folder when it has one, else GameData/Knubot. Read-only.")]
        public string Read(string id)
        {
            string? path = _catalog.FileFor(id);
            if (path == null)
                return Error("No script file for id '" + id + "'. Use knubot_list for ids.");
            return Json(new { id, file = path, json = File.ReadAllText(path) });
        }

        [McpServerTool(Name = "knubot_check")]
        [System.ComponentModel.Description("Validates Knubot script JSON as script id without writing or loading it: the errors it would be rejected with (empty = it loads) and its dead-end replies. Read-only.")]
        public string Check(string id, string json)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Error("id is required.");
            var errors = _catalog.Check(id, json, out string[] deadEnds);
            return Json(new { id, loads = errors.Count == 0, errors, deadEnds });
        }

        [McpServerTool(Name = "knubot_write")]
        [System.ComponentModel.Description("WRITE: replaces (or creates) the Knubot script file for id with json, then reloads every script so the change applies to the next chat without a restart. Refused, with nothing written, when the JSON would not load. The previous file is kept beside it as <file>.bak. Needs DebugMcp AllowWrites. Returns the reload result and this script's dead ends.")]
        public string Write(string id, string json)
        {
            if (!_endpoint.AllowWrites)
                return Error("Write tools are off. Set DebugMcp AllowWrites to true in the zone config and restart the zone engine.");
            if (string.IsNullOrWhiteSpace(id) || !_catalog.TryWritePath(id, out string path))
                return Error("'" + id + "' is not a script id: letters, digits, '_', '-' and '/' only, relative to GameData/Knubot.");
            if (string.IsNullOrEmpty(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxScriptBytes)
                return Error("json is empty or larger than " + MaxScriptBytes + " bytes.");

            var errors = _catalog.Check(id, json, out string[] deadEnds);
            if (errors.Count != 0)
                return Json(new { id, written = false, errors });

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temp, path, overwrite: true);

            KnubotLoadResult result = _catalog.Reload();
            return Json(new
            {
                id,
                written = true,
                file = path,
                deadEnds,
                reload = Summary(result)
            });
        }

        [McpServerTool(Name = "knubot_reload")]
        [System.ComponentModel.Description("WRITE: reloads every Knubot script from disk (after editing files directly). Scripts with errors stay out, as at startup; spawned NPCs whose script presence changed are re-announced to nearby players. Needs DebugMcp AllowWrites. Returns loaded/rejected counts and the problems.")]
        public string Reload()
        {
            if (!_endpoint.AllowWrites)
                return Error("Write tools are off. Set DebugMcp AllowWrites to true in the zone config and restart the zone engine.");
            return Json(Summary(_catalog.Reload()));
        }

        static object Summary(KnubotLoadResult result)
            => new { loaded = result.Loaded, npcs = result.Npcs, rejected = result.Problems.Count, problems = result.Problems };

        static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

        static string Error(string message) => Json(new { error = message });
    }
}
