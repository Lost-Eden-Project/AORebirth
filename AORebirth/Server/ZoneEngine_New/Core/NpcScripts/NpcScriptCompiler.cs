namespace ZoneEngine_New.Core.NpcScripts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.GameData;

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
            ctx.Jumps.ToArray());
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

        public string Where { get; set; } = string.Empty;

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

            case "Despawn":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.Despawn));
                return;

            case "SetStat":
            {
                string statName = GetString(value, "Stat") ?? string.Empty;
                if (!Enum.TryParse(statName, ignoreCase: false, out CharacterStat stat) || !Enum.IsDefined(stat))
                    ctx.Error("SetStat: unknown CharacterStat '" + statName + "'");
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.SetStat, (int)stat, GetInt(value, "Value", 0)));
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
                else
                    ctx.Ops.Add(new ScriptOp(ScriptOpCode.Restart, (int)trigger));
                return;
            }

            case "ReleaseMovement":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.ReleaseMovement));
                return;

            case "End":
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.End));
                return;

            default:
                ctx.Error("unknown verb '" + verb + "'");
                return;
        }
    }

    void CompileMove(JsonElement value, bool run, ProgramContext ctx)
    {
        int runFlag = run ? 1 : 0;
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!TryParseVector(value, out Vector3 vector))
            {
                ctx.Error("WalkTo: a position is [x, y, z]");
                return;
            }

            ctx.Ops.Add(new ScriptOp(ScriptOpCode.MoveTo, ctx.Vector(vector), 0, runFlag, 0));
            return;
        }

        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        if (string.Equals(text, "Home", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Ops.Add(new ScriptOp(ScriptOpCode.MoveTo, 0, 0, runFlag, 3));
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
                ctx.Ops.Add(new ScriptOp(ScriptOpCode.MoveTo, ScriptOp.ParamRef(slot), 0, runFlag, 1));
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
            ctx.Ops.Add(new ScriptOp(ScriptOpCode.MoveTo, ScriptOp.ParamRef(arraySlot), 0, runFlag, 2));
            return;
        }

        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0)
        {
            ctx.Error("WalkTo: index must be 'next' or a non-negative number");
            return;
        }

        ctx.Ops.Add(new ScriptOp(ScriptOpCode.MoveTo, ScriptOp.ParamRef(arraySlot), index, runFlag, 1));
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

    void CompileRandom(JsonElement value, ProgramContext ctx, HandlerScope scope)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            ctx.Error("Random: needs a non-empty list of op lists");
            return;
        }

        int count = value.GetArrayLength();
        int table = ctx.Jumps.Count;
        for (int i = 0; i < count; i++)
            ctx.Jumps.Add(0);
        ctx.Ops.Add(new ScriptOp(ScriptOpCode.Random, count, table));

        // Each block ends with a Goto to the op after the last block; patched once that index is known.
        var exits = new List<int>();
        int block = 0;
        string where = ctx.Where;
        foreach (JsonElement blockOps in value.EnumerateArray())
        {
            if (blockOps.ValueKind != JsonValueKind.Array)
            {
                ctx.Where = where;
                ctx.Error("Random: each block is a list of ops");
                return;
            }

            ctx.Jumps[table + block] = ctx.Ops.Count;
            CompileBlock(blockOps, ctx, scope, where + "block " + block + " ");
            exits.Add(ctx.Ops.Count);
            ctx.Ops.Add(new ScriptOp(ScriptOpCode.Goto));
            block++;
        }

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
        int targets = (hash != null ? 1 : 0) + (spawnPoint != null ? 1 : 0) + (npcKey != null ? 1 : 0);
        if (targets != 1)
        {
            problems.Add(where + "exactly one of Hash, SpawnPoint, NpcKey");
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
        else
        {
            target = ScriptBindingTarget.NpcKey;
            key = npcKey!.Trim();
            if (playfield == null)
            {
                problems.Add(where + "NpcKey needs Playfield");
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
