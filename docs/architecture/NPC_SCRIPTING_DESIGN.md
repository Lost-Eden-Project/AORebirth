# NPC server-side scripting

Status: implemented 2026-10-21 in `ZoneEngine_New/Core/NpcScripts` (catalog, compiler, runner), with the brain's
movement lease, the `GfxTriggerMessage` and the hooks listed below. See "Implementation notes" at the end for the
verbs and triggers that are in, the ones still open, and where the design was adjusted. Grounded in the current
`ZoneEngine_New` runtime and in the AO client (Gamecode.dll, Ghidra) for the two wire messages the scripts need.
PR #57 (`NPC_EVENTS_PLAN.md`) was read as reference only, see "Relation to PR #57" at the end.

## Goal

NPCs run small server-side programs: walk a route and stop at each point to emote and drop a world item (Careless
Citizen), fire a scene at a health threshold, patrol, talk in vicinity chat, play effects, react to a nano ending,
and whatever comes next. The system must cost nothing for the thousands of NPCs that have no script, next to nothing
for the ones that do, and grow by adding a verb or a trigger, never by redesign.

All script content lives under `GameData/NpcScripts/`. There is no playfield-specific content file.

## What exists and is reused

| Need | Existing mechanism | Where |
| --- | --- | --- |
| Per-NPC ticking that stops when no player is near | `CellHeatScheduler` ticks dynels by cell heat; asleep cells do not tick | `Core/Playfield/Locality/CellHeatScheduler.cs` |
| NPC decision cadence | `NpcBrain.Tick` thinks every 0.5 s (`NpcAiRules.BrainThinkSeconds`), tree order leash > combat > patrol > wander > idle | `Core/Ai/NpcBrain.cs:273`, `Core/Ai/NpcBehaviourTree.cs` |
| Pathing and arrival | `NpcBrain.PathTo`, `NpcAiRules.IsNearby(..., ArriveHomeMeters)`, stuck watch `ProgressSinceUtc` | `Core/Ai/NpcAiActions.cs:151` (PatrolNode) |
| Say in vicinity | `VicinityChatRelay.Speak(Character, text, type)` | `Core/Chat/VicinityChatRelay.cs:45` |
| Emote | `SocialActionCmdMessage` (AbstractAnimID 1..71) announced through `Cell.Announce` | `Core/Entities/Character.cs:2118` |
| Spawn a world item | `SpawnService.SpawnDroppedItem(item, pos, rot, owner, lifetime)`, `WorldItem.ExpiresAtUtc` | `Core/Playfield/SpawnService.cs:385` |
| Spawn an NPC | `SpawnService.SpawnMob(template, pos, heading, level, source, spawnHash, configure)` | `Core/Playfield/SpawnService.cs:144` |
| Hooks already in the right place | `NpcCharacter.OnDamaged`, `Character.OnDeath`, `NpcCharacter.OnReset`, `NpcBrain.AddThreat`, `NanoRuntime.ExecuteBuffEnd`, `NpcCharacter.TryUse`, Knubot open/close | listed per trigger below |
| Data compiled at load, validated, with errors reported | Knubot: JSON file -> `KnubotParser` -> `KnubotCondition` / `KnubotPlan` records, `KnubotLoadResult` | `Core/Knubot/*` |
| Spawn-time configuration | `HashSpawnPoint` + `configure: Action<NpcCharacter>` in `HashSpawnSystem` | `Core/Playfield/HashSpawnSystem.cs:879` |
| Placed NPCs with stable keys | `PlayfieldContent/<pf>/Npcs.json` (`Key`), read-only input for binding targets | `Core/GameData/PlayfieldNpcContentCatalog.cs` |
| Visibility-scoped broadcast | `PlayfieldLocality.Announce(source, message)` | `Core/Playfield/Locality/PlayfieldLocality.cs:248` |

Nothing above needs replacing. The scripting layer is a compiler, a per-NPC runner, one behaviour-tree node, one new
wire message (`GfxTrigger`) and a handful of one-line hook calls.

