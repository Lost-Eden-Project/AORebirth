# Daily Login as a zone module: plan

Status: proposal. Daily Login is subarumike's feature on public master; this plan needs their agreement before any of
it lands. Nothing here changes Daily Login behaviour: the module must do exactly what the engine does today.

## Why

Daily Login is an optional feature built on top of the game, not part of its rules. Today it lives inside
ZoneEngine_New (`Core/DailyLogin`, about 1,100 lines), which means:

- every server, and every test setup, must create `dailyloginclaims` before the zone will start;
- a server cannot turn it off;
- its catalog, file paths and claim rules ship with every engine build.

As a module it is loaded, configured, granted capabilities and switched off like any other.

## What Daily Login does today

| Part | Where | What |
|---|---|---|
| Trigger | `CharacterActionMessageHandler`, `CharacterActionType.SitDown` (263) with Target = self | The client's Daily Rewards button. The handler calls `DailyLoginClaimBridge.HandleClaim(player, session.AccountName, now)`. |
| Requests | `DailyLoginClaimFiles` | The web page (`tools-temp/icc-rk-local-web/daily/claim.php`) writes claim request files; the zone reads the pending ones for the account and character, and writes result and board files back. Roots: `AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS`, `AO_REBIRTH_ZONE_STATE_DIR`, the XAMPP folders. |
| Choosing | `DailyLoginRewardCatalog`, `DailyLoginRewardSelector` | Day 1 to 28 from `rewards.json` and `day10-profession-nanos.json`, by level and profession. |
| Granting | `DailyLoginRewardService.Claim` | Under `player.PersistenceGate`: checks space, mints the items, builds an `InventoryGrantPlan`, then one `CommitDailyLoginClaim` transaction writes the claim, the new board state and the item rows together; publishes the items only after it commits. |
| Ledger | `IDailyLoginClaimDao` on `MySqlCharacterPersistenceDao`, table `dailyloginclaims` | Board state per account and character, one claim per character per day, and replay by claim token. |

The property that matters most: the claim record and the items are one transaction. A crash can never leave a claim
without its items, or items without a claim, and a replayed token returns the first result instead of paying twice.

## What the module API lacks

1. **Granting items.** No operation puts items in a player's inventory.
2. **Storage.** A module has no persistent state, so it cannot keep the board or the claim ledger.
3. **Answering a client message.** Message observers (1.5) only watch; they cannot handle a message or keep the engine
   from handling it.
4. **Doing both together.** Even with 1 and 2, a module could not commit "the claim and its items" atomically, which
   is the property above.

## Module API 1.8 additions

### A. Module storage

Per-module key/value storage, scoped by the module id, so modules can never read or write each other's data.

- One engine table, `modulestate` (`ModuleId`, `Scope`, `Key`, `Version`, `Value` JSON, `UpdatedAtUtcTicks`), primary
  key (`ModuleId`, `Scope`, `Key`). `Scope` is `character:<id>`, `account:<name>` or `global`. This is one ordinary
  migration that every future module reuses; no module ever adds its own tables.
- `IModuleStorage` on the module context: `Get(scope, key)` returns the value and version; `TrySet(scope, key, value,
  expectedVersion)` writes only if the version still matches (compare-and-set), so two zones cannot both claim day 5.
- Capability `module-storage`. Size cap per value (for example 64 KB) and per module, logged when exceeded.
- Reads and writes run on a worker thread and return a `Task`, like account operations.

### B. Granting items

Operation `player.grant-items` (capability of the same name), target a character:

- `items`: a list of { template id or item hash, high id, QL, count }.
- `key`: an idempotency key the module chooses (the Daily Login claim token). The engine records it with the grant; a
  second call with the same key returns the first result and grants nothing.
- `state` (optional): one module storage write { scope, key, value, expectedVersion } committed in the same
  transaction as the items. This is what keeps "the claim and its items" atomic.
- All or nothing: if the items do not all fit (main inventory and overflow rules as today), nothing is granted and the
  result says `no_space`. On commit-outcome-unknown the player is quarantined, as the engine does now.
