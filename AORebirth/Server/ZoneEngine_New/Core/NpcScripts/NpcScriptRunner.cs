namespace ZoneEngine_New.Core.NpcScripts;

using System;
using System.Collections.Generic;

using AORebirth.Enums;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Chat;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using MsgVector3 = SmokeLounge.AOtomation.Messaging.GameData.Vector3;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// One NPC's execution of a <see cref="NpcScriptProgram"/>. Advanced from <see cref="NpcCharacter.Tick"/>, so it
/// only runs while the NPC's cell ticks; a waiting runner costs one compare per tick. Triggers arrive through
/// <see cref="Raise"/> from the engine's existing hooks and start their handler on the next tick, never inside the
/// hook. Everything runs on the playfield thread. An NPC may run more than one program: the first runner is its own
/// binding's, the <see cref="Next"/> ones are feature overlays (<see cref="ScriptBindingTarget.Feature"/>); every
/// entry point forwards along that chain.
/// </summary>
public sealed class NpcScriptRunner
{
    /// <summary>Instant ops one tick may run before yielding, so a bad loop in data costs a bounded slice.</summary>
    public const int OpBudget = 32;

    /// <summary>Live NPCs one runner may own at once (<see cref="ScriptOpCode.SpawnNpc"/>).</summary>
    public const int MaxOwnedNpcs = 16;

    /// <summary>
    /// SpawnNpc generations: a spawn point's NPC may spawn adds (depth 1) that spawn adds (depth 2) that spawn adds
    /// (depth 3), no further. Independent spawns count too, so a program that spawns itself still stops.
    /// </summary>
    public const int MaxSpawnDepth = 3;

    /// <summary>AddDied events one runner holds waiting for their handler; more are dropped with a warning.</summary>
    public const int MaxQueuedAddDied = 32;

    const int ArrivePollMs = 250;
    const int BusyPollMs = 500;
    const int CastPollMs = 250;
    const int SayMinIntervalMs = 1000;

    /// <summary>Where a warped recipient lands: this far from the NPC's centre, on the side it came from.</summary>
    const double FeetOffsetMeters = 1.5;

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
    bool _spawnWarned;
    bool _detached;

    // SpawnNpc: the live NPCs this runner owns, for the cap and for despawning them with the owner.
    List<NpcCharacter>? _owned;

    // AddDied: add kills waiting for their handler, oldest first. Queued, not dropped, so a slot that loses its add
    // while another handler runs still respawns. The epoch moves on at the owner's death, reset or despawn, which
    // cuts every earlier add loose; the second wake lets a queued entry come due during another handler's wait.
    List<PendingAddDied>? _addDied;
    long _addDiedWakeMs = long.MaxValue;
    int _ownerEpoch;
    bool _addDiedWarned;

    // CastNano RandomNearby: the locality query's buffer, reused across casts.
    List<NpcCharacter>? _nearby;

    // CastNano with Warp: the cast whose landing moves its recipient to the NPC's feet.
    int _pendingWarpNano;
    Identity _pendingWarpTarget;

    // Target Playfield: the op's resolved nano ids, reused across casts.
    List<int>? _castNanos;

    enum WaitKind : byte
    {
        None,
        Delay,
        Arrive,
        Cast
    }

    readonly record struct PendingAddDied(long DueMs, int Handler);

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

    /// <summary>The next program this NPC runs (a feature overlay), or null.</summary>
    public NpcScriptRunner? Next { get; private set; }

    /// <summary>Links <paramref name="runner"/> after the last runner of this chain.</summary>
    public void Append(NpcScriptRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        NpcScriptRunner last = this;
        while (last.Next != null)
            last = last.Next;
        last.Next = runner;
    }

    /// <summary>True when any program in the chain has a handler for <paramref name="trigger"/>.</summary>
    public bool Subscribes(NpcScriptTrigger trigger)
    {
        for (NpcScriptRunner? runner = this; runner != null; runner = runner.Next)
        {
            if (runner._program.Subscribes(trigger))
                return true;
        }

        return false;
    }