## Shape of the system

```
GameData/NpcScripts/*.json      programs (what to do) + their bindings (which NPCs, with what arguments)
            |
            v  load: parse -> validate -> compile
NpcScriptCatalog        immutable, shared: NpcScriptProgram[] (handlers + flat ScriptOp[] bytecode + constant tables)
                        + binding indexes: by hash, by (playfield, spawn point key), by (playfield, npc key)
            |
            v  spawn: SpawnService.SpawnMob resolves the binding -> npc.AttachScript(new NpcScriptRunner(program, args))
NpcScriptRunner         per NPC, ~100 bytes: program ref, pc, wakeAtMs, handler state, 8 int vars, xorshift rng
            ^
            |  advance: NpcCharacter.Tick -> Scripts?.Tick(nowMs)   (early-out on wakeAtMs: one compare per tick)
            |  events:  hooks call npc.Scripts?.Raise(trigger, arg) (early-out on a subscription bitmask)
```

Three rules keep it fast and safe:

1. **Pay only when scripted.** `NpcCharacter.Scripts` is null for an unscripted NPC; every hook is `Scripts?.Raise(...)`.
2. **No work between wakes.** A runner that is waiting (delay, walking, animating) is a `long` compare per tick.
   No central scheduler, no heap, no bookkeeping on despawn or cell sleep: the existing heat scheduler already
   decides whether the NPC ticks at all, so a sleeping cell's runners are paused for free and resume with the cell.
3. **Compile once, interpret structs.** Programs are compiled at load into a flat `ScriptOp[]` (struct: opcode +
   four int operands) with constant tables for strings, vectors and item hashes. Execution is a `switch` on the
   opcode. No string parsing, dictionary lookups, delegates or allocations on the hot path.

## Data model

### One file per program, bindings included (`GameData/NpcScripts/<name>.json`)

JSON Schema: `AORebirth/Schemas/NpcScripts.schema.json` (hand-maintained; the compiler is the authority). Point a
file at it with `"$schema": "../../Schemas/NpcScripts.schema.json"` for editor completion and validation.

```json
{
  "Id": "careless-citizen",
  "Params": {
    "Waypoints": "Vector3[]",
    "Litter": "ItemHash",
    "Emote": "SocialAction"
  },
  "Handlers": [
    {
      "On": "Spawned",
      "Do": [
        { "Label": "loop" },
        { "WalkTo": "$Waypoints[next]" },
        { "Wait": [1000, 2500] },
        { "Emote": "$Emote" },
        { "Wait": 2000 },
        { "SpawnWorldItem": { "Item": "$Litter", "Offset": [0.5, 0, 0.5], "LifetimeSeconds": 120 } },
        { "Goto": "loop" }
      ]
    },
    {
      "On": "HealthBelow", "Percent": 50, "Once": true, "Interrupts": true,
      "Do": [ { "Say": "Help! Somebody help!" }, { "Emote": "Panic" } ]
    },
    { "On": "Reset", "Do": [ { "Restart": "Spawned" } ] }
  ],
  "Bindings": [
    {
      "Playfield": 6553,
      "SpawnPoint": "ROTE@3543.2,7.3,778.7",
      "Args": {
        "Waypoints": [[3543.2, 7.3, 778.7], [3549.9, 6.9, 778.7], [3559.2, 6.9, 779.1]],
        "Litter": "CRUMPLED_CAN",
        "Emote": "Shrug"
      },
      "Provenance": "capture:2026-10-18"
    },
    {
      "Playfield": 6553,
      "SpawnPoint": "ROTE@3509.1,5.5,793.8",
      "Args": { "Waypoints": [[3509.1, 5.5, 793.8], [3512.4, 5.1, 794.3]], "Litter": "CRUMPLED_CAN", "Emote": "Shrug" }
    }
  ]
}
```

- `Params` declares what a binding must supply and its type; the compiler checks every `$Name` against it, so a
  route change is a data edit on a binding and the program is shared by every NPC that walks a route and litters.
