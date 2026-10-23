namespace ZoneEngine_New.Core.NpcScripts;

using System;

using AORebirth.Enums;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Chat;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Playfield;

using MsgVector3 = SmokeLounge.AOtomation.Messaging.GameData.Vector3;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// One NPC's execution of a <see cref="NpcScriptProgram"/>. Advanced from <see cref="NpcCharacter.Tick"/>, so it
/// only runs while the NPC's cell ticks; a waiting runner costs one compare per tick. Triggers arrive through
/// <see cref="Raise"/> from the engine's existing hooks and start their handler on the next tick, never inside the
/// hook. Everything runs on the playfield thread.
/// </summary>
public sealed class NpcScriptRunner
{
    /// <summary>Instant ops one tick may run before yielding, so a bad loop in data costs a bounded slice.</summary>
    public const int OpBudget = 32;

    const int ArrivePollMs = 250;
    const int BusyPollMs = 500;
    const int SayMinIntervalMs = 1000;

    readonly NpcScriptProgram _program;
    readonly ScriptArgs _args;
    readonly NpcCharacter _npc;

    int _handler = -1;
    int _pc;
    long _wakeAtMs = long.MaxValue;
    WaitKind _waiting;
    int _waitWaypoint = -1;
    int _waypointCursor;
    bool _moveRun;
    bool _moveDirect;
    Vector3? _moveDestination;

    // One level of suspension: an interrupting handler runs, then the suspended one resumes where it was.
    int _suspendedHandler = -1;
    int _suspendedPc;
    WaitKind _suspendedWaiting;
    int _suspendedWaypoint;

    ulong _onceFired;
    ulong _healthFired;
    ulong _rng;
    long _lastSayMs;
    readonly long[]? _cooldownUntilMs;
    readonly long[]? _timerDueMs;
    readonly int[]? _vars;
    bool _budgetWarned;
    bool _detached;

    enum WaitKind : byte
    {
        None,
        Delay,
        Arrive
    }