    // Diagnostics (debug MCP).
    public string? CurrentTrigger => _handler >= 0 ? _program.Handlers[_handler].Trigger.ToString() : null;

    bool InDeathHandler => _handler >= 0 && _program.Handlers[_handler].Trigger == NpcScriptTrigger.Death;

    /// <summary>Program ids along the chain, comma separated.</summary>
    public string ProgramIds
    {
        get
        {
            string ids = _program.Id;
            for (NpcScriptRunner? overlay = Next; overlay != null; overlay = overlay.Next)
                ids += ", " + overlay._program.Id;
            return ids;
        }
    }

    public int ProgramCounter => _pc;

    public string WaitState => _waiting.ToString();

    public long WakeAtMs => _wakeAtMs;

    static long Now() => Environment.TickCount64;

    // ----------------------------------------------------------------------------------------------------------------
    // Triggers
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A trigger from a hook, to every program in the chain. One mask test when a program has no handler for it. The
    /// chosen handler starts on the NPC's next tick.
    /// </summary>
    public void Raise(NpcScriptTrigger trigger, int arg = 0)
    {
        for (NpcScriptRunner? runner = this; runner != null; runner = runner.Next)
            runner.RaiseOwn(trigger, arg);
    }

    void RaiseOwn(NpcScriptTrigger trigger, int arg)
    {
        if (_detached)
            return;

        // The owner is gone for its adds whether or not a handler listens.
        if (trigger is NpcScriptTrigger.Death or NpcScriptTrigger.Reset)
        {
            ReleaseOwned(Now());
            CancelAddDied(Now());
        }

        if (!_program.Subscribes(trigger))
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

    /// <param name="mark">Latch Once and start the cooldown; false when that already happened at queue time.</param>
    void Start(int handlerIndex, long now, bool mark = true)
    {
        if (mark)
            MarkFired(handlerIndex, now);
        _handler = handlerIndex;
        _pc = _program.Handlers[handlerIndex].Start;
        _waiting = WaitKind.None;
        _waitWaypoint = -1;
        _wakeAtMs = now;
    }

    void MarkFired(int handlerIndex, long now)
    {
        ScriptHandler handler = _program.Handlers[handlerIndex];
        if (handler.Once)
            _onceFired |= 1UL << (handlerIndex & 63);
        if (_cooldownUntilMs != null && handler.CooldownMs > 0)
            _cooldownUntilMs[handlerIndex] = now + handler.CooldownMs;
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
            ArmAddDiedWake(now);
            return;
        }

        ArmIdleWake(now);
    }

