namespace ZoneEngine_New.Core.NpcScripts;

using System;
using System.Collections.Generic;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// What starts a handler. Each is one bit of <see cref="NpcScriptProgram.TriggerMask"/>, so a runner answers an
/// unsubscribed <c>Raise</c> with one AND.
/// </summary>
public enum NpcScriptTrigger
{
    Spawned = 0,
    Arrived = 1,
    Stuck = 2,
    HealthBelow = 3,
    Death = 4,
    Reset = 5,
    CombatStart = 6,
    CombatEnd = 7,
    NanoEnded = 8,
    Timer = 9,
    Used = 10
}

/// <summary>One compiled verb. Operand meanings per code are documented on <see cref="ScriptOp"/>.</summary>
public enum ScriptOpCode : byte
{
    /// <summary>Handler ends.</summary>
    End = 0,

    /// <summary>Blocking delay. A = min ms, B = max ms.</summary>
    Wait,

    /// <summary>
    /// Blocking walk. D = kind: 0 program vector A; 1 param vector array A at index B; 2 param vector array A at the
    /// runner's waypoint cursor (advancing it); 3 home. C = flags: 1 run (else walk), 2 direct (straight line, no navmesh).
    /// </summary>
    MoveTo,

    /// <summary>Blocking emote. A = anim id value ref, B = duration ms.</summary>
    Emote,

    /// <summary>A = text (string table index or Text param ref), B = chat type.</summary>
    Say,

    /// <summary>A = item hash (string table index or ItemHash param ref), B = offset vector ref or <see cref="ScriptOp.None"/>, C = quality, D = lifetime seconds.</summary>
    SpawnWorldItem,

    /// <summary>A = gfx id value ref, B = anchor mode (<see cref="GfxAnchor"/>), C = vector ref 1, D = vector ref 2.</summary>
    Gfx,

    /// <summary>The NPC leaves the world as a GM despawn would; the handler ends.</summary>
    Despawn,

    /// <summary>A = CharacterStat, B = value.</summary>
    SetStat,

    /// <summary>A = var slot, B = value.</summary>
    SetVar,

    /// <summary>A = var slot, B = delta.</summary>
    AddVar,

    /// <summary>Jump to D when the predicate holds. A = <see cref="ScriptPredicate"/>, B and C its operands.</summary>
    If,

    /// <summary>Jump to A.</summary>
    Goto,

    /// <summary>Jump to one of A entries of the jump table starting at B, picked evenly.</summary>
    Random,

    /// <summary>End this handler and start the first handler of trigger A.</summary>
    Restart,

    /// <summary>Give movement back to the brain's patrol / wander / idle without ending the handler.</summary>
    ReleaseMovement,

    /// <summary>
    /// Re-posts the NPC: its home for leash and return becomes the point. A, B, D as <see cref="MoveTo"/>; D = 4 is
    /// the NPC's current position ("Here").
    /// </summary>
    SetHome
}

public enum GfxAnchor
{
    /// <summary>Type 1 at the vector in C.</summary>
    AtPosition = 1,

    /// <summary>Type 2 on the NPC.</summary>
    OnSelf = 2,

    /// <summary>Type 3 from the vector in C to the vector in D.</summary>
    Between = 3,

    /// <summary>Type 4 on the NPC at the vector in C.</summary>
    OnSelfAtPosition = 4,

    /// <summary>Type 1 at the NPC's position plus the vector in C.</summary>
    AtOffset = 5
}

public enum ScriptPredicate
{
    VarEquals = 0,
    VarNotEquals = 1,
    VarLessThan = 2,
    VarGreaterThan = 3,
    HealthBelow = 4,
    HasTarget = 5,
    Chance = 6
}

/// <summary>
/// One op. Value refs: a non-negative operand is a constant (or an index into a program table); a negative one
/// is a binding parameter, slot <c>-(v + 1)</c> (<see cref="ParamRef"/>).
/// </summary>
public readonly record struct ScriptOp(ScriptOpCode Code, int A = 0, int B = 0, int C = 0, int D = 0)
{
    public const int None = int.MinValue;

    public static int ParamRef(int slot) => -(slot + 1);

    public static bool IsParam(int value) => value < 0 && value != None;

    public static int ParamSlot(int value) => -(value + 1);
}

public readonly record struct ScriptHandler(
    NpcScriptTrigger Trigger,
    int Filter,
    int Filter2,
    int Start,
    int End,
    bool Once,
    int CooldownMs,
    byte ChancePercent,
    bool Interrupts,
    byte Priority);

public enum ScriptParamType
{
    Vector3Array,
    Vector3,
    ItemHash,
    SocialAction,
    Int,
    Gfx,
    Text
}

public readonly record struct ScriptParam(string Name, ScriptParamType Type);