- `Handlers` are `(trigger, filter, policy, ops)`. Policy: `Once`, `CooldownMs`, `ChancePercent`, `Interrupts`
  (preempts the running handler instead of being dropped), `Priority` (ties between simultaneous triggers).
- Ops are a flat list. Control flow is `Label` / `Goto` / `Random` (pick one of N blocks) / `If` (one compiled
  predicate, jump if false). Deliberately not a tree of nested blocks: compiling to a flat array keeps the runner a
  `pc` integer and resume-after-interrupt trivial.
- `Bindings` say which NPCs run the program. A program with no bindings compiles and is reported as unused.

A program used by many playfields can keep its bindings in a sibling file instead, `GameData/NpcScripts/<name>.bindings.json`
with `{ "Script": "<id>", "Bindings": [...] }`, so a long list of routes does not bury the program. Same folder,
same loader, same shape; nothing lives under `PlayfieldContent`.

### Binding targets

Exactly one of:

| Target | Scope | Needs `Playfield` |
| --- | --- | --- |
| `Hash` | every NPC of that `NpcTemplates.json` hash, on every playfield (or on one when `Playfield` is given) | optional |
| `SpawnPoint` | the NPC that comes out of one `Spawns.json` point | yes |
| `NpcKey` | one placed NPC, by its `Key` in `PlayfieldContent/<pf>/Npcs.json` | yes |

Resolution at spawn, most specific wins: `SpawnPoint` > `NpcKey` > `Hash` with `Playfield` > `Hash`. One program per
NPC. Where two behaviours must coexist they are two handlers of one program, which is what keeps movement ownership
unambiguous.

**Spawn point key**: `<hashText>@<x>,<y>,<z>` with the position rounded to 0.1 m, derived by the server at load from
fields `Spawns.json` already has (`hashText`, `position`). Stacked points at one position get `#2`, `#3` in RDB
order. No extractor change, readable in a diff; the loader rejects a key that matches no point or more than one.
`Spawns.json` itself is never edited (it is rewritten on every extraction).

**Plain movement without a program.** A binding may carry `"Movement": { "Patrol": {...} }` or `"Wander": {...}`
and no `Script`; that configures the existing `PatrolNode` / `EnableWander` directly. Scripts are for anything beyond
plain movement. Both may be present: a program's movement verbs take precedence while a handler holds the movement
lease (below), and the plain patrol resumes when it is released.

### Compiled form

```csharp
public readonly record struct ScriptOp(ScriptOpCode Code, int A, int B, int C, int D);

public sealed class NpcScriptProgram
{
    public string Id;
    public ScriptHandler[] Handlers;      // sorted by Trigger, then Priority
    public ScriptOp[] Ops;                // all handlers' ops concatenated; handler = [Start, End)
    public string[] Strings;              // Say texts, labels for diagnostics
    public Vector3[] Vectors;             // fixed waypoints/offsets
    public uint TriggerMask;              // 1 << trigger for every handler present
    public int[] HealthThresholdsDesc;    // HealthBelow percents, descending
}

public readonly record struct ScriptHandler(NpcScriptTrigger Trigger, int Filter, int Start, int End,
    bool Once, int CooldownMs, byte ChancePercent, bool Interrupts, byte Priority);
```

Binding `Args` compile into a per-binding `ScriptArgs` (vector array, item template ids, anim ids, gfx ids)
referenced by the runner; ops address arguments by slot index, so one program instance serves every binding.

## Runtime

### Runner

```csharp
public sealed class NpcScriptRunner
{
    readonly NpcScriptProgram _program;
    readonly ScriptArgs _args;
    readonly NpcCharacter _npc;
    int _handler = -1;                 // running handler index, -1 idle
    int _pc;                           // next op
    long _wakeAtMs;                    // long.MaxValue when idle; now >= _wakeAtMs means "needs service"
    WaitKind _waiting;                 // None, Delay, Arrive, AnimEnd
    int _waypointCursor;               // for $Waypoints[next]
    int _nextHealthThreshold;          // index into HealthThresholdsDesc
    ulong _onceFired, _rngState;       // handler bitset; xorshift64*
    long[] _cooldownUntilMs;           // per handler, allocated only when any handler has a cooldown
    int[] _vars;                       // 8 ints, allocated only when the program uses SetVar
    int _spawnedByScript;              // live entities owned by this runner (cap)
}
```