    public NpcScriptRunner(NpcScriptProgram program, ScriptArgs args, NpcCharacter npc)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(npc);
        _program = program;
        _args = args;
        _npc = npc;
        _rng = unchecked(0x9E3779B97F4A7C15UL ^ ((ulong)(uint)npc.Identity.Instance << 17) ^ (ulong)Environment.TickCount64);
        if (_rng == 0)
            _rng = 1;
        if (program.HasCooldowns)
            _cooldownUntilMs = new long[program.Handlers.Length];
        if (program.VarCount > 0)
            _vars = new int[program.VarCount];
        if (program.HasTimers)
        {
            _timerDueMs = new long[program.Handlers.Length];
            long now = Now();
            for (int i = 0; i < program.Handlers.Length; i++)
            {
                ScriptHandler handler = program.Handlers[i];
                if (handler.Trigger == NpcScriptTrigger.Timer)
                    _timerDueMs[i] = now + NextInt(handler.Filter, handler.Filter2);
            }

            ArmIdleWake(now);
        }
    }

    public NpcScriptProgram Program => _program;

    // Diagnostics (debug MCP).
    public string? CurrentTrigger => _handler >= 0 ? _program.Handlers[_handler].Trigger.ToString() : null;

    public int ProgramCounter => _pc;

    public string WaitState => _waiting.ToString();

    public long WakeAtMs => _wakeAtMs;

    static long Now() => Environment.TickCount64;

    // ----------------------------------------------------------------------------------------------------------------
    // Triggers
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A trigger from a hook. One mask test when the program has no handler for it. The chosen handler starts on
    /// the NPC's next tick.
    /// </summary>
    public void Raise(NpcScriptTrigger trigger, int arg = 0)
    {
        if (_detached || !_program.Subscribes(trigger))
            return;

        long now = Now();
        if (trigger == NpcScriptTrigger.Reset)
            _healthFired = 0;

        ScriptHandler[] handlers = _program.Handlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            ScriptHandler handler = handlers[i];
            if (handler.Trigger != trigger)
                continue;
            if (!FilterMatches(handler, i, trigger, arg))
                continue;
            if (handler.Once && (_onceFired & (1UL << (i & 63))) != 0)
                continue;
            if (_cooldownUntilMs != null && _cooldownUntilMs[i] > now)
                continue;
            if (handler.ChancePercent < 100 && NextInt(0, 99) >= handler.ChancePercent)
                continue;

            if (_handler >= 0)
            {
                if (!handler.Interrupts)
                    return;
                Suspend();
            }

            Start(i, now);
            return;
        }
    }

    bool FilterMatches(ScriptHandler handler, int index, NpcScriptTrigger trigger, int arg)
    {
        switch (trigger)
        {
            case NpcScriptTrigger.HealthBelow:
                // arg is the health percent after the hit; each threshold fires once per life (reset on Reset).
                if (arg > handler.Filter || (_healthFired & (1UL << (index & 63))) != 0)
                    return false;
                _healthFired |= 1UL << (index & 63);
                return true;
            case NpcScriptTrigger.Arrived:
            case NpcScriptTrigger.Stuck:
                return handler.Filter < 0 || handler.Filter == arg;
            case NpcScriptTrigger.NanoEnded:
                return handler.Filter == 0 || handler.Filter == arg;
            default:
                return true;
        }
    }

    void Start(int handlerIndex, long now)
    {
        ScriptHandler handler = _program.Handlers[handlerIndex];
        if (handler.Once)
            _onceFired |= 1UL << (handlerIndex & 63);
        if (_cooldownUntilMs != null && handler.CooldownMs > 0)
            _cooldownUntilMs[handlerIndex] = now + handler.CooldownMs;
        _handler = handlerIndex;
        _pc = handler.Start;
        _waiting = WaitKind.None;
        _waitWaypoint = -1;
        _wakeAtMs = now;
    }

    void Suspend()
    {
        _suspendedHandler = _handler;
        _suspendedPc = _pc;
        _suspendedWaiting = _waiting;
        _suspendedWaypoint = _waitWaypoint;
        if (_waiting == WaitKind.Arrive)
            _npc.Brain?.ScriptReleaseMovement();
        _handler = -1;
        _waiting = WaitKind.None;
    }

    void EndHandler(long now)
    {
        _handler = -1;
        _waiting = WaitKind.None;
        _waitWaypoint = -1;
        if (_suspendedHandler >= 0)
        {
            _handler = _suspendedHandler;
            _pc = _suspendedPc;
            _waiting = _suspendedWaiting;
            _waitWaypoint = _suspendedWaypoint;
            _suspendedHandler = -1;
            // A walk that was cut short is re-issued from the same op.
            if (_waiting == WaitKind.Arrive)
            {
                _waiting = WaitKind.None;
                _pc--;
            }

            _wakeAtMs = now;
            return;
        }

        ArmIdleWake(now);
    }

    void ArmIdleWake(long now)
    {
        _wakeAtMs = long.MaxValue;
        if (_timerDueMs == null)
            return;
        for (int i = 0; i < _timerDueMs.Length; i++)
        {
            if (_program.Handlers[i].Trigger == NpcScriptTrigger.Timer && _timerDueMs[i] < _wakeAtMs)
                _wakeAtMs = _timerDueMs[i];
        }

        if (_wakeAtMs < now)
            _wakeAtMs = now;
    }

    void TryStartDueTimer(long now)
    {
        if (_timerDueMs == null)
            return;
        for (int i = 0; i < _timerDueMs.Length; i++)
        {
            ScriptHandler handler = _program.Handlers[i];
            if (handler.Trigger != NpcScriptTrigger.Timer || _timerDueMs[i] > now)
                continue;
            _timerDueMs[i] = now + NextInt(handler.Filter, handler.Filter2);
            if (_handler >= 0)
                continue;
            if (handler.ChancePercent < 100 && NextInt(0, 99) >= handler.ChancePercent)
                continue;
            Start(i, now);
        }
    }

    /// <summary>The NPC left the world: drop the movement lease and go quiet.</summary>
    public void OnDespawn()
    {
        _detached = true;
        _handler = -1;
        _suspendedHandler = -1;
        _waiting = WaitKind.None;
        _wakeAtMs = long.MaxValue;
        _npc.Brain?.ScriptReleaseMovement();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Tick
    // ----------------------------------------------------------------------------------------------------------------

    public void Tick(long nowMs)
    {
        if (nowMs < _wakeAtMs || _detached)
            return;

        if (_handler < 0)
        {
            TryStartDueTimer(nowMs);
            if (_handler < 0)
            {
                ArmIdleWake(nowMs);
                return;
            }
        }
        else if (_timerDueMs != null)
        {
            // Timers keep their cadence while another handler runs; they do not stack up.
            TryStartDueTimer(nowMs);
        }

        if (_waiting == WaitKind.Arrive && !PollArrival(nowMs))
            return;

        _waiting = WaitKind.None;
        Execute(nowMs);
    }

    /// <summary>True when the walk is over (arrived, stuck, or the NPC cannot walk) and ops may continue.</summary>
    bool PollArrival(long now)
    {
        NpcBrain? brain = _npc.Brain;
        if (brain == null || _npc.IsDead)
            return true;

        // The walk is over before the trigger is raised: an interrupting Arrived handler then suspends a handler
        // that is past its MoveTo, so resuming does not walk to the same waypoint again.
        switch (brain.ConsumeScriptMoveResult())
        {
            case ScriptMoveResult.Arrived:
                _waiting = WaitKind.None;
                _moveDestination = null;
                _wakeAtMs = now;
                Raise(NpcScriptTrigger.Arrived, _waitWaypoint);
                return true;
            case ScriptMoveResult.Stuck:
                _waiting = WaitKind.None;
                _moveDestination = null;
                _wakeAtMs = now;
                Raise(NpcScriptTrigger.Stuck, _waitWaypoint);
                return true;
        }

        if (brain.ScriptHoldsMovement)
        {
            _wakeAtMs = now + ArrivePollMs;
            return false;
        }

        // The brain dropped the lease for a fight or a leash. Wait it out, then ask for the walk again.
        if (brain.IsBusy)
        {
            _wakeAtMs = now + BusyPollMs;
            return false;
        }

        if (_moveDestination is Vector3 destination)
        {
            brain.ScriptMoveTo(destination, _moveRun, _moveDirect);
            _wakeAtMs = now + ArrivePollMs;
            return false;
        }

        return true;
    }

    void Execute(long now)
    {
        ScriptOp[] ops = _program.Ops;
        int budget = OpBudget;
        while (_handler >= 0)
        {
            if (budget-- <= 0)
            {
                if (!_budgetWarned)
                {
                    _budgetWarned = true;
                    _npc.Playfield?.GetService<Logging.IZoneLogger>()?.Warn(
                        "NpcScripts: program '" + _program.Id + "' on " + _npc.Name + " hit the op budget; yielding");
                }

                _wakeAtMs = now + ArrivePollMs;
                return;
            }

            ScriptHandler handler = _program.Handlers[_handler];
            if (_pc < handler.Start || _pc >= handler.End)
            {
                EndHandler(now);
                return;
            }

            ScriptOp op = ops[_pc++];
            switch (op.Code)
            {
                case ScriptOpCode.End:
                    EndHandler(now);
                    return;

                case ScriptOpCode.Wait:
                    _waiting = WaitKind.Delay;
                    _wakeAtMs = now + NextInt(op.A, op.B);
                    return;

                case ScriptOpCode.MoveTo:
                    if (BeginMove(op, now))
                        return;
                    break;

                case ScriptOpCode.Emote:
                    DoEmote(op);
                    if (op.B > 0)
                    {
                        _waiting = WaitKind.Delay;
                        _wakeAtMs = now + op.B;
                        return;
                    }

                    break;

                case ScriptOpCode.Say:
                    DoSay(op, now);
                    break;

                case ScriptOpCode.SpawnWorldItem:
                    DoSpawnWorldItem(op);
                    break;

                case ScriptOpCode.Gfx:
                    DoGfx(op);
                    break;

                case ScriptOpCode.Despawn:
                    _handler = -1;
                    _suspendedHandler = -1;
                    _wakeAtMs = long.MaxValue;
                    _npc.Brain?.ScriptReleaseMovement();
                    _npc.DespawnAsDeath();
                    return;

                case ScriptOpCode.SetStat:
                    _npc.Stats.Set((CharacterStat)op.A, op.B, StatDetail.Base, dirty: true);
                    break;

                case ScriptOpCode.SetVar:
                    if (_vars != null && op.A < _vars.Length)
                        _vars[op.A] = op.B;
                    break;

                case ScriptOpCode.AddVar:
                    if (_vars != null && op.A < _vars.Length)
                        _vars[op.A] += op.B;
                    break;

                case ScriptOpCode.If:
                    if (Evaluate(op))
                        _pc = op.D;
                    break;

                case ScriptOpCode.Goto:
                    _pc = op.A;
                    break;

                case ScriptOpCode.Random:
                    _pc = _program.Jumps[op.B + NextInt(0, op.A - 1)];
                    break;

                case ScriptOpCode.Restart:
                {
                    _suspendedHandler = -1;
                    _npc.Brain?.ScriptReleaseMovement();
                    _handler = -1;
                    int target = FirstHandler((NpcScriptTrigger)op.A);
                    if (target < 0)
                    {
                        ArmIdleWake(now);
                        return;
                    }

                    Start(target, now);
                    break;
                }

                case ScriptOpCode.ReleaseMovement:
                    _moveDestination = null;
                    _npc.Brain?.ScriptReleaseMovement();
                    break;

                case ScriptOpCode.SetHome:
                    if (_npc.Brain is { } homeBrain && TryResolvePoint(op, out Vector3 newHome, out _))
                        homeBrain.SetHome(newHome);
                    break;
            }
        }
    }

    int FirstHandler(NpcScriptTrigger trigger)
    {
        ScriptHandler[] handlers = _program.Handlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i].Trigger == trigger)
                return i;
        }

        return -1;
    }

    bool Evaluate(ScriptOp op)
    {
        switch ((ScriptPredicate)op.A)
        {
            case ScriptPredicate.VarEquals: return Var(op.B) == op.C;
            case ScriptPredicate.VarNotEquals: return Var(op.B) != op.C;
            case ScriptPredicate.VarLessThan: return Var(op.B) < op.C;
            case ScriptPredicate.VarGreaterThan: return Var(op.B) > op.C;
            case ScriptPredicate.HealthBelow: return HealthPercent() < op.B;
            case ScriptPredicate.HasTarget: return (_npc.FightingTarget.Instance != 0) == (op.B != 0);
            case ScriptPredicate.Chance: return NextInt(0, 99) < op.B;
            default: return false;
        }
    }

    int Var(int slot) => _vars != null && slot < _vars.Length ? _vars[slot] : 0;

    int HealthPercent()
    {
        int max = _npc.Stats.GetOrZero(CharacterStat.MaxHealth);
        if (max <= 0)
            return 100;
        return (int)Math.Clamp(_npc.Stats.GetOrZero(CharacterStat.Health) * 100L / max, 0, 100);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Verbs
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>Starts a walk and returns true when the op blocks; false when the NPC cannot walk and ops go on.</summary>
    bool BeginMove(ScriptOp op, long now)
    {
        NpcBrain? brain = _npc.Brain;
        if (brain == null || _npc.IsDead || _npc.IsImmobile)
            return false;

        if (!TryResolvePoint(op, out Vector3 target, out int waypoint))
            return false;

        _moveDestination = target;
        _moveRun = (op.C & 1) != 0;
        _moveDirect = (op.C & 2) != 0;
        _waitWaypoint = waypoint;
        _waiting = WaitKind.Arrive;
        _wakeAtMs = now + ArrivePollMs;
        if (!brain.IsBusy)
            brain.ScriptMoveTo(target, _moveRun, _moveDirect);
        return true;
    }

    /// <summary>The point a MoveTo / SetHome op names through its D kind; false when it names nothing usable.</summary>
    bool TryResolvePoint(ScriptOp op, out Vector3 point, out int waypoint)
    {
        point = default!;
        waypoint = -1;
        Vector3? destination;
        switch (op.D)
        {
            case 4:
                destination = _npc.Position;
                break;
            case 3:
                destination = _npc.Brain?.Home;
                break;
            case 2:
            {
                Vector3[]? route = ParamVectors(op.A);
                if (route == null || route.Length == 0)
                    return false;
                waypoint = _waypointCursor % route.Length;
                _waypointCursor = (waypoint + 1) % route.Length;
                destination = route[waypoint];
                break;
            }

            case 1:
            {
                Vector3[]? route = ParamVectors(op.A);
                if (route == null || op.B >= route.Length)
                    return false;
                waypoint = op.B;
                destination = route[op.B];
                break;
            }

            default:
                destination = op.A < _program.Vectors.Length ? _program.Vectors[op.A] : null;
                break;
        }

        if (destination is not Vector3 target)
            return false;

        point = target;
        return true;
    }

    Vector3[]? ParamVectors(int reference)
    {
        if (!ScriptOp.IsParam(reference))
            return null;
        int slot = ScriptOp.ParamSlot(reference);
        return slot < _args.Vectors.Length ? _args.Vectors[slot] : null;
    }

    int IntValue(int reference)
    {
        if (!ScriptOp.IsParam(reference))
            return reference;
        int slot = ScriptOp.ParamSlot(reference);
        return slot < _args.Ints.Length ? _args.Ints[slot] : 0;
    }

    string? StringValue(int reference)
    {
        if (ScriptOp.IsParam(reference))
        {
            int slot = ScriptOp.ParamSlot(reference);
            return slot < _args.Strings.Length ? _args.Strings[slot] : null;
        }

        return reference >= 0 && reference < _program.Strings.Length ? _program.Strings[reference] : null;
    }

    Vector3? VectorValue(int reference)
    {
        if (reference == ScriptOp.None)
            return null;
        if (ScriptOp.IsParam(reference))
        {
            Vector3[]? vectors = ParamVectors(reference);
            return vectors is { Length: > 0 } ? vectors[0] : null;
        }

        return reference < _program.Vectors.Length ? _program.Vectors[reference] : null;
    }

    void DoEmote(ScriptOp op)
    {
        int anim = IntValue(op.A);
        if (anim < NpcScriptCompiler.MinSocialAnim || anim > NpcScriptCompiler.MaxSocialAnim || _npc.IsDead)
            return;

        // The same command a player's emote is relayed with: the client acts on CommandState 1 and treats any ref
        // that is not its own as foreign, so every receiver plays it.
        _npc.Cell?.Announce(new SocialActionCmdMessage
        {
            Identity = _npc.Identity,
            CommandState = 1,
            CommandRef = 0,
            Action = (SocialAction)anim
        });
    }

    void DoSay(ScriptOp op, long now)
    {
        if (now - _lastSayMs < SayMinIntervalMs)
            return;
        string? text = StringValue(op.A);
        if (string.IsNullOrEmpty(text))
            return;
        _lastSayMs = now;
        _npc.Playfield?.GetService<VicinityChatRelay>()?.Speak(_npc, text, (ChatMessageType)op.B);
    }

    void DoSpawnWorldItem(ScriptOp op)
    {
        Playfield? playfield = _npc.Playfield;
        if (playfield == null || _npc.IsDead)
            return;
        string? hash = StringValue(op.A);
        if (string.IsNullOrEmpty(hash))
            return;
        HashItemMinter? minter = playfield.GetService<HashItemMinter>();
        SpawnService? spawns = playfield.GetService<SpawnService>();
        if (minter == null || spawns == null || !minter.TryMint(hash, Math.Max(1, op.C), ItemSource.Other, out Item item))
            return;

        // The offset is in the NPC's own frame: x to its right, y up, z ahead, so [1, 0, 0] lands at its right side
        // whichever way it faces.
        Vector3 at = _npc.Position;
        if (VectorValue(op.B) is Vector3 offset)
        {
            var turned = (Vector3)_npc.Rotation.RotateVector3(offset);
            at = new Vector3(at.x + turned.x, at.y + turned.y, at.z + turned.z);
        }

        spawns.SpawnDroppedItem(item, at, _npc.Rotation, Identity.None, TimeSpan.FromSeconds(op.D));
    }

    void DoGfx(ScriptOp op)
    {
        int gfx = IntValue(op.A);
        if (gfx <= 0 || gfx == GfxTriggerMessage.NoGfx || _npc.Cell == null)
            return;

        var message = new GfxTriggerMessage { Identity = _npc.Identity, GfxId = gfx };
        Vector3 position = _npc.Position;
        switch ((GfxAnchor)op.B)
        {
            case GfxAnchor.AtPosition:
                if (VectorValue(op.C) is not Vector3 at)
                    return;
                message.Type = GfxTriggerMessage.TypeAtPosition;
                message.Position = ToMsg(at);
                break;
            case GfxAnchor.AtOffset:
                if (VectorValue(op.C) is not Vector3 offset)
                    return;
                message.Type = GfxTriggerMessage.TypeAtPosition;
                message.Position = new MsgVector3((float)(position.x + offset.x), (float)(position.y + offset.y), (float)(position.z + offset.z));
                break;
            case GfxAnchor.Between:
                if (VectorValue(op.C) is not Vector3 from || VectorValue(op.D) is not Vector3 to)
                    return;
                message.Type = GfxTriggerMessage.TypeBetweenPositions;
                message.Position = ToMsg(from);
                message.Position2 = ToMsg(to);
                break;
            case GfxAnchor.OnSelfAtPosition:
                if (VectorValue(op.C) is not Vector3 bound)
                    return;
                message.Type = GfxTriggerMessage.TypeOnCharacterAtPosition;
                message.Target = _npc.Identity;
                message.Position = ToMsg(bound);
                break;
            default:
                message.Type = GfxTriggerMessage.TypeOnCharacter;
                message.Target = _npc.Identity;
                message.Extra = 0;
                break;
        }

        _npc.Cell.Announce(message);
    }

    static MsgVector3 ToMsg(Vector3 v) => new((float)v.x, (float)v.y, (float)v.z);

    // ----------------------------------------------------------------------------------------------------------------
    // Random
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>xorshift64*: cheap, allocation-free, per NPC.</summary>
    ulong NextRaw()
    {
        ulong x = _rng;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _rng = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [min, max].</summary>
    int NextInt(int min, int max)
    {
        if (max <= min)
            return min;
        ulong range = (ulong)((long)max - min + 1);
        return min + (int)(NextRaw() % range);
    }
}