/// <summary>A compiled program: shared by every NPC bound to it. Immutable after load.</summary>
public sealed class NpcScriptProgram
{
    public NpcScriptProgram(
        string id,
        string file,
        ScriptParam[] parameters,
        ScriptHandler[] handlers,
        ScriptOp[] ops,
        string[] strings,
        Vector3[] vectors,
        int[] jumps)
    {
        Id = id;
        File = file;
        Params = parameters;
        Handlers = handlers;
        Ops = ops;
        Strings = strings;
        Vectors = vectors;
        Jumps = jumps;
        uint mask = 0;
        bool timers = false;
        bool cooldowns = false;
        int vars = 0;
        foreach (ScriptHandler handler in handlers)
        {
            mask |= 1u << (int)handler.Trigger;
            timers |= handler.Trigger == NpcScriptTrigger.Timer;
            cooldowns |= handler.CooldownMs > 0;
        }

        foreach (ScriptOp op in ops)
        {
            if (op.Code is ScriptOpCode.SetVar or ScriptOpCode.AddVar)
                vars = Math.Max(vars, op.A + 1);
            else if (op.Code == ScriptOpCode.If && op.A <= (int)ScriptPredicate.VarGreaterThan)
                vars = Math.Max(vars, op.B + 1);
        }

        TriggerMask = mask;
        HasTimers = timers;
        HasCooldowns = cooldowns;
        VarCount = vars;
    }

    public string Id { get; }

    public string File { get; }

    public ScriptParam[] Params { get; }

    /// <summary>Sorted by trigger, then by descending priority.</summary>
    public ScriptHandler[] Handlers { get; }

    public ScriptOp[] Ops { get; }

    public string[] Strings { get; }

    public Vector3[] Vectors { get; }

    /// <summary>Jump tables for <see cref="ScriptOpCode.Random"/>.</summary>
    public int[] Jumps { get; }

    public uint TriggerMask { get; }

    public bool HasTimers { get; }

    public bool HasCooldowns { get; }

    public int VarCount { get; }

    public const int MaxVars = 8;

    public bool Subscribes(NpcScriptTrigger trigger) => (TriggerMask & (1u << (int)trigger)) != 0;

    public int IndexOfParam(string name)
    {
        for (int i = 0; i < Params.Length; i++)
        {
            if (string.Equals(Params[i].Name, name, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

/// <summary>A binding's compiled arguments, one slot per program parameter.</summary>
public sealed class ScriptArgs
{
    public static readonly ScriptArgs Empty = new(Array.Empty<Vector3[]?>(), Array.Empty<int>(), Array.Empty<string?>());

    public ScriptArgs(Vector3[]?[] vectors, int[] ints, string?[] strings)
    {
        Vectors = vectors;
        Ints = ints;
        Strings = strings;
    }

    /// <summary>Vector3[] and Vector3 (one element) parameters.</summary>
    public Vector3[]?[] Vectors { get; }

    /// <summary>SocialAction, Int and Gfx parameters.</summary>
    public int[] Ints { get; }

    /// <summary>ItemHash and Text parameters.</summary>
    public string?[] Strings { get; }
}

public sealed class ScriptPatrol
{
    public ScriptPatrol(Vector3[] waypoints, bool run)
    {
        Waypoints = waypoints;
        Run = run;
    }

    public Vector3[] Waypoints { get; }

    public bool Run { get; }
}

public sealed class ScriptWander
{
    public ScriptWander(float radius, double minPauseSeconds, double maxPauseSeconds)
    {
        Radius = radius;
        MinPauseSeconds = minPauseSeconds;
        MaxPauseSeconds = maxPauseSeconds;
    }

    public float Radius { get; }

    public double MinPauseSeconds { get; }

    public double MaxPauseSeconds { get; }
}

public enum ScriptBindingTarget
{
    Hash,
    SpawnPoint,
    NpcKey
}

/// <summary>Which NPCs run a program, with what arguments and optional plain movement.</summary>
public sealed class NpcScriptBinding
{
    public NpcScriptBinding(
        NpcScriptProgram? program,
        ScriptArgs args,
        ScriptBindingTarget target,
        string key,
        int? playfieldId,
        ScriptPatrol? patrol,
        ScriptWander? wander,
        string? provenance)
    {
        Program = program;
        Args = args;
        Target = target;
        Key = key;
        PlayfieldId = playfieldId;
        Patrol = patrol;
        Wander = wander;
        Provenance = provenance;
    }

    /// <summary>Null for a movement-only binding.</summary>
    public NpcScriptProgram? Program { get; }

    public ScriptArgs Args { get; }

    public ScriptBindingTarget Target { get; }

    /// <summary>The hash, spawn point key or NPC key.</summary>
    public string Key { get; }

    public int? PlayfieldId { get; }

    public ScriptPatrol? Patrol { get; }

    public ScriptWander? Wander { get; }

    public string? Provenance { get; }

    public string Describe()
        => (Program?.Id ?? "movement") + " -> " + Target + " " + Key + (PlayfieldId is int pf ? " pf " + pf : string.Empty);
}

/// <summary>One catalog build: counts and one line per problem, the Knubot shape.</summary>
public sealed record NpcScriptLoadResult(int Programs, int Bindings, IReadOnlyList<string> Problems);