`Tick(long nowMs)`:

```
if (nowMs < _wakeAtMs) return;                      // the whole cost for a waiting runner
if (_waiting == Arrive) { if (!ArrivedOrStuck()) { _wakeAtMs = nowMs + 250; return; } }
run ops from _pc until one blocks or the handler ends, at most OpBudget (32) instant ops per tick
```

Blocking ops set `_wakeAtMs`: `Wait` to `now + delay`; `WalkTo` to `now + 250` and `_waiting = Arrive` (arrival is
polled at 4 Hz, cheaper than the brain's own think and plenty for a walk); `Emote` to `now + animMs` (a per-
`SocialAction` duration table; default 2000 ms) so "emote, then say" sequences line up without the author timing
them. An idle runner (`_handler == -1`) has `_wakeAtMs = long.MaxValue` and never runs ops.

Time is the playfield's monotonic millisecond clock passed in from the tick (Knubot takes the same `Func<long>`),
not `DateTime.UtcNow` per op.

`Raise(NpcScriptTrigger trigger, int arg)`:

```
if ((_program.TriggerMask & (1u << (int)trigger)) == 0) return;   // the whole cost for an unsubscribed trigger
pick the first handler with this trigger whose filter matches arg, not Once-fired, not cooling down, passes Chance
if a handler is running: Interrupts ? preempt (clear wait, drop movement lease) : drop the trigger
start: _handler = h, _pc = h.Start, _wakeAtMs = nowMs  (ops run on this NPC's next Tick, never re-entrantly inside the hook)
```

Starting on the next tick rather than inside the hook matters: `OnDamaged` and `ExecuteBuffEnd` run mid-combat-
resolution, and a script that despawns or moves the NPC from inside them would corrupt the caller's state.

### Triggers

| Trigger | Fired from | Arg / filter |
| --- | --- | --- |
| `Spawned` | `SpawnService.SpawnMob` after `configure` | - |
| `Arrived` | runner itself when a `WalkTo` completes; also `PatrolNode` when its waypoint is reached (plain patrols get "stop and do X at waypoint N") | waypoint index |
| `Stuck` | runner when `ProgressSinceUtc` exceeds the brain's stuck window during `WalkTo` | waypoint index |
| `HealthBelow` | `NpcCharacter.OnDamaged`; percent computed only when the mask has the bit, compared against `HealthThresholdsDesc[_nextHealthThreshold]` so each hit is one compare; threshold cursor resets on `Reset`/heal above | percent |
| `Death` | `NpcCharacter.OnDeath` (before corpse) | - |
| `Reset` | `NpcCharacter.OnReset` (leash finished, HP restored) | - |
| `CombatStart` / `CombatEnd` | `NpcBrain.AddThreat` first entry / hate emptied | - |
| `NanoEnded` / `NanoLanded` | `NanoRuntime.ExecuteBuffEnd` / buff applied, on the NPC | nano id (0 = any) |
| `Timer` | runner: a handler with `On: "Timer", "EveryMs": [min,max]` re-arms itself via `_wakeAtMs`; the only polling trigger and it polls nothing | - |
| `Used` | `NpcCharacter.TryUse` | - |
| `DialogueOpened` / `DialogueClosed` | `KnubotService` conversation start / end | - |
| `PlayerNear` | `NpcBrain.ScanProximity`, which already runs every think for aggro; adds one distance check against the handler's radius only when subscribed | radius (m) |
| `Signal` | another runner's `Signal` verb | signal name index |

Adding a trigger: one enum member, one `Scripts?.Raise(...)` line at the hook, one row here.

### Verbs