- Runs on the player's playfield tick through `InventoryGrantPlan`, so the client is told the normal way.

### C. Handling client messages

`RegisterMessageHandler(handler, MessageSubscription)` (capability `handle-messages`, only during `Initialize`): an
inbound message of a subscribed type goes to the handler on the playfield tick, before the engine's own handler. The
handler returns `Handled` (the engine does nothing more) or `NotHandled` (the engine handles it as today).

- The sample gives the character, the session id and the decoded fields the existing observer exposes.
- A throwing or slow handler is isolated, timed and removed after repeated failures, as observers are, and the
  message then falls through to the engine.
- Narrower alternative if a general handler is too much: a `DailyRewardsRequested` event raised by the engine on that
  exact action. It is simpler but specific to one client button.

### D. Smaller pieces

- `SessionInfo.AccountName` already exists (1.5); the module uses it as the account, never anything the web page sends.
- `PlayerInfo` already has `Level` and `ProfessionId` for the selector.
- Feedback lines go through `player.message`.

## The DailyLogin module

A new project, `AORebirth/Modules/DailyLogin`, built against the Abstractions only.

- **Moves over unchanged:** `DailyLoginAccountState`, `DailyLoginRewardCatalog`, `DailyLoginRewardSelector`,
  `DailyLoginClaimResult`, `DailyLoginClaimFiles`. They hold the rules and the file format; none of them needs engine
  types beyond item ids.
- **Rewritten on the API:** the bridge and the service.
  - `Initialize` registers a message handler for CharacterAction 263 with Target = self.
  - On a claim: read pending request files for `SessionInfo.AccountName` and the character, load the board from module
    storage (scope `character:<id>`, key `board`), choose the reward, then call `player.grant-items` with the claim
    token as `key` and the new board as the `state` write.
  - Replay, already-claimed-today, the 28-day reset and every player-facing message stay as they are.
- **Config:** the claims roots and catalog paths move from environment variables to the module's config
  (`GetConfig`), with the current values as defaults. `rewards.json` and `day10-profession-nanos.json` ship in the
  module folder instead of the engine's `Content/Daily`.
- **Capabilities:** `handle-messages`, `module-storage`, `read-sessions`, `read-world`, `player.grant-items`,
  `player.message`.

## Moving existing data

`dailyloginclaims` already holds real progress. A one-off, idempotent data migration copies each account and character
row into `modulestate` (scope `character:<id>`, key `board`; claim tokens under key `claim:<token>`), keeping ownership
and the per-day uniqueness. It never deletes `dailyloginclaims`; that table is dropped in a later release, after the
module has run in production. A server can roll back by unloading the module and restoring the engine version before
the cut-over, since the old table is still intact.

## Order of work

1. **API 1.8, engine side:** module storage (A) with its migration, `player.grant-items` (B), message handlers (C).
   Tests for idempotent grants, compare-and-set and handler fall-through.
2. **Module:** build `DailyLogin` against 1.8; run it beside the engine copy in a test setup where the engine path is
   turned off by config, and compare results claim by claim (same tokens, same items, same messages).
3. **Data migration** into `modulestate`, then switch: Modules.json enables the module and the engine stops handling
   action 263.
4. **Remove** `Core/DailyLogin`, its DI wiring, the `IDailyLoginClaimDao` methods and the `CharacterActionMessageHandler`
   case. Drop `dailyloginclaims` one release later.

## Risks

- **Atomicity.** If `grant-items` with a `state` write is not truly one transaction, a crash can pay twice or lose a
  claim. This is the part that needs the most review.
- **Ordering with the web page.** The page writes a request, then the client sends 263. That stays the same; only who
  reads the request changes.
- **Two zones.** The board is per character and a character is on one zone at a time; compare-and-set covers the gap
  during a handoff.
- **A general message handler is a large new power.** It lets a module swallow any inbound message; it needs its own
  capability, logging and review. The narrower event in C avoids that if preferred.
