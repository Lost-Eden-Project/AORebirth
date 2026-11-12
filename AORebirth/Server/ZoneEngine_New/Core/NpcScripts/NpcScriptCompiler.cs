namespace ZoneEngine_New.Core.NpcScripts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Mobs;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Compiles one <c>GameData/NpcScripts/*.json</c> program (and the bindings in it or in a sibling
/// <c>.bindings.json</c>) into <see cref="NpcScriptProgram"/> and <see cref="NpcScriptBinding"/>s. Every problem is
/// one line; a program or binding with a problem is not produced, the rest still are.
/// </summary>
public sealed class NpcScriptCompiler
{
    public const int MinWaitMs = 100;
    public const int DefaultEmoteMs = 2000;
    public const int MaxSayLength = 255;
    public const int MinSocialAnim = 1;
    public const int MaxSocialAnim = 71;
    public const int MaxWorldItemLifetimeSeconds = 3600;
    public const int MaxNpcLifetimeSeconds = 3600;
    public const int MaxSpawnCount = 12;
    public const float MaxSpawnRadius = 30f;
    public const int MaxSpawnLevelPercent = 200;
    public const int MaxCastDistance = 40;
    public const int MaxAddDiedDelayMs = 3_600_000;
    public const int MaxTagLength = 32;
    public const int MaxRandomWeight = 10_000;

    readonly IGameData _gameData;

    public NpcScriptCompiler(IGameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Program
    // ----------------------------------------------------------------------------------------------------------------

    public NpcScriptProgram? CompileProgram(string file, JsonElement root, List<string> problems)
    {
        var errors = new List<string>();
        string id = root.TryGetProperty("Id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!.Trim()
            : string.Empty;
        if (id.Length == 0)
            errors.Add("missing Id");

        ScriptParam[] parameters = ParseParams(root, errors);
        var ctx = new ProgramContext(parameters, errors);

        var handlers = new List<ScriptHandler>();
        if (root.TryGetProperty("Handlers", out JsonElement handlersElement) && handlersElement.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement handlerElement in handlersElement.EnumerateArray())
            {
                CompileHandler(handlerElement, index++, ctx, handlers);
            }
        }
        else
        {
            errors.Add("missing Handlers");
        }

        if (handlers.Count == 0 && errors.Count == 0)
            errors.Add("no handlers");

        CheckSpinningLoops(ctx, handlers);

        // An AddDied Tag no SpawnNpc gives out can never fire: a typo, not a choice.
        foreach ((int handlerIndex, int tag) in ctx.TagFilters)
        {
            if (!ctx.SpawnedTags.Contains(tag))
                errors.Add("handler " + handlerIndex + ": AddDied Tag '" + ctx.Tags[tag] + "' is on no SpawnNpc");
        }

        if (errors.Count > 0)
        {
            problems.Add(file + ": " + string.Join("; ", errors));
            return null;
        }

        // Trigger, then descending priority: Raise picks the first match.
        handlers.Sort((a, b) =>
        {
            int byTrigger = a.Trigger.CompareTo(b.Trigger);
            return byTrigger != 0 ? byTrigger : b.Priority.CompareTo(a.Priority);
        });

        return new NpcScriptProgram(
            id,
            file,
            parameters,
            handlers.ToArray(),
            ctx.Ops.ToArray(),
            ctx.Strings.ToArray(),
            ctx.Vectors.ToArray(),
            ctx.Jumps.ToArray(),
            ctx.Spawns.ToArray(),
            ctx.Tags.ToArray(),
            ctx.NanoLists.ToArray(),
            ctx.HashLists.ToArray());
    }

    static ScriptParam[] ParseParams(JsonElement root, List<string> errors)
    {
        var list = new List<ScriptParam>();
        if (!root.TryGetProperty("Params", out JsonElement paramsElement))
            return Array.Empty<ScriptParam>();
        if (paramsElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Params must be an object");
            return Array.Empty<ScriptParam>();
        }

        foreach (JsonProperty property in paramsElement.EnumerateObject())
        {
            string typeName = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
            if (!TryParseParamType(typeName, out ScriptParamType type))
            {
                errors.Add("param " + property.Name + ": unknown type '" + typeName + "'");
                continue;
            }

            foreach (ScriptParam existing in list)
            {
                if (string.Equals(existing.Name, property.Name, StringComparison.Ordinal))
                    errors.Add("param " + property.Name + " declared twice");
            }

            list.Add(new ScriptParam(property.Name, type));
        }

        return list.ToArray();
    }

    static bool TryParseParamType(string text, out ScriptParamType type)
    {
        switch (text.Trim())
        {
            case "Vector3[]": type = ScriptParamType.Vector3Array; return true;
            case "Vector3": type = ScriptParamType.Vector3; return true;
            case "ItemHash": type = ScriptParamType.ItemHash; return true;
            case "SocialAction": type = ScriptParamType.SocialAction; return true;
            case "Int": type = ScriptParamType.Int; return true;
            case "Gfx": type = ScriptParamType.Gfx; return true;
            case "Text": type = ScriptParamType.Text; return true;
            case "NpcHash": type = ScriptParamType.NpcHash; return true;
            default: type = default; return false;
        }
    }

    sealed class ProgramContext
    {
        public ProgramContext(ScriptParam[] parameters, List<string> errors)
        {
            Params = parameters;
            Errors = errors;
        }

        public ScriptParam[] Params { get; }

        public List<string> Errors { get; }

        public List<ScriptOp> Ops { get; } = new();

        public List<string> Strings { get; } = new();

        public List<Vector3> Vectors { get; } = new();

        public List<int> Jumps { get; } = new();

        public List<SpawnNpcSpec> Spawns { get; } = new();

        public List<string> Tags { get; } = new();

        public List<int[]> NanoLists { get; } = new();

        public List<string[]> HashLists { get; } = new();

        /// <summary>Tag ids some SpawnNpc gives out.</summary>
        public HashSet<int> SpawnedTags { get; } = new();

        /// <summary>(handler index, tag id) for each AddDied handler with a Tag filter.</summary>
        public List<(int Handler, int Tag)> TagFilters { get; } = new();

        public string Where { get; set; } = string.Empty;

        /// <summary>The tag's id, or -1 (with an error) when the name is unusable.</summary>
        public int Tag(string? name, string verb)
        {
            string tag = (name ?? string.Empty).Trim();
            if (tag.Length == 0 || tag.Length > MaxTagLength)
            {
                Error(verb + ": Tag is a name of 1.." + MaxTagLength + " characters");
                return -1;
            }

            int index = Tags.IndexOf(tag);
            if (index >= 0)
                return index;
            Tags.Add(tag);
            return Tags.Count - 1;
        }

        public void Error(string message) => Errors.Add(Where + message);

        public int String(string text)
        {
            int index = Strings.IndexOf(text);
            if (index >= 0)
                return index;
            Strings.Add(text);
            return Strings.Count - 1;
        }

        public int Vector(Vector3 vector)
        {
            Vectors.Add(vector);
            return Vectors.Count - 1;
        }

        public int ParamSlot(string name, ScriptParamType expected, out bool found)
        {
            found = false;
            for (int i = 0; i < Params.Length; i++)
            {
                if (!string.Equals(Params[i].Name, name, StringComparison.Ordinal))
                    continue;
                found = true;
                if (Params[i].Type != expected)
                {
                    Error("$" + name + " is " + Params[i].Type + ", expected " + expected);
                    return -1;
                }

                return i;
            }

            Error("unknown parameter $" + name);
            return -1;
        }
    }

    sealed class HandlerScope
    {
        public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);

        /// <summary>(op index, label) to patch once all labels are known: A for Goto, D for If.</summary>
        public List<(int Op, string Label, bool IsIf)> Pending { get; } = new();
    }

    void CompileHandler(JsonElement element, int index, ProgramContext ctx, List<ScriptHandler> handlers)
    {
        ctx.Where = "handler " + index + ": ";
        if (element.ValueKind != JsonValueKind.Object)
        {
            ctx.Error("must be an object");
            return;
        }

        string on = GetString(element, "On") ?? string.Empty;
        if (!Enum.TryParse(on, ignoreCase: true, out NpcScriptTrigger trigger))
        {
            ctx.Error("unknown trigger '" + on + "'");
            return;
        }

        int filter = 0;
        int filter2 = 0;
        switch (trigger)
        {
            case NpcScriptTrigger.HealthBelow:
                filter = GetInt(element, "Percent", -1);
                if (filter < 1 || filter > 100)
                    ctx.Error("HealthBelow needs Percent 1..100");
                break;
            case NpcScriptTrigger.Arrived:
            case NpcScriptTrigger.Stuck:
                filter = GetInt(element, "Waypoint", -1);
                break;
            case NpcScriptTrigger.NanoEnded:
                filter = GetInt(element, "NanoId", 0);
                break;
            case NpcScriptTrigger.Timer:
                if (!TryGetRange(element, "EveryMs", out filter, out filter2) || filter < MinWaitMs)
                    ctx.Error("Timer needs EveryMs >= " + MinWaitMs + " (a number or [min, max])");
                break;
            case NpcScriptTrigger.AddDied:
                filter = -1;
                if (element.TryGetProperty("Tag", out JsonElement tagElement))
                {
                    filter = ctx.Tag(tagElement.ValueKind == JsonValueKind.String ? tagElement.GetString() : null, "AddDied");
                    if (filter >= 0)
                        ctx.TagFilters.Add((index, filter));
                }

                filter2 = GetInt(element, "DelayMs", 0);
                if (filter2 < 0 || filter2 > MaxAddDiedDelayMs)
                    ctx.Error("AddDied: DelayMs must be 0.." + MaxAddDiedDelayMs);
                break;
        }

        bool once = GetBool(element, "Once", false);
        int cooldown = Math.Max(0, GetInt(element, "CooldownMs", 0));
        int chance = Math.Clamp(GetInt(element, "ChancePercent", 100), 0, 100);
        bool interrupts = GetBool(element, "Interrupts", false);
        int priority = Math.Clamp(GetInt(element, "Priority", 0), 0, 255);

        if (!element.TryGetProperty("Do", out JsonElement doElement) || doElement.ValueKind != JsonValueKind.Array)
        {
            ctx.Error("missing Do list");
            return;
        }

        int start = ctx.Ops.Count;
        var scope = new HandlerScope();
        CompileBlock(doElement, ctx, scope, "handler " + index + " ");
        ctx.Ops.Add(new ScriptOp(ScriptOpCode.End));
        int end = ctx.Ops.Count;

        foreach ((int op, string label, bool isIf) in scope.Pending)
        {
            if (!scope.Labels.TryGetValue(label, out int target))
            {
                ctx.Where = "handler " + index + ": ";
                ctx.Error("unknown label '" + label + "'");
                continue;
            }

            ScriptOp existing = ctx.Ops[op];
            ctx.Ops[op] = isIf ? existing with { D = target } : existing with { A = target };
        }

        handlers.Add(new ScriptHandler(trigger, filter, filter2, start, end, once, cooldown, (byte)chance, interrupts, (byte)priority));
    }

    void CompileBlock(JsonElement ops, ProgramContext ctx, HandlerScope scope, string where)
    {
        int n = 0;
        foreach (JsonElement op in ops.EnumerateArray())
        {
            ctx.Where = where + "op " + n++ + ": ";
            CompileOp(op, ctx, scope);
        }
    }

    void CompileOp(JsonElement op, ProgramContext ctx, HandlerScope scope)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            ctx.Error("an op is an object with one verb");
            return;
        }

        string? verb = null;
        JsonElement value = default;
        foreach (JsonProperty property in op.EnumerateObject())
        {
            if (verb != null)
            {
                ctx.Error("an op has exactly one verb (found '" + verb + "' and '" + property.Name + "')");
                return;
            }

            verb = property.Name;
            value = property.Value;
        }

        if (verb == null)
        {
            ctx.Error("empty op");
            return;
        }

        switch (verb)
        {
            case "Label":
            {
                string? label = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(label))
                    ctx.Error("Label needs a name");
                else if (!scope.Labels.TryAdd(label, ctx.Ops.Count))
                    ctx.Error("label '" + label + "' defined twice");
                return;
            }

            case "Wait":
                if (!TryGetRange(value, out int min, out int max) || min < MinWaitMs)
                    ctx.Error("Wait needs a number or [min, max] of at least " + MinWaitMs + " ms");
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.Wait, min, max));
                return;

            case "WalkTo":
            case "RunTo":
                CompileMove(value, verb == "RunTo", ctx);
                return;

            case "Emote":
                CompileEmote(value, ctx);
                return;

            case "Say":
                CompileSay(value, ctx);
                return;

            case "SpawnWorldItem":
                CompileSpawnWorldItem(value, ctx);
                return;

            case "Gfx":
                CompileGfx(value, ctx);
                return;

            case "GfxEffect":
                if (!TryValueRef(value, ScriptParamType.Gfx, ctx, out int gfxEffectRef, ParseNanoId))
                    ctx.Error("GfxEffect: a positive gfx id or a $Gfx param");
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.GfxEffect, gfxEffectRef));
                return;

            case "SpawnNpc":
                CompileSpawnNpc(value, ctx);
                return;

            case "CastNano":
                CompileCastNano(value, ctx);
                return;

            case "Despawn":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.Despawn));
                return;

            case "SetStat":
            {
                string statName = GetString(value, "Stat") ?? string.Empty;
                if (!Enum.TryParse(statName, ignoreCase: false, out CharacterStat stat) || !Enum.IsDefined(stat))
                    ctx.Error("SetStat: unknown CharacterStat '" + statName + "'");
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.SetStat, (int)stat, GetInt(value, "Value", 0), GetBool(value, "Show", false) ? 1 : 0));
                return;
            }

            case "SetVar":
            case "AddVar":
            {
                int slot = GetInt(value, "Var", -1);
                if (slot < 0 || slot >= NpcScriptProgram.MaxVars)
                    ctx.Error(verb + ": Var must be 0.." + (NpcScriptProgram.MaxVars - 1));
                else
                    ctx.Ops.Add(new ScriptOp(verb == "SetVar" ? ScriptOpCode.SetVar : ScriptOpCode.AddVar, slot, GetInt(value, "Value", 0)));
                return;
            }

            case "If":
                CompileIf(value, ctx, scope);
                return;

            case "Goto":
            {
                string? label = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(label))
                {
                    ctx.Error("Goto needs a label");
                    return;
                }

                scope.Pending.Add((ctx.Ops.Count, label, false));
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.Goto));
                return;
            }

            case "Random":
                CompileRandom(value, ctx, scope);
                return;

            case "Restart":
            {
                string on = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
                if (!Enum.TryParse(on, ignoreCase: true, out NpcScriptTrigger trigger))
                    ctx.Error("Restart: unknown trigger '" + on + "'");
                else if (trigger == NpcScriptTrigger.AddDied)
                    ctx.Error("Restart: AddDied only starts from an add's death");
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.Restart, (int)trigger));
                return;
            }

            case "ReleaseMovement":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.ReleaseMovement));
                return;

            case "SetHome":
                if (value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), "Here", StringComparison.OrdinalIgnoreCase))
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.SetHome, 0, 0, 0, 4));
                else
                    CompileMove(value, false, ctx, false, ScriptOpCode.SetHome);
                return;

            case "End":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.End));
                return;

            default:
                ctx.Error("unknown verb '" + verb + "'");
                return;
        }
    }

    /// <summary>
    /// A move target, or its object form <c>{ "To": target, "Direct": true }</c>. Direct walks a straight line
    /// without the navmesh: the bridge for a short gap the mesh does not cover (a steep ramp between two islands).
    /// </summary>
    void CompileMove(JsonElement value, bool run, ProgramContext ctx, bool direct = false, ScriptOpCode code = ScriptOpCode.MoveTo)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("To", out JsonElement to))
            {
                ctx.Error("WalkTo: object form needs To");
                return;
            }

            bool directFlag = value.TryGetProperty("Direct", out JsonElement directElement)
                && directElement.ValueKind == JsonValueKind.True;
            CompileMove(to, run, ctx, directFlag, code);
            return;
        }

        int runFlag = (run ? 1 : 0) | (direct ? 2 : 0);
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!TryParseVector(value, out Vector3 vector))
            {
                ctx.Error("WalkTo: a position is [x, y, z]");
                return;
            }

            ctx.Ops.Add(new ScriptOp(code, ctx.Vector(vector), 0, runFlag, 0));
            return;
        }

        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        if (string.Equals(text, "Home", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Ops.Add(new ScriptOp(code, 0, 0, runFlag, 3));
            return;
        }

        if (!text.StartsWith('$'))
        {
            ctx.Error("WalkTo: expected [x, y, z], \"Home\", \"$Param\", \"$Param[i]\" or \"$Param[next]\"");
            return;
        }

        string name = text.Substring(1);
        int bracket = name.IndexOf('[');
        if (bracket < 0)
        {
            // A Vector3 parameter: one-element array, index 0.
            int slot = ctx.ParamSlot(name, ScriptParamType.Vector3, out bool found);
            if (slot >= 0)
                ctx.Ops.Add(new ScriptOp(code, ScriptOp.ParamRef(slot), 0, runFlag, 1));
            return;
        }

        if (!name.EndsWith(']'))
        {
            ctx.Error("WalkTo: malformed index in '" + text + "'");
            return;
        }

        string indexText = name.Substring(bracket + 1, name.Length - bracket - 2);
        name = name.Substring(0, bracket);
        int arraySlot = ctx.ParamSlot(name, ScriptParamType.Vector3Array, out _);
        if (arraySlot < 0)
            return;

        if (string.Equals(indexText, "next", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Ops.Add(new ScriptOp(code, ScriptOp.ParamRef(arraySlot), 0, runFlag, 2));
            return;
        }

        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0)
        {
            ctx.Error("WalkTo: index must be 'next' or a non-negative number");
            return;
        }

        ctx.Ops.Add(new ScriptOp(code, ScriptOp.ParamRef(arraySlot), index, runFlag, 1));
    }

    void CompileEmote(JsonElement value, ProgramContext ctx)
    {
        JsonElement action = value;
        int duration = DefaultEmoteMs;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("Action", out action))
            {
                ctx.Error("Emote: object form needs Action");
                return;
            }

            duration = Math.Max(0, GetInt(value, "DurationMs", DefaultEmoteMs));
        }

        if (!TryValueRef(action, ScriptParamType.SocialAction, ctx, out int animRef, ParseSocialAction))
        {
            ctx.Error("Emote: a SocialAction name, an id " + MinSocialAnim + ".." + MaxSocialAnim + " or a $SocialAction param");
            return;
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.Emote, animRef, duration));
    }

    public static bool ParseSocialAction(JsonElement element, out int id)
    {
        id = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out id))
            return id >= MinSocialAnim && id <= MaxSocialAnim;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        string text = element.GetString() ?? string.Empty;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            return id >= MinSocialAnim && id <= MaxSocialAnim;
        if (Enum.TryParse(text, ignoreCase: true, out SocialAction action) && Enum.IsDefined(action))
        {
            id = (int)action;
            return id >= MinSocialAnim && id <= MaxSocialAnim;
        }

        // Short names: "Shrug" for GesturesShrug, "Wave" for GreetingWave.
        foreach (SocialAction candidate in Enum.GetValues<SocialAction>())
        {
            string name = candidate.ToString();
            if (name.EndsWith(text, StringComparison.OrdinalIgnoreCase) && (int)candidate >= MinSocialAnim && (int)candidate <= MaxSocialAnim)
            {
                id = (int)candidate;
                return true;
            }
        }

        return false;
    }

    void CompileSay(JsonElement value, ProgramContext ctx)
    {
        JsonElement textElement = value;
        int type = (int)ChatMessageType.Say;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("Text", out textElement))
            {
                ctx.Error("Say: object form needs Text");
                return;
            }

            string typeName = GetString(value, "Type") ?? "Say";
            if (!Enum.TryParse(typeName, ignoreCase: true, out ChatMessageType chatType))
            {
                ctx.Error("Say: unknown chat Type '" + typeName + "'");
                return;
            }

            type = (int)chatType;
        }

        if (textElement.ValueKind != JsonValueKind.String)
        {
            ctx.Error("Say: Text must be a string");
            return;
        }

        string text = textElement.GetString() ?? string.Empty;
        if (text.StartsWith('$'))
        {
            int slot = ctx.ParamSlot(text.Substring(1), ScriptParamType.Text, out _);
            if (slot >= 0)
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.Say, ScriptOp.ParamRef(slot), type));
            return;
        }

        if (text.Length == 0 || text.Length > MaxSayLength)
        {
            ctx.Error("Say: text must be 1.." + MaxSayLength + " characters");
            return;
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.Say, ctx.String(text), type));
    }

    void CompileSpawnWorldItem(JsonElement value, ProgramContext ctx)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Item", out JsonElement item) || item.ValueKind != JsonValueKind.String)
        {
            ctx.Error("SpawnWorldItem: needs { Item, LifetimeSeconds, Offset?, Quality? }");
            return;
        }

        string itemText = item.GetString() ?? string.Empty;
        int itemRef;
        if (itemText.StartsWith('$'))
        {
            int slot = ctx.ParamSlot(itemText.Substring(1), ScriptParamType.ItemHash, out _);
            if (slot < 0)
                return;
            itemRef = ScriptOp.ParamRef(slot);
        }
        else
        {
            if (!_gameData.CanResolveItemHash(itemText))
            {
                ctx.Error("SpawnWorldItem: item hash '" + itemText + "' does not resolve");
                return;
            }

            itemRef = ctx.String(itemText);
        }

        int offsetRef = ScriptOp.None;
        if (value.TryGetProperty("Offset", out JsonElement offset))
        {
            if (!TryParseVector(offset, out Vector3 vector))
            {
                ctx.Error("SpawnWorldItem: Offset is [x, y, z]");
                return;
            }

            offsetRef = ctx.Vector(vector);
        }

        int lifetime = GetInt(value, "LifetimeSeconds", 0);
        if (lifetime < 1 || lifetime > MaxWorldItemLifetimeSeconds)
        {
            ctx.Error("SpawnWorldItem: LifetimeSeconds must be 1.." + MaxWorldItemLifetimeSeconds);
            return;
        }

        int quality = Math.Max(1, GetInt(value, "Quality", 1));
        ctx.Ops.Add(new ScriptOp(ScriptOpCode.SpawnWorldItem, itemRef, offsetRef, quality, lifetime));
    }

    /// <summary>
    /// <c>{ "Hash", "At" | "Offset"?, "Count"?, "Radius"?, "LifetimeSeconds"?, "DespawnWithOwner"?, "Level" | "LevelPercent"?, "JoinFight"? }</c>.
    /// The hash is a constant or a $NpcHash param. DespawnWithOwner is true (at once), false (independent) or the
    /// seconds the spawn outlives its owner's death, reset or despawn; default true. Tag names the spawns for the
    /// owner's AddDied handlers; several SpawnNpc may share one.
    /// </summary>
    void CompileSpawnNpc(JsonElement value, ProgramContext ctx)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Hash", out JsonElement hash) || hash.ValueKind != JsonValueKind.String)
        {
            ctx.Error("SpawnNpc: needs { Hash, At? | Offset?, Count?, Radius?, LifetimeSeconds?, DespawnWithOwner?, Level?, JoinFight?, Tag? }");
            return;
        }

        string hashText = (hash.GetString() ?? string.Empty).Trim();
        int hashRef;
        if (hashText.StartsWith('$'))
        {
            int slot = ctx.ParamSlot(hashText.Substring(1), ScriptParamType.NpcHash, out _);
            if (slot < 0)
                return;
            hashRef = ScriptOp.ParamRef(slot);
        }
        else
        {
            if (!_gameData.HasMobTemplate(hashText))
            {
                ctx.Error("SpawnNpc: no NPC template for hash '" + hashText + "'");
                return;
            }

            hashRef = ctx.String(hashText);
        }

        bool at = value.TryGetProperty("At", out JsonElement atElement);
        bool offset = value.TryGetProperty("Offset", out JsonElement offsetElement);
        if (at && offset)
        {
            ctx.Error("SpawnNpc: At or Offset, not both");
            return;
        }

        int centreRef = ScriptOp.None;
        if (at || offset)
        {
            if (!TryParseVector(at ? atElement : offsetElement, out Vector3 centre))
            {
                ctx.Error("SpawnNpc: " + (at ? "At" : "Offset") + " is [x, y, z]");
                return;
            }

            centreRef = ctx.Vector(centre);
        }

        int count = GetInt(value, "Count", 1);
        if (count < 1 || count > MaxSpawnCount)
        {
            ctx.Error("SpawnNpc: Count must be 1.." + MaxSpawnCount);
            return;
        }

        float radius = (float)GetDouble(value, "Radius", 0);
        if (radius < 0 || radius > MaxSpawnRadius)
        {
            ctx.Error("SpawnNpc: Radius must be 0.." + MaxSpawnRadius.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (count > 1 && radius <= 0)
        {
            ctx.Error("SpawnNpc: a Count above 1 needs a Radius (the spawns stand on that circle)");
            return;
        }

        int lifetime = GetInt(value, "LifetimeSeconds", 0);
        if (lifetime < 0 || lifetime > MaxNpcLifetimeSeconds)
        {
            ctx.Error("SpawnNpc: LifetimeSeconds must be 1.." + MaxNpcLifetimeSeconds);
            return;
        }

        int ownerDelay = 0;
        if (value.TryGetProperty("DespawnWithOwner", out JsonElement withOwner))
        {
            switch (withOwner.ValueKind)
            {
                case JsonValueKind.True:
                    break;
                case JsonValueKind.False:
                    ownerDelay = -1;
                    break;
                case JsonValueKind.Number when withOwner.TryGetInt32(out ownerDelay) && ownerDelay >= 0 && ownerDelay <= MaxNpcLifetimeSeconds:
                    break;
                default:
                    ctx.Error("SpawnNpc: DespawnWithOwner is true, false or seconds 0.." + MaxNpcLifetimeSeconds);
                    return;
            }
        }

        int level = GetInt(value, "Level", 0);
        if (level < 0)
        {
            ctx.Error("SpawnNpc: Level must be positive");
            return;
        }

        int levelPercent = GetInt(value, "LevelPercent", 0);
        if (levelPercent < 0 || levelPercent > MaxSpawnLevelPercent)
        {
            ctx.Error("SpawnNpc: LevelPercent must be 1.." + MaxSpawnLevelPercent);
            return;
        }

        if (level > 0 && levelPercent > 0)
        {
            ctx.Error("SpawnNpc: Level or LevelPercent, not both");
            return;
        }

        int tag = -1;
        if (value.TryGetProperty("Tag", out JsonElement tagElement))
        {
            tag = ctx.Tag(tagElement.ValueKind == JsonValueKind.String ? tagElement.GetString() : null, "SpawnNpc");
            if (tag < 0)
                return;
            ctx.SpawnedTags.Add(tag);
        }

        var spec = new SpawnNpcSpec(at, count, radius, lifetime, ownerDelay, level, GetBool(value, "JoinFight", true), levelPercent, tag);
        ctx.Spawns.Add(spec);
        ctx.Ops.Add(new ScriptOp(ScriptOpCode.SpawnNpc, hashRef, centreRef, ctx.Spawns.Count - 1));
    }

    /// <summary>
    /// <c>{ "Nano", "Target"?, "MinDistance"?, "Warp"? }</c>. Nano is one id / $Int param or a list of them (a random
    /// pick per recipient). Target is Self (default), Target (the current fighting target), RandomHostile or Playfield
    /// (every living player on the playfield, one cast bar); MinDistance (metres) only narrows RandomHostile; Warp
    /// "ToSelf" moves the recipient to the NPC's feet once the nano has landed, and is refused with Playfield.
    /// </summary>
    void CompileCastNano(JsonElement value, ProgramContext ctx)
    {
        JsonElement nanoElement = value;
        NpcScriptTarget target = NpcScriptTarget.Self;
        int minDistance = 0;
        int flags = 0;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("Nano", out nanoElement))
            {
                ctx.Error("CastNano: object form needs Nano");
                return;
            }

            string targetName = GetString(value, "Target") ?? "Self";
            if (!Enum.TryParse(targetName, ignoreCase: true, out target) || !Enum.IsDefined(target))
            {
                ctx.Error("CastNano: Target is Self, Target, RandomHostile, Playfield or RandomNearby");
                return;
            }

            minDistance = GetInt(value, "MinDistance", 0);
            if (minDistance < 0 || minDistance > MaxCastDistance)
            {
                ctx.Error("CastNano: MinDistance must be 0.." + MaxCastDistance);
                return;
            }

            if (minDistance > 0 && target != NpcScriptTarget.RandomHostile)
            {
                ctx.Error("CastNano: MinDistance only applies to Target RandomHostile");
                return;
            }

            bool hasMax = value.TryGetProperty("MaxDistance", out _);
            bool hasHashes = value.TryGetProperty("Hashes", out JsonElement hashesElement);
            if ((hasMax || hasHashes) && target != NpcScriptTarget.RandomNearby)
            {
                ctx.Error("CastNano: MaxDistance and Hashes only apply to Target RandomNearby");
                return;
            }

            if (target == NpcScriptTarget.RandomNearby)
            {
                // C carries the maximum distance for a nearby pick.
                minDistance = GetInt(value, "MaxDistance", MaxCastDistance);
                if (minDistance < 1 || minDistance > MaxCastDistance)
                {
                    ctx.Error("CastNano: MaxDistance must be 1.." + MaxCastDistance);
                    return;
                }

                // Hashes is required: an unfiltered "any NPC near me" would reach every bystander in the cell.
                if (!hasHashes || hashesElement.ValueKind != JsonValueKind.Array || hashesElement.GetArrayLength() < 1
                    || hashesElement.GetArrayLength() > MaxCastHashList)
                {
                    ctx.Error("CastNano: Target RandomNearby needs Hashes, a list of 1.." + MaxCastHashList + " NPC template hashes");
                    return;
                }

                var hashes = new List<string>();
                foreach (JsonElement item in hashesElement.EnumerateArray())
                {
                    string hashText = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? string.Empty).Trim() : string.Empty;
                    if (!_gameData.HasMobTemplate(hashText))
                    {
                        ctx.Error("CastNano: Hashes: no NPC template for hash '" + hashText + "'");
                        return;
                    }

                    if (!hashes.Contains(hashText))
                        hashes.Add(hashText);
                }

                ctx.HashLists.Add(hashes.ToArray());
                flags |= ctx.HashLists.Count << CastHashListShift;
            }

            if (value.TryGetProperty("Warp", out JsonElement warp))
            {
                if (warp.ValueKind != JsonValueKind.String || !string.Equals(warp.GetString(), "ToSelf", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Error("CastNano: Warp is \"ToSelf\"");
                    return;
                }

                if (target == NpcScriptTarget.Self)
                {
                    ctx.Error("CastNano: Warp needs a Target other than Self");
                    return;
                }

                // A warp follows one recipient's landing; a playfield cast has many.
                if (target == NpcScriptTarget.Playfield)
                {
                    ctx.Error("CastNano: Warp does not apply to Target Playfield");
                    return;
                }

                flags |= CastWarpToSelf;
            }
        }

        if (nanoElement.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.Object)
        {
            int count = nanoElement.GetArrayLength();
            if (count < 1 || count > MaxCastNanoList)
            {
                ctx.Error("CastNano: a Nano list holds 1.." + MaxCastNanoList + " ids");
                return;
            }

            var refs = new int[count];
            int i = 0;
            foreach (JsonElement item in nanoElement.EnumerateArray())
            {
                if (!TryValueRef(item, ScriptParamType.Int, ctx, out refs[i++], ParseNanoId))
                {
                    ctx.Error("CastNano: each Nano list entry is a positive nano id or an $Int param");
                    return;
                }
            }

            ctx.NanoLists.Add(refs);
            ctx.Ops.Add(new ScriptOp(ScriptOpCode.CastNano, ctx.NanoLists.Count - 1, (int)target, minDistance, flags | CastNanoList));
            return;
        }

        if (!TryValueRef(nanoElement, ScriptParamType.Int, ctx, out int nanoRef, ParseNanoId))
        {
            ctx.Error("CastNano: a positive nano id, an $Int param or (object form) a list of them");
            return;
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.CastNano, nanoRef, (int)target, minDistance, flags));
    }

    /// <summary><see cref="ScriptOpCode.CastNano"/> D flag: warp the recipient to the NPC's feet when the nano lands.</summary>
    public const int CastWarpToSelf = 1;

    /// <summary><see cref="ScriptOpCode.CastNano"/> D flag: A indexes <see cref="NpcScriptProgram.NanoLists"/>.</summary>
    public const int CastNanoList = 2;

    /// <summary>Longest CastNano Nano list.</summary>
    public const int MaxCastNanoList = 16;

    /// <summary><see cref="ScriptOpCode.CastNano"/> D: bits from here hold 1 + a <see cref="NpcScriptProgram.HashLists"/> index.</summary>
    public const int CastHashListShift = 8;

    /// <summary>Longest CastNano RandomNearby Hashes list.</summary>
    public const int MaxCastHashList = 16;

    static bool ParseNanoId(JsonElement element, out int id)
    {
        id = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out id) && id > 0;
    }

    void CompileGfx(JsonElement value, ProgramContext ctx)
    {
        JsonElement idElement = value;
        GfxAnchor anchor = GfxAnchor.OnSelf;
        int vector1 = ScriptOp.None;
        int vector2 = ScriptOp.None;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("Id", out idElement))
            {
                ctx.Error("Gfx: object form needs Id");
                return;
            }

            bool at = value.TryGetProperty("At", out JsonElement atElement);
            bool offset = value.TryGetProperty("Offset", out JsonElement offsetElement);
            bool from = value.TryGetProperty("From", out JsonElement fromElement);
            bool to = value.TryGetProperty("To", out JsonElement toElement);
            bool onSelf = string.Equals(GetString(value, "On"), "Self", StringComparison.OrdinalIgnoreCase);
            if (from || to)
            {
                if (!from || !to || !TryParseVector(fromElement, out Vector3 fromVector) || !TryParseVector(toElement, out Vector3 toVector))
                {
                    ctx.Error("Gfx: From and To are both [x, y, z]");
                    return;
                }

                anchor = GfxAnchor.Between;
                vector1 = ctx.Vector(fromVector);
                vector2 = ctx.Vector(toVector);
            }
            else if (at)
            {
                if (!TryParseVector(atElement, out Vector3 atVector))
                {
                    ctx.Error("Gfx: At is [x, y, z]");
                    return;
                }

                anchor = onSelf ? GfxAnchor.OnSelfAtPosition : GfxAnchor.AtPosition;
                vector1 = ctx.Vector(atVector);
            }
            else if (offset)
            {
                if (!TryParseVector(offsetElement, out Vector3 offsetVector))
                {
                    ctx.Error("Gfx: Offset is [x, y, z]");
                    return;
                }

                anchor = GfxAnchor.AtOffset;
                vector1 = ctx.Vector(offsetVector);
            }
        }

        if (!TryValueRef(idElement, ScriptParamType.Gfx, ctx, out int gfxRef, ParseGfxId))
        {
            ctx.Error("Gfx: a positive id or a $Gfx param");
            return;
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.Gfx, gfxRef, (int)anchor, vector1, vector2));
    }

    public static bool ParseGfxId(JsonElement element, out int id)
    {
        id = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out id))
            return id > 0 && id != GfxTriggerMessage.NoGfx;
        return element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            && id > 0 && id != GfxTriggerMessage.NoGfx;
    }

    void CompileIf(JsonElement value, ProgramContext ctx, HandlerScope scope)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            ctx.Error("If: needs an object with one condition and Goto");
            return;
        }

        string? label = GetString(value, "Goto");
        if (string.IsNullOrWhiteSpace(label))
        {
            ctx.Error("If: needs Goto label");
            return;
        }

        ScriptPredicate predicate;
        int b = 0;
        int c = 0;
        if (value.TryGetProperty("Var", out _))
        {
            b = GetInt(value, "Var", -1);
            if (b < 0 || b >= NpcScriptProgram.MaxVars)
            {
                ctx.Error("If: Var must be 0.." + (NpcScriptProgram.MaxVars - 1));
                return;
            }

            if (value.TryGetProperty("Eq", out _)) { predicate = ScriptPredicate.VarEquals; c = GetInt(value, "Eq", 0); }
            else if (value.TryGetProperty("Ne", out _)) { predicate = ScriptPredicate.VarNotEquals; c = GetInt(value, "Ne", 0); }
            else if (value.TryGetProperty("Lt", out _)) { predicate = ScriptPredicate.VarLessThan; c = GetInt(value, "Lt", 0); }
            else if (value.TryGetProperty("Gt", out _)) { predicate = ScriptPredicate.VarGreaterThan; c = GetInt(value, "Gt", 0); }
            else
            {
                ctx.Error("If: Var needs one of Eq, Ne, Lt, Gt");
                return;
            }
        }
        else if (value.TryGetProperty("HealthBelow", out _))
        {
            predicate = ScriptPredicate.HealthBelow;
            b = GetInt(value, "HealthBelow", 0);
            if (b < 1 || b > 100)
            {
                ctx.Error("If: HealthBelow is 1..100");
                return;
            }
        }
        else if (value.TryGetProperty("HasTarget", out _))
        {
            predicate = ScriptPredicate.HasTarget;
            b = GetBool(value, "HasTarget", true) ? 1 : 0;
        }
        else if (value.TryGetProperty("Chance", out _))
        {
            predicate = ScriptPredicate.Chance;
            b = Math.Clamp(GetInt(value, "Chance", 0), 0, 100);
        }
        else
        {
            ctx.Error("If: unknown condition");
            return;
        }

        scope.Pending.Add((ctx.Ops.Count, label, true));
        ctx.Ops.Add(new ScriptOp(ScriptOpCode.If, (int)predicate, b, c));
    }

    /// <summary>
    /// <c>[ block, ... ]</c> where a block is a list of ops (weight 1) or <c>{ "Weight": n, "Do": [ ops ] }</c>. All
    /// weights 1 compiles to the even pick; otherwise the jump table also carries the running weight totals and C is
    /// the sum.
    /// </summary>
    void CompileRandom(JsonElement value, ProgramContext ctx, HandlerScope scope)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            ctx.Error("Random: needs a non-empty list of blocks (op lists or { Weight, Do })");
            return;
        }

        int count = value.GetArrayLength();
        int[] weights = new int[count];
        bool weighted = false;
        long total = 0;
        int index = 0;
        foreach (JsonElement block in value.EnumerateArray())
        {
            int weight = 1;
            if (block.ValueKind == JsonValueKind.Object)
            {
                if (!block.TryGetProperty("Weight", out JsonElement w) || !w.TryGetInt32(out weight)
                    || weight < 1 || weight > MaxRandomWeight)
                {
                    ctx.Error("Random: block " + index + " Weight must be an integer 1.." + MaxRandomWeight);
                    return;
                }

                if (!block.TryGetProperty("Do", out JsonElement doOps) || doOps.ValueKind != JsonValueKind.Array)
                {
                    ctx.Error("Random: block " + index + " needs Do, a list of ops");
                    return;
                }

                weighted |= weight != 1;
            }
            else if (block.ValueKind != JsonValueKind.Array)
            {
                ctx.Error("Random: each block is a list of ops or { Weight, Do }");
                return;
            }

            weights[index++] = weight;
            total += weight;
        }

        // Even: Jumps[B..B+A) are the block starts. Weighted: Jumps[B+A..B+2A) are the running totals and C the sum.
        int table = ctx.Jumps.Count;
        for (int i = 0; i < count; i++)
            ctx.Jumps.Add(0);
        if (weighted)
        {
            int running = 0;
            for (int i = 0; i < count; i++)
            {
                running += weights[i];
                ctx.Jumps.Add(running);
            }
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.Random, count, table, weighted ? (int)total : 0));

        // Each block ends with a Goto to the op after the last block; patched once that index is known.
        var exits = new List<int>();
        int blockIndex = 0;
        string where = ctx.Where;
        foreach (JsonElement block in value.EnumerateArray())
        {
            JsonElement blockOps = block.ValueKind == JsonValueKind.Object ? block.GetProperty("Do") : block;
            ctx.Jumps[table + blockIndex] = ctx.Ops.Count;
            CompileBlock(blockOps, ctx, scope, where + "block " + blockIndex + " ");
            exits.Add(ctx.Ops.Count);
            ctx.Ops.Add(new ScriptOp(ScriptOpCode.Goto));
            blockIndex++;
        }

        ctx.Where = where;
        int after = ctx.Ops.Count;
        foreach (int exit in exits)
            ctx.Ops[exit] = new ScriptOp(ScriptOpCode.Goto, after);
    }

    /// <summary>A handler loop without a blocking op would spin against the op budget every tick: reject it.</summary>
    static void CheckSpinningLoops(ProgramContext ctx, List<ScriptHandler> handlers)
    {
        foreach (ScriptHandler handler in handlers)
        {
            for (int pc = handler.Start; pc < handler.End; pc++)
            {
                ScriptOp op = ctx.Ops[pc];
                if (op.Code != ScriptOpCode.Goto || op.A > pc)
                    continue;
                bool blocks = false;
                for (int i = op.A; i <= pc; i++)
                {
                    if (ctx.Ops[i].Code is ScriptOpCode.Wait or ScriptOpCode.MoveTo or ScriptOpCode.Emote)
                    {
                        blocks = true;
                        break;
                    }
                }

                if (!blocks)
                    ctx.Errors.Add("handler " + handler.Trigger + ": a backwards Goto loop with no Wait, WalkTo or Emote in it");
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Bindings
    // ----------------------------------------------------------------------------------------------------------------

    public NpcScriptBinding? CompileBinding(string file, int index, JsonElement element, NpcScriptProgram? program, List<string> problems)
    {
        string where = file + " binding " + index + ": ";
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(where + "must be an object");
            return null;
        }

        string? hash = GetString(element, "Hash");
        string? spawnPoint = GetString(element, "SpawnPoint");
        string? npcKey = GetString(element, "NpcKey");
        string? feature = GetString(element, "Feature");
        int targets = (hash != null ? 1 : 0) + (spawnPoint != null ? 1 : 0) + (npcKey != null ? 1 : 0) + (feature != null ? 1 : 0);
        if (targets != 1)
        {
            problems.Add(where + "exactly one of Hash, SpawnPoint, NpcKey, Feature");
            return null;
        }

        int? playfield = element.TryGetProperty("Playfield", out JsonElement pfElement) && pfElement.ValueKind == JsonValueKind.Number
            ? pfElement.GetInt32()
            : null;
        ScriptBindingTarget target;
        string key;
        if (hash != null)
        {
            target = ScriptBindingTarget.Hash;
            key = hash.Trim();
            if (!_gameData.HasMobTemplate(key))
            {
                problems.Add(where + "no NPC template for hash '" + key + "'");
                return null;
            }
        }
        else if (spawnPoint != null)
        {
            target = ScriptBindingTarget.SpawnPoint;
            key = SpawnPointKey.Normalize(spawnPoint);
            if (playfield == null)
            {
                problems.Add(where + "SpawnPoint needs Playfield");
                return null;
            }

            if (key.Length == 0)
            {
                problems.Add(where + "SpawnPoint key is malformed: HASH@x,y,z[#n]");
                return null;
            }
        }
        else if (npcKey != null)
        {
            target = ScriptBindingTarget.NpcKey;
            key = npcKey.Trim();
            if (playfield == null)
            {
                problems.Add(where + "NpcKey needs Playfield");
                return null;
            }
        }
        else
        {
            // A feature overlay runs beside the NPC's own program, so it may not take the movement lease or carry
            // plain movement: that stays with the primary binding.
            target = ScriptBindingTarget.Feature;
            if (!Enum.TryParse(feature!.Trim(), ignoreCase: false, out NpcFeature parsed) || !Enum.IsDefined(parsed))
            {
                problems.Add(where + "unknown Feature '" + feature + "' (an NpcFeature name from MonsterCombatRules.json)");
                return null;
            }

            key = parsed.ToString();
            if (program == null)
            {
                problems.Add(where + "Feature needs a program");
                return null;
            }

            if (program.HasMovement)
            {
                problems.Add(where + "Feature overlay program '" + program.Id + "' moves the NPC (WalkTo/RunTo/SetHome/ReleaseMovement); only the NPC's own binding may");
                return null;
            }

            if (element.TryGetProperty("Movement", out _))
            {
                problems.Add(where + "Feature binding cannot carry Movement");
                return null;
            }
        }

        ScriptPatrol? patrol = null;
        ScriptWander? wander = null;
        if (element.TryGetProperty("Movement", out JsonElement movement) && movement.ValueKind == JsonValueKind.Object)
        {
            if (movement.TryGetProperty("Patrol", out JsonElement patrolElement))
            {
                if (!patrolElement.TryGetProperty("Waypoints", out JsonElement waypoints) || !TryParseVectors(waypoints, out Vector3[] route) || route.Length == 0)
                {
                    problems.Add(where + "Movement.Patrol needs Waypoints [[x,y,z], ...]");
                    return null;
                }

                patrol = new ScriptPatrol(route, GetBool(patrolElement, "Run", false));
            }
            else if (movement.TryGetProperty("Wander", out JsonElement wanderElement))
            {
                float radius = (float)GetDouble(wanderElement, "Radius", 0);
                if (radius <= 0)
                {
                    problems.Add(where + "Movement.Wander needs Radius > 0");
                    return null;
                }

                wander = new ScriptWander(radius, GetDouble(wanderElement, "MinPauseSeconds", 3), GetDouble(wanderElement, "MaxPauseSeconds", 12));
            }
            else
            {
                problems.Add(where + "Movement is { Patrol } or { Wander }");
                return null;
            }
        }

        if (program == null && patrol == null && wander == null)
        {
            problems.Add(where + "binds nothing (no program and no Movement)");
            return null;
        }

        ScriptArgs args = ScriptArgs.Empty;
        if (program != null)
        {
            args = CompileArgs(where, element, program, problems, out bool ok)!;
            if (!ok)
                return null;
        }

        return new NpcScriptBinding(program, args, target, key, playfield, patrol, wander, GetString(element, "Provenance"));
    }

    ScriptArgs? CompileArgs(string where, JsonElement binding, NpcScriptProgram program, List<string> problems, out bool ok)
    {
        ScriptArgs? result = CompileArgs(where, binding, program, problems);
        ok = result != null;
        return result;
    }

    ScriptArgs? CompileArgs(string where, JsonElement binding, NpcScriptProgram program, List<string> problems)
    {
        bool ok = true;
        int n = program.Params.Length;
        var vectors = new Vector3[]?[n];
        var ints = new int[n];
        var strings = new string?[n];
        bool hasArgs = binding.TryGetProperty("Args", out JsonElement args) && args.ValueKind == JsonValueKind.Object;
        for (int i = 0; i < n; i++)
        {
            ScriptParam param = program.Params[i];
            if (!hasArgs || !args.TryGetProperty(param.Name, out JsonElement value))
            {
                problems.Add(where + "missing Args." + param.Name + " (" + param.Type + ")");
                ok = false;
                continue;
            }

            switch (param.Type)
            {
                case ScriptParamType.Vector3Array:
                    if (!TryParseVectors(value, out Vector3[] route) || route.Length == 0)
                        Fail("a non-empty [[x,y,z], ...]");
                    else
                        vectors[i] = route;
                    break;
                case ScriptParamType.Vector3:
                    if (!TryParseVector(value, out Vector3 vector))
                        Fail("[x, y, z]");
                    else
                        vectors[i] = new[] { vector };
                    break;
                case ScriptParamType.ItemHash:
                {
                    string hash = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
                    if (!_gameData.CanResolveItemHash(hash))
                        Fail("an item hash that resolves");
                    else
                        strings[i] = hash;
                    break;
                }

                case ScriptParamType.SocialAction:
                    if (!ParseSocialAction(value, out int anim))
                        Fail("a SocialAction name or id " + MinSocialAnim + ".." + MaxSocialAnim);
                    else
                        ints[i] = anim;
                    break;
                case ScriptParamType.Int:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out ints[i]))
                        Fail("an integer");
                    break;
                case ScriptParamType.Gfx:
                    if (!ParseGfxId(value, out ints[i]))
                        Fail("a positive gfx id");
                    break;
                case ScriptParamType.Text:
                {
                    string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
                    if (text.Length == 0 || text.Length > MaxSayLength)
                        Fail("text of 1.." + MaxSayLength + " characters");
                    else
                        strings[i] = text;
                    break;
                }

                case ScriptParamType.NpcHash:
                {
                    string hash = value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
                    if (!_gameData.HasMobTemplate(hash))
                        Fail("an NPC template hash");
                    else
                        strings[i] = hash;
                    break;
                }
            }

            void Fail(string expected)
            {
                problems.Add(where + "Args." + param.Name + " must be " + expected);
                ok = false;
            }
        }

        return ok ? new ScriptArgs(vectors, ints, strings) : null;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // JSON helpers
    // ----------------------------------------------------------------------------------------------------------------

    delegate bool ParseInt(JsonElement element, out int value);

    /// <summary>
    /// A constant parsed by <paramref name="parse"/>, or a <c>$Param</c> of <paramref name="paramType"/>. A bad
    /// <c>$Param</c> is reported by <see cref="ProgramContext.ParamSlot"/> and still returns true, so the caller
    /// does not add a second line for it.
    /// </summary>
    static bool TryValueRef(JsonElement element, ScriptParamType paramType, ProgramContext ctx, out int reference, ParseInt parse)
    {
        reference = 0;
        if (element.ValueKind == JsonValueKind.String && (element.GetString() ?? string.Empty).StartsWith('$'))
        {
            int slot = ctx.ParamSlot(element.GetString()!.Substring(1), paramType, out _);
            reference = slot < 0 ? 0 : ScriptOp.ParamRef(slot);
            return true;
        }

        if (!parse(element, out int value))
            return false;
        reference = value;
        return true;
    }

    public static bool TryParseVector(JsonElement element, out Vector3 vector)
    {
        vector = default!;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            return false;
        var parts = new double[3];
        int i = 0;
        foreach (JsonElement part in element.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number)
                return false;
            parts[i++] = part.GetDouble();
        }

        vector = new Vector3(parts[0], parts[1], parts[2]);
        return true;
    }

    public static bool TryParseVectors(JsonElement element, out Vector3[] vectors)
    {
        vectors = Array.Empty<Vector3>();
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<Vector3>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (!TryParseVector(item, out Vector3 vector))
                return false;
            list.Add(vector);
        }

        vectors = list.ToArray();
        return true;
    }

    static bool TryGetRange(JsonElement element, out int min, out int max)
    {
        min = 0;
        max = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out min))
        {
            max = min;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
            return false;
        int i = 0;
        foreach (JsonElement part in element.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetInt32(out int value))
                return false;
            if (i++ == 0)
                min = value;
            else
                max = value;
        }

        if (max < min)
            (min, max) = (max, min);
        return true;
    }

    static bool TryGetRange(JsonElement parent, string name, out int min, out int max)
    {
        min = 0;
        max = 0;
        return parent.TryGetProperty(name, out JsonElement element) && TryGetRange(element, out min, out max);
    }

    static string? GetString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    static int GetInt(JsonElement parent, string name, int fallback)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : fallback;

    static double GetDouble(JsonElement parent, string name, double fallback)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : fallback;

    static bool GetBool(JsonElement parent, string name, bool fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement element))
            return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }
}