Instant unless marked blocking. Operands are constants or `$Param` slots.

| Verb | Does | Notes |
| --- | --- | --- |
| `Wait` ms or `[min,max]` | blocking delay | clamped to >= 100 ms |
| `WalkTo` / `RunTo` vector, `$Waypoints[i]`, `$Waypoints[next]`, `Home`; or `{ "To": point, "Direct": true }` | blocking: takes the movement lease, `brain.PathTo`; completes on arrival or stuck. `Direct` walks the straight line without the navmesh: the bridge across a short gap the mesh does not cover (a ramp too steep to bake, between two mesh islands). A non-direct destination is shifted per NPC by `NpcSpread` (up to 4 m, from the NPC's identity) so a group sent to one point fans out; a direct leg is walked exactly | raises `Arrived` / `Stuck` |
| `Face` heading or target | set heading, announce | |
| `Emote` SocialAction | the player emote message from the NPC (below); blocking for the anim's duration | 1..71 |
| `Say` text, `Type` | `VicinityChatRelay.Speak` | rate limit 1/s per NPC |
| `SpawnWorldItem` item, offset (in the NPC's frame: x right, y up, z ahead), lifetime, `Count` | mint via `HashItemMinter`, `SpawnDroppedItem` with no owner | lifetime mandatory, per-runner live cap (8) |
| `SpawnNpc` hash, offset, `DespawnWithOwner` | `SpawnMob` with `SpawnSource.Script`; tracked by the runner | cap (4) |
| `Despawn` | `SpawnService.DespawnNpc(self)`, ends the program | |
| `Gfx` id, `On` / `At` / `From`+`To` | `GfxTriggerMessage` (below) to the NPC's observers | |
| `CastNano` id, `Self` / `Target` | existing nano runtime entry | |
| `SetStat` stat, value | `Stats.Set(..., dirty: true)` for pose / flags style spawn states | stat by `CharacterStat` name only |
| `SetVar` / `AddVar` / `If` var op value, `Goto` | 8 int slots, compiled predicates | `If` also supports `Health <`, `HasTarget`, `PlayersNear r`, `Chance p` |
| `Random` [blocks] | jump to one block, equal or weighted | compiled to jump table |
| `Signal` name, radius | `Raise(Signal)` on scripted NPCs in own + adjacent cells within radius (`PlayfieldLocality.CollectNearby`) | multi-NPC scenes |
| `ReleaseMovement` | gives movement back to patrol / wander / idle without ending the handler | |
| `SetHome` `Here` or point | re-posts the NPC: `brain.Home` (leash, return, wander anchor) becomes the point. While a `WalkTo`/`RunTo` (or a brain patrol leg) is under way the brain's distance leash measures from the walk's target (`NpcBrain.LeashAnchor`), not the spawn, so a march or patrol may go beyond the 70 m leash range | |
| `Restart` trigger | re-enter a handler (used by `Reset` to resume a route) | |
| `End` | stop the handler | implicit at `End` index |

Adding a verb: one `ScriptOpCode` member, one case in the compiler (JSON -> op + operand validation), one case in
the interpreter.

### Movement ownership

The brain stays the only writer of the motor. A script moves the NPC through a **movement lease** the brain
exposes: `WalkTo` sets `brain.ScriptedDestination`; a new `ScriptedMoveNode` in `NpcBehaviourTree` sits after the
combat branch and before `patrol-enabled`:

```
root: leash > combat > scripted-move (lease held) > patrol > wander > idle
```

So combat and leash preempt a walking script exactly as they preempt a patrol today, with no new code paths in the
fight branch. When hate arrives the runner gets `CombatStart`; a handler marked `Interrupts: false` (the default for
movement handlers) is suspended with its `pc` intact, the lease is dropped, and on `Reset` the `Restart` policy
(default: resume at the same op, re-issuing the `WalkTo`) picks it up. Programs that must not be disturbed (a
pacified actor in a scripted scene) set `"Combat": "Ignore"` on the binding, which the existing `IsPacified` already
covers.

## Wire messages

### Emote: the player message, sent for the NPC

NPCs emote with the same `SocialActionCmdMessage` (`N3MessageType.SocialActionCmd`) a player's emote is relayed
with, from the NPC's identity: `Identity = npc`, `CommandState = 1` (the client acts only on 1), `CommandRef = 0`
(a ref is only compared against the receiving client's own counter, so every receiver treats an NPC's command as
foreign and plays it), `Action` = the AbstractAnimID, which the client accepts in 1..71. Sent through
`Cell.Announce` as `Character.AnnounceSocialAction` does for players.

### Gfx: `GfxTriggerMessage` (new in `AOtomation.Messaging`)

`N3MessageType.GfxTrigger` (0x7a222202) exists in the enum with no message class. The client side is
`GfxTriggerIIR_t` in Gamecode.dll (registered by name at 1000f445; vtable 1015e3f4; read 10039879, write 10039937,
apply 100399ea). After the standard N3 header the body is:

```
int32  Type         1..6, selects the payload and the effect anchor
int32  GfxId        gfx template id (49999 = none, the client ignores it)
then, by Type:
  1: Vector3 Pos                              -> effect at a world position
  2: Identity Target, int32 Extra             -> effect on a character
  3: Vector3 Pos, Vector3 Pos2                -> effect from a position to a position
  4: Identity Target, Vector3 Pos             -> effect at a position, bound to a character
  5: Identity Target, Vector3 Pos, int32 Extra-> effect on a character at an offset position
  6: Identity A, Identity B, int32 Extra      -> effect between two characters
```

`Vector3` is three floats x, y, z (reader 1000404e); `Identity` is int32 type then int32 instance (reader
1013d0d1). The apply function hands these to `_EffectHandler_t::CreateEffect2` overloads.

What the client does with them, and therefore what the server must respect:

- A `Target` identity is resolved with `GetDynel` and dynamic-cast to `SimpleChar_t`. Types 2, 4, 5 and 6 only
  work on characters (players, NPCs); for a world item or a static dynel use type 1 or 3 at its position.
- `Extra` is passed straight through to `CreateGfxControl`. Every client-internal caller passes 0
  (`N3Msg_FlashHead` at 100169f5, `n3EngineClientAnarchy_t::CreateEffect` at 1001685d). The server sends 0.
- The gfx template's category must match the anchor: `CreateGfxControl` builds a control only when the template's
  category (field +4 of the template record) is 0x3f2 for character-anchored types (2, 5, 6), 0x3f3 for
  position-anchored types (1, 4) and 0x3f3 or 0xbbf for a position pair (3). A wrong pairing is silently nothing.
  The loader validates the pairing from an offline table of gfx template categories (built once from the client's
  template storage, `_EffectHandler_t::GetGfxTemplateStorage`); until that table exists the `Gfx` verb accepts
  any id and the mismatch shows up as no effect, never as an error on the client.
- Type 6 reads both identities but the apply function resolves A twice (100399ea, the `pnVar3` lookup uses +0x20),
  so a two-character effect is drawn on A alone. The server does not expose type 6 to script authors.
- Effects are subject to the player's effect-category settings except category 0x3fc (always on).

The `Gfx` verb maps `On: Self|Target` to type 2, `At: [x,y,z]` to type 1, `From`/`To` positions to type 3, and
`At` with `On` to type 4. Sent through `Cell.Announce` from the NPC, so it reaches exactly the players who see it.

### Everything else

`Say`, world items, spawning, stats and pathing use paths that already send verified packets.

## Load and bind

1. `NpcScriptCatalog.Load(root)` reads every `GameData/NpcScripts/*.json`, compiles each program, resolves its
   `Bindings` (`Args` against `Params`, item hashes to template ids, anim names to ids) and collects errors into a
   `NpcScriptLoadResult` (the Knubot shape). A program or binding with an error is dropped; everything else loads.
   The catalog indexes bindings by hash, by (playfield, spawn point key) and by (playfield, npc key).
2. Spawn point keys resolve when a playfield initialises (`HashSpawnSystem.Initialize` already walks every point):
   each `HashSpawnPoint` gets its key, and the playfield's bindings for unknown keys are logged once there.
3. `SpawnService.SpawnMob` gains a `spawnPointKey` (passed by `HashSpawnSystem`, which already passes
   `spawnHash`). After `configure`, it asks the catalog for the most specific binding and attaches the runner, then
   raises `Spawned`. Placed NPCs (`NpcContentActivationService`) do the same with their `Key`.
4. `DespawnNpc` detaches the runner, which despawns entities it owns with `DespawnWithOwner`.

Private GameData mirror: the runtime root follows `AO_REBIRTH_GAMEDATA_PATH` as for every content file; public
`GameData/NpcScripts` carries the schema and example programs.

## Validation at load

Fail-closed per program/binding, never per server:

- unknown verb, trigger, param type or `$Param`; missing or wrong-typed `Args`;
- `Goto` to a missing label, `Random` with no blocks, `If` with an unknown predicate;
- `Emote` id outside 1..71; `Say` text empty or over the chat limit; `Wait` below 100 ms; `SetStat` naming a stat
  not in `CharacterStat`; `Gfx` anchor/category mismatch once the category table exists;
- item hash the item catalog cannot resolve; NPC hash with no template;
- binding with none or more than one of `Hash` / `SpawnPoint` / `NpcKey`; `SpawnPoint` or `NpcKey` without
  `Playfield`; key matching zero or several points; one target bound by two programs;
- a program whose `Spawned` handler has no blocking op in a loop (would spin against the op budget every tick).

`GameDataContractCheck` can run the same compiler over the tree offline.

## Performance

Steady state for N scripted NPCs in ticking cells: N `long` compares per heartbeat plus the ops of the few runners
that are due. For 2,000 scripted NPCs at 32 Hz that is 64k compares a second, below the noise of the dynel tick that
already runs. Unscripted NPCs: one null check. Asleep cells: nothing, because the dynel does not tick.

Memory: one shared `NpcScriptProgram` per script, one `ScriptArgs` per binding, about 100 bytes per runner plus
optional arrays only for programs that use cooldowns or variables.

Hot-path guarantees:

- no allocation in `Tick`, `Raise` or any verb except the ones that spawn things (those allocate the entity);
- no LINQ, delegates or virtual dispatch in the interpreter; structs and a `switch`;
- `Raise` is a mask test before anything else; `HealthBelow` is one integer compare per hit;
- per-tick op budget (32 instant ops) so a bad `Goto` loop costs a bounded slice and logs once;
- per-runner caps on spawned items/NPCs and a `Say` rate limit, so data cannot flood a cell;
- everything runs on the playfield thread inside the existing tick lock; no locks of its own.

Metrics: a `TickStallWatch` stage `npc.script` around runner ticks, a counter of handlers started per trigger, and
`get_npc_brain` (debug MCP) extended with program id, handler, `pc`, wait kind and wake time.

## Phases

1. Catalog + compiler + runner; triggers `Spawned`, `Arrived`, `Stuck`, `Timer`, `HealthBelow`, `Death`, `Reset`;
   verbs `Wait`, `WalkTo`/`RunTo`, `Face`, `Emote`, `Say`, `SpawnWorldItem`, `Random`, `Goto`/`Label`, `Restart`,
   `End`; bindings by hash, spawn point and npc key; `Movement` shorthand mapping to the existing patrol/wander.
   Acceptance: one Careless Citizen bound on a captured route, emoting and littering at each stop, resuming after a
   fight.
2. `GfxTriggerMessage` + `Gfx` (types 1 to 5), `CombatStart`/`CombatEnd`, `NanoEnded`/`NanoLanded`, `Used`,
   `PlayerNear`, `SetStat`, `CastNano`, `SpawnNpc`, `Despawn`, variables and `If`.
3. `Signal` scenes, `DialogueOpened`/`Closed` from Knubot, the gfx category table for load-time validation, offline
   contract check, hot reload of `NpcScripts` if a GameData reload path exists by then.

## Implementation notes

Files: `Core/NpcScripts/NpcScriptModel.cs` (enums, ops, program, binding), `NpcScriptCompiler.cs` (JSON to
bytecode, bindings and args, validation), `NpcScriptCatalog.cs` (folder load, indexes, `SpawnPointKey`, attach),
`NpcScriptRunner.cs` (per-NPC execution). Hooks: `NpcCharacter` (`Scripts`, tick, damage, death, reset, use),
`NpcBrain` (movement lease, `CombatStart`, stuck), `NpcBehaviourTree` / `NpcAiActions` (`ScriptedMoveNode`,
`Arrived` from `PatrolNode`), `SpawnService.SpawnMob` (`spawnPointKey`, attach, despawn), `HashSpawnSystem`
(keys), `NpcContentActivationService` (placed NPCs by `Key`), `NanoRuntime.ExecuteBuffEnd` (`NanoEnded`),
`Program.cs` (singleton). `GfxTriggerMessage` lives in `AOtomation.Messaging` and is listed in both its legacy
project and `SharedBuild/source-inventory`.

In: triggers `Spawned`, `Arrived`, `Stuck`, `HealthBelow`, `Death`, `Reset`, `CombatStart`, `CombatEnd`,
`NanoEnded`, `Timer` (`EveryMs`), `Used`; verbs `Wait`, `WalkTo`/`RunTo` (object form `To` + `Direct` for a straight-line step off the navmesh), `Emote` (optional `DurationMs`), `Say`,
`SpawnWorldItem`, `Gfx` (anchors `On: Self`, `At`, `Offset`, `From`/`To`), `Despawn`, `SetStat`, `SetVar`/`AddVar`,
`If` (jumps to its `Goto` label when the condition holds), `Goto`/`Label`, `Random`, `Restart`, `ReleaseMovement`, `SetHome`,
`End`; bindings by `Hash` (optionally per `Playfield`), `SpawnPoint`, `NpcKey`; `Movement` shorthand (`Patrol`,
`Wander`). Timer handlers re-arm themselves and never queue up. An interrupting handler suspends the running one,
which resumes where it was (a cut-short walk is re-issued).

Not yet: `Face`, `CastNano`, `SpawnNpc`, `Signal`, `PlayerNear`, `DialogueOpened`/`Closed`, `NanoLanded`, the gfx
category table, debug-MCP exposure of runner state, hot reload.

Adjustments from the design above: the runner holds a walk while the brain is busy and re-requests it when free,
so `Interrupts` is only about handler preemption; the `Emote` duration is per op (default 2000 ms) rather than a
per-animation table; `HealthBelow` handlers each fire once per life (latched until `Reset`) rather than through a
threshold cursor.

## Relation to PR #57

Taken: behaviour belongs to the spawn point (or placed NPC) rather than to the template alone; the `Arrived`
idea; provenance on bindings.

Different:

- PR #57 stores *steps* (delay / anim / say / gfx) directly on the spawn point, in a file per playfield. This
  design stores *programs* and their bindings together under `GameData/NpcScripts`, with no playfield file; one
  program serves many NPCs and routes through parameters, and behaviour grows by verbs and triggers rather than by
  new top-level fields.
- Movement is a verb inside the program (with plain `Patrol`/`Wander` kept as a binding shorthand), so "walk, stop,
  emote, drop, continue" is one handler rather than a `Movement` mode plus an `Arrived` event with `HoldMovement`.
- Spawn point identity is a readable key derived by the server from existing `Spawns.json` fields instead of an
  extractor-generated 40-bit hash. Neither stores anything in `Spawns.json`.
- `NpcTemplates.json` is untouched (public master's templates carry no `Patrol`/`Events`/`SpawnState`), so there is
  no migration step and no `Script` field on templates: a hash-wide binding is a `Hash` binding in the script file.