    void ArmIdleWake(long now)
    {
        _wakeAtMs = long.MaxValue;
        ArmAddDiedWake(now);
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

    /// <summary>The NPC left the world: drop the movement lease, let the adds go and go quiet, along the chain.</summary>
    public void OnDespawn()
    {
        for (NpcScriptRunner? runner = this; runner != null; runner = runner.Next)
            runner.OnDespawnOwn();
    }

    void OnDespawnOwn()
    {
        if (_detached)
            return;

        _detached = true;
        ReleaseOwned(Now());
        _ownerEpoch++;
        _addDied?.Clear();
        _addDiedWakeMs = long.MaxValue;
        ClearPendingWarp();
        _handler = -1;
        _suspendedHandler = -1;
        _waiting = WaitKind.None;
        _wakeAtMs = long.MaxValue;
        _npc.Brain?.ScriptReleaseMovement();
    }

    /// <summary>
    /// A nano the NPC cast has landed on <paramref name="recipient"/> (<see cref="NanoRuntime"/>). A CastNano that
    /// asked for a warp moves the recipient to the NPC's feet now, after the buff is on it.
    /// </summary>
    public void OnCastLanded(int nanoId, Character recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        for (NpcScriptRunner? runner = this; runner != null; runner = runner.Next)
        {
            if (runner._detached || runner._pendingWarpNano != nanoId || !runner._pendingWarpTarget.Equals(recipient.Identity))
                continue;
            runner.ClearPendingWarp();
            runner.WarpToFeet(recipient);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Tick
    // ----------------------------------------------------------------------------------------------------------------

    public void Tick(long nowMs)
    {
        for (NpcScriptRunner? runner = this; runner != null; runner = runner.Next)
            runner.TickOwn(nowMs);
    }

    void TickOwn(long nowMs)
    {
        if ((nowMs < _wakeAtMs && nowMs < _addDiedWakeMs) || _detached)
            return;

        if (nowMs >= _addDiedWakeMs)
        {
            TryStartDueAddDied(nowMs);
            // Woken only for the queue, and nothing started: the running handler's own wait goes on.
            if (nowMs < _wakeAtMs)
                return;
        }

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

        if (_waiting == WaitKind.Cast)
        {
            if (_npc.IsCastingNano && !_npc.IsDead)
            {
                _wakeAtMs = nowMs + CastPollMs;
                return;
            }

            // The cast is over; a landing already warped, anything else (resisted, interrupted) warps nobody.
            ClearPendingWarp();
        }

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

                case ScriptOpCode.GfxEffect:
                    DoGfxEffect(IntValue(op.A));
                    break;

                case ScriptOpCode.SpawnNpc:
                    DoSpawnNpc(op, now);
                    break;

                case ScriptOpCode.CastNano:
                    if (BeginCast(op, now))
                        return;
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
                    if (op.C != 0)
                        ShowSetStat((CharacterStat)op.A, op.B);
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
                    _pc = _program.Jumps[op.B + PickBlock(op)];
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
        // A Death handler runs on the corpse-to-be, so it may still drop an item where the NPC fell; any other
        // handler stops littering once the NPC is dead.
        Playfield? playfield = _npc.Playfield;
        if (playfield == null || (_npc.IsDead && !InDeathHandler))
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

    /// <summary>The Set function type (0xCF22): the effect identity type of a SetStat Show.</summary>
    const int SetFunctionType = 53026;

    /// <summary>The GfxEffect function type (0xCF26): the effect identity type of a GfxEffect op.</summary>
    const int GfxEffectFunctionType = 53030;

    /// <summary>Effect instances for SetStat Show and GfxEffect, unique per process as retail's are.</summary>
    static int _setEffectInstance;

    /// <summary>GfxEffect: plays <paramref name="gfx"/> on the NPC for its observers as a SpellList GfxEffect function effect.</summary>
    void DoGfxEffect(int gfx)
    {
        if (gfx <= 0 || _npc.Cell == null)
            return;

        _npc.Cell.Announce(new SpellListMessage
        {
            Identity = _npc.Identity,
            NanoEffects = Array.Empty<NanoEffect>(),
            Character = _npc.Identity,
            GfxEffect = new SpellListGfxEffect
            {
                Effect = new Identity { Type = (IdentityType)GfxEffectFunctionType, Instance = System.Threading.Interlocked.Increment(ref _setEffectInstance) },
                Gfx = gfx
            }
        });
    }

    /// <summary>
    /// SetStat Show: tells the NPC's observers the stat changed the way retail does for a Fresh Recruit hatching (a
    /// SpellList Set function effect on it), so the client applies it at once: Scale grows the model. The stat itself is
    /// already set, so later spawn packets carry the new value.
    /// </summary>
    void ShowSetStat(CharacterStat stat, int value)
    {
        if (_npc.Cell == null)
            return;

        _npc.Cell.Announce(new SpellListMessage
        {
            Identity = _npc.Identity,
            NanoEffects = Array.Empty<NanoEffect>(),
            Character = _npc.Identity,
            SetStat = new SpellListSetStat
            {
                Effect = new Identity { Type = (IdentityType)SetFunctionType, Instance = System.Threading.Interlocked.Increment(ref _setEffectInstance) },
                Stat = (int)stat,
                Value = value
            }
        });
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
    // SpawnNpc
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spawns the op's NPCs around the centre (the NPC's position, an offset in its frame, or a world position): one,
    /// or Count of them evenly on a circle of Radius. Each is owned by this runner: capped at
    /// <see cref="MaxOwnedNpcs"/> live, given the spec's lifetime, and despawned after the owner's death, reset or
    /// despawn unless the spec leaves it independent. With JoinFight the add starts on the owner's current target.
    /// From a Death handler the owner is already dead: an independent spawn is unaffected, a linked one starts its
    /// owner-despawn delay at once (0 = gone next tick), and none reports AddDied to the dead owner.
    /// </summary>
    void DoSpawnNpc(ScriptOp op, long now)
    {
        Playfield? playfield = _npc.Playfield;
        bool ownerDead = _npc.IsDead;
        if (playfield == null || (ownerDead && !InDeathHandler))
            return;

        if (_npc.ScriptSpawnDepth >= MaxSpawnDepth)
        {
            WarnSpawnOnce("is " + MaxSpawnDepth + " SpawnNpc generations deep; its SpawnNpc is ignored");
            return;
        }

        string? hash = StringValue(op.A);
        if (string.IsNullOrEmpty(hash) || op.C < 0 || op.C >= _program.Spawns.Length)
            return;

        SpawnNpcSpec spec = _program.Spawns[op.C];
        PruneOwned();
        int wanted = Math.Min(spec.Count, MaxOwnedNpcs - (_owned?.Count ?? 0));
        if (wanted <= 0)
        {
            WarnSpawnOnce("owns " + MaxOwnedNpcs + " live NPCs; its SpawnNpc is ignored until some die");
            return;
        }

        SpawnService? spawns = playfield.GetService<SpawnService>();
        int? level = spec.Level > 0 ? spec.Level : null;
        if (spec.LevelPercent > 0)
            level = Math.Max(1, (int)Math.Round(_npc.Stats.GetOrOne(CharacterStat.Level) * spec.LevelPercent / 100.0, MidpointRounding.AwayFromZero));
        if (spawns == null
            || !playfield.GetRequiredService<IGameData>().TryResolveMobTemplate(hash, level, out MobTemplate template)
            || !NpcTemplateValidation.CanSpawn(template))
            return;

        Vector3 centre = _npc.Position;
        if (VectorValue(op.B) is Vector3 vector)
        {
            if (spec.Absolute)
            {
                centre = vector;
            }
            else
            {
                var turned = (Vector3)_npc.Rotation.RotateVector3(vector);
                centre = new Vector3(centre.x + turned.x, centre.y + turned.y, centre.z + turned.z);
            }
        }

        Character? enemy = spec.JoinFight ? _npc.Brain?.ResolveCurrentTarget() : null;
        // A random start angle, so two circles from the same op do not stack their adds on the same spots.
        double phase = NextInt(0, 359) * Math.PI / 180.0;
        for (int i = 0; i < wanted; i++)
        {
            Vector3 at = centre;
            if (spec.Count > 1)
            {
                double angle = phase + (Math.PI * 2.0 * i / spec.Count);
                at = new Vector3(centre.x + (spec.Radius * Math.Cos(angle)), centre.y, centre.z + (spec.Radius * Math.Sin(angle)));
            }

            NpcCharacter add;
            try
            {
                add = spawns.SpawnMob(template, at, _npc.Rotation, level, SpawnSource.Script, spawnHash: hash);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                WarnSpawnOnce("cannot spawn '" + hash + "': " + exception.Message);
                return;
            }

            add.ScriptOwner = _npc;
            add.ScriptSpawnDepth = _npc.ScriptSpawnDepth + 1;
            if (spec.LifetimeSeconds > 0)
                add.DespawnAtMs = now + (spec.LifetimeSeconds * 1000L);
            if (spec.OwnerDespawnDelaySeconds >= 0)
            {
                add.OwnerDespawnDelayMs = spec.OwnerDespawnDelaySeconds * 1000;
                (_owned ??= new List<NpcCharacter>()).Add(add);

                // The owner's death already released its adds; this one's delay runs from now.
                if (ownerDead)
                {
                    long releaseAt = now + add.OwnerDespawnDelayMs;
                    if (add.DespawnAtMs == 0 || releaseAt < add.DespawnAtMs)
                        add.DespawnAtMs = releaseAt;
                }
            }

            if (!ownerDead && _program.Subscribes(NpcScriptTrigger.AddDied))
                add.ScriptLink = new NpcScriptAddLink(this, spec.Tag, _ownerEpoch);

            if (enemy != null)
                add.Brain?.AddThreat(enemy.Identity, NpcAiRules.ProximityHate);
        }
    }

    /// <summary>Drops owned NPCs that have died or left the world.</summary>
    void PruneOwned()
    {
        if (_owned == null)
            return;
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            NpcCharacter owned = _owned[i];
            if (owned.IsDead || owned.Playfield == null)
                _owned.RemoveAt(i);
        }
    }

    /// <summary>The owner died, reset or despawned: each live add despawns after its own delay (0 = next tick).</summary>
    void ReleaseOwned(long now)
    {
        if (_owned == null || _owned.Count == 0)
            return;
        for (int i = 0; i < _owned.Count; i++)
        {
            NpcCharacter owned = _owned[i];
            if (owned.IsDead || owned.Playfield == null || owned.OwnerDespawnDelayMs < 0)
                continue;
            long at = now + owned.OwnerDespawnDelayMs;
            if (owned.DespawnAtMs == 0 || at < owned.DespawnAtMs)
                owned.DespawnAtMs = at;
        }

        _owned.Clear();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // AddDied
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One of this runner's adds was killed (<see cref="NpcCharacter.OnDeath"/>; a despawn never comes here). While
    /// the owner lives and the add is from its current life, the first matching AddDied handler (by priority, passing
    /// Once, CooldownMs and ChancePercent) is queued to start after its DelayMs. Raised on this runner only: tags are
    /// program-local.
    /// </summary>
    public void OnAddKilled(NpcScriptAddLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (_detached || _npc.IsDead || _npc.Playfield == null || link.Epoch != _ownerEpoch
            || !_program.Subscribes(NpcScriptTrigger.AddDied))
            return;

        long now = Now();
        ScriptHandler[] handlers = _program.Handlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            ScriptHandler handler = handlers[i];
            if (handler.Trigger != NpcScriptTrigger.AddDied || (handler.Filter >= 0 && handler.Filter != link.Tag))
                continue;
            if (handler.Once && (_onceFired & (1UL << (i & 63))) != 0)
                continue;
            if (_cooldownUntilMs != null && _cooldownUntilMs[i] > now)
                continue;
            if (handler.ChancePercent < 100 && NextInt(0, 99) >= handler.ChancePercent)
                continue;

            _addDied ??= new List<PendingAddDied>();
            if (_addDied.Count >= MaxQueuedAddDied)
            {
                if (!_addDiedWarned)
                {
                    _addDiedWarned = true;
                    _npc.Playfield.GetService<Logging.IZoneLogger>()?.Warn(
                        "NpcScripts: program '" + _program.Id + "' on " + _npc.Name + " has " + MaxQueuedAddDied
                        + " AddDied events waiting; further ones are dropped");
                }

                return;
            }

            MarkFired(i, now);
            long due = now + handler.Filter2;
            _addDied.Add(new PendingAddDied(due, i));
            if (due < _addDiedWakeMs)
                _addDiedWakeMs = due;
            return;
        }
    }

    /// <summary>
    /// Starts the oldest due AddDied entry: at once when no handler runs, by suspending the running one when the
    /// entry's handler Interrupts (one level, never over another AddDied). Otherwise it waits for the running handler
    /// to end, which re-arms the queue.
    /// </summary>
    void TryStartDueAddDied(long now)
    {
        _addDiedWakeMs = long.MaxValue;
        List<PendingAddDied>? queue = _addDied;
        if (queue == null || queue.Count == 0)
            return;

        int pick = -1;
        long next = long.MaxValue;
        for (int i = 0; i < queue.Count; i++)
        {
            long due = queue[i].DueMs;
            if (due <= now)
            {
                if (pick < 0)
                    pick = i;
            }
            else if (due < next)
            {
                next = due;
            }
        }

        if (pick < 0)
        {
            _addDiedWakeMs = next;
            return;
        }

        int handlerIndex = queue[pick].Handler;
        if (_handler >= 0)
        {
            ScriptHandler[] handlers = _program.Handlers;
            if (!handlers[handlerIndex].Interrupts || _suspendedHandler >= 0
                || handlers[_handler].Trigger == NpcScriptTrigger.AddDied)
                return;
            Suspend();
        }

        queue.RemoveAt(pick);
        Start(handlerIndex, now, mark: false);
    }

    /// <summary>The queue's next wake: its earliest entry, no earlier than now; never when it is empty.</summary>
    void ArmAddDiedWake(long now)
    {
        _addDiedWakeMs = long.MaxValue;
        List<PendingAddDied>? queue = _addDied;
        if (queue == null)
            return;
        for (int i = 0; i < queue.Count; i++)
        {
            if (queue[i].DueMs < _addDiedWakeMs)
                _addDiedWakeMs = queue[i].DueMs;
        }

        if (_addDiedWakeMs < now)
            _addDiedWakeMs = now;
    }

    /// <summary>
    /// The owner died or reset: earlier adds no longer report, queued AddDied events are dropped and a running (or
    /// suspended) AddDied handler ends where it is.
    /// </summary>
    void CancelAddDied(long now)
    {
        _ownerEpoch++;
        _addDied?.Clear();
        _addDiedWakeMs = long.MaxValue;
        if (!_program.Subscribes(NpcScriptTrigger.AddDied))
            return;

        ScriptHandler[] handlers = _program.Handlers;
        if (_suspendedHandler >= 0 && handlers[_suspendedHandler].Trigger == NpcScriptTrigger.AddDied)
            _suspendedHandler = -1;
        if (_handler < 0 || handlers[_handler].Trigger != NpcScriptTrigger.AddDied)
            return;

        if (_waiting == WaitKind.Arrive)
        {
            _moveDestination = null;
            _npc.Brain?.ScriptReleaseMovement();
        }
        else if (_waiting == WaitKind.Cast)
        {
            ClearPendingWarp();
        }

        EndHandler(now);
    }

    void WarnSpawnOnce(string what)
    {
        if (_spawnWarned)
            return;
        _spawnWarned = true;
        _npc.Playfield?.GetService<Logging.IZoneLogger>()?.Warn(
            "NpcScripts: program '" + _program.Id + "' on " + _npc.Name + " " + what);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // CastNano
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the op's cast through <see cref="NanoRuntime.TryStartCast"/> (requirements, cost, recharge and target
    /// legality as for any caster). True when the op blocks for the cast bar; false when nothing was started, or the
    /// nano was instant and has already resolved, so ops go on.
    /// </summary>
    bool BeginCast(ScriptOp op, long now)
    {
        if (_npc.IsDead || _npc.Playfield == null)
            return false;

        int[]? list = (op.D & NpcScriptCompiler.CastNanoList) != 0 ? _program.NanoLists[op.A] : null;
        if ((NpcScriptTarget)op.B == NpcScriptTarget.Playfield)
            return BeginPlayfieldCast(op, list, now);

        int nanoId = IntValue(list == null ? op.A : list[NextInt(0, list.Length - 1)]);
        Character? recipient = (NpcScriptTarget)op.B == NpcScriptTarget.RandomNearby
            ? PickRandomNearby(op.C, op.D >> NpcScriptCompiler.CastHashListShift)
            : PickTarget((NpcScriptTarget)op.B, op.C);
        if (nanoId <= 0 || recipient == null)
            return false;

        // Armed before the cast: an instant nano lands inside TryStartCast and warps from there.
        if ((op.D & NpcScriptCompiler.CastWarpToSelf) != 0)
        {
            _pendingWarpNano = nanoId;
            _pendingWarpTarget = recipient.Identity;
        }

        NanoCastRefusal refusal = NanoRuntime.TryStartCast(_npc, nanoId, recipient.Identity, DateTime.UtcNow);
        if (refusal != NanoCastRefusal.None)
        {
            ClearPendingWarp();
            Utility.LogUtil.Debug(
                Utility.DebugInfoDetail.Engine,
                "NpcScripts: program '" + _program.Id + "' on " + _npc.Name + " could not cast " + nanoId + ": " + refusal);
            return false;
        }

        if (!_npc.IsCastingNano)
        {
            ClearPendingWarp();
            return false;
        }

        _waiting = WaitKind.Cast;
        _wakeAtMs = now + CastPollMs;
        return true;
    }

    /// <summary>
    /// Target Playfield: one cast bar on the NPC (<see cref="NanoRuntime.TryStartPlayfieldCast"/>) that lands on every
    /// living player on its playfield, each picking from <paramref name="list"/> on its own. Blocks as a cast does.
    /// </summary>
    bool BeginPlayfieldCast(ScriptOp op, int[]? list, long now)
    {
        List<int> nanos = _castNanos ??= new List<int>(4);
        nanos.Clear();
        if (list == null)
        {
            nanos.Add(IntValue(op.A));
        }
        else
        {
            for (int i = 0; i < list.Length; i++)
                nanos.Add(IntValue(list[i]));
        }

        NanoCastRefusal refusal = NanoRuntime.TryStartPlayfieldCast(_npc, nanos, DateTime.UtcNow);
        if (refusal != NanoCastRefusal.None)
        {
            Utility.LogUtil.Debug(
                Utility.DebugInfoDetail.Engine,
                "NpcScripts: program '" + _program.Id + "' on " + _npc.Name + " could not cast on the playfield: " + refusal);
            return false;
        }

        if (!_npc.IsCastingNano)
            return false;

        _waiting = WaitKind.Cast;
        _wakeAtMs = now + CastPollMs;
        return true;
    }

    Character? PickTarget(NpcScriptTarget picker, int minDistance)
    {
        switch (picker)
        {
            case NpcScriptTarget.Self:
                return _npc;
            case NpcScriptTarget.Target:
                return _npc.Brain?.ResolveCurrentTarget();
            default:
                return PickRandomHostile(minDistance);
        }
    }

    /// <summary>
    /// A random player on the hate list that the NPC may attack, at least <paramref name="minDistance"/> metres
    /// away (edge to edge). Reservoir pick over the list, no allocation. Pets and other NPCs are never picked.
    /// </summary>
    Character? PickRandomHostile(int minDistance)
    {
        NpcBrain? brain = _npc.Brain;
        Playfield? playfield = _npc.Playfield;
        if (brain == null || playfield == null || brain.Hate.IsEmpty)
            return null;

        DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
        Character? chosen = null;
        int eligible = 0;
        foreach (HateEntry entry in brain.Hate.Entries)
        {
            if (!registry.TryGet(entry.Identity, out Dynel? dynel) || dynel is not Player player || player.IsDead)
                continue;
            if (!ReferenceEquals(player.Playfield, playfield) || !CombatRules.CanAttack(_npc, player))
                continue;
            if (minDistance > 0 && _npc.GetEdgeDistanceTo(player) < minDistance)
                continue;

            eligible++;
            if (NextInt(0, eligible - 1) == 0)
                chosen = player;
        }

        return chosen;
    }

    /// <summary>
    /// A random living NPC near this one (the locality cell and its neighbours, <see cref="PlayfieldLocality"/>),
    /// other than this NPC and never a player's pet, within <paramref name="maxDistance"/> metres (edge to edge) and
    /// spawned with one of the hashes in <see cref="NpcScriptProgram.HashLists"/>[hashList - 1]. Reservoir pick over a
    /// reused buffer.
    /// </summary>
    NpcCharacter? PickRandomNearby(int maxDistance, int hashList)
    {
        Playfield? playfield = _npc.Playfield;
        if (playfield == null || hashList <= 0 || hashList > _program.HashLists.Length)
            return null;

        string[] hashes = _program.HashLists[hashList - 1];
        List<NpcCharacter> nearby = _nearby ??= new List<NpcCharacter>();
        playfield.GetRequiredService<PlayfieldLocality>().CollectNearby(_npc, maxDistance, nearby);
        NpcCharacter? chosen = null;
        int eligible = 0;
        for (int i = 0; i < nearby.Count; i++)
        {
            NpcCharacter other = nearby[i];
            if (ReferenceEquals(other, _npc) || other.IsDead || other.Pet != null || Array.IndexOf(hashes, other.SpawnHash) < 0)
                continue;
            if (_npc.GetEdgeDistanceTo(other) > maxDistance)
                continue;

            eligible++;
            if (NextInt(0, eligible - 1) == 0)
                chosen = other;
        }

        nearby.Clear();
        return chosen;
    }

    void ClearPendingWarp()
    {
        _pendingWarpNano = 0;
        _pendingWarpTarget = Identity.None;
    }

    /// <summary>
    /// Moves <paramref name="recipient"/> to the NPC's feet: <see cref="FeetOffsetMeters"/> from its centre on the side
    /// the recipient came from (ahead of the NPC when they overlap). A player goes as a lift lands it
    /// (<see cref="Player.TeleportWithinPlayfield"/>) and its living pets are set down with it, as the Abmouth capture
    /// of 2026-07-20 shows (N3Teleport for the player, SetPos for both pets); an NPC recipient is warped on the floor.
    /// </summary>
    void WarpToFeet(Character recipient)
    {
        Playfield? playfield = _npc.Playfield;
        if (playfield == null || _npc.IsDead || recipient.IsDead || ReferenceEquals(recipient, _npc)
            || !ReferenceEquals(recipient.Playfield, playfield))
            return;

        Vector3 at = _npc.Position;
        Vector3 from = recipient.Position;
        double dx = from.x - at.x;
        double dz = from.z - at.z;
        double length = Math.Sqrt((dx * dx) + (dz * dz));
        Vector3 landing;
        if (length > 0.01)
        {
            landing = new Vector3(at.x + (dx / length * FeetOffsetMeters), at.y, at.z + (dz / length * FeetOffsetMeters));
        }
        else
        {
            var ahead = (Vector3)_npc.Rotation.RotateVector3(new Vector3(0, 0, FeetOffsetMeters));
            landing = new Vector3(at.x + ahead.x, at.y, at.z + ahead.z);
        }

        switch (recipient)
        {
            case Player player:
                player.TeleportWithinPlayfield(landing);
                foreach (NpcCharacter pet in player.OwnedPets.All)
                {
                    if (!pet.IsDead && ReferenceEquals(pet.Playfield, playfield))
                        pet.Brain?.WarpTo(landing);
                }

                break;
            case NpcCharacter npc:
                npc.Brain?.WarpTo(landing);
                break;
        }
    }

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

    /// <summary>A <see cref="ScriptOpCode.Random"/> block index: even when C is 0, else by the running weight totals.</summary>
    int PickBlock(ScriptOp op)
    {
        if (op.C <= 0)
            return NextInt(0, op.A - 1);

        int roll = NextInt(0, op.C - 1);
        int[] jumps = _program.Jumps;
        int totals = op.B + op.A;
        for (int i = 0; i < op.A - 1; i++)
        {
            if (roll < jumps[totals + i])
                return i;
        }

        return op.A - 1;
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
