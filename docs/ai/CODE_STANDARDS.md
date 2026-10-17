# Code Standards

Generated: 2026-06-02

## Naming Conventions

- Preserve existing AO Rebirth naming where possible.
- Use clear packet field names, but document when historical names are misleading.
- Do not rename broad public APIs during focused gameplay repairs unless necessary.
- When packet fields are unknown, use stable names such as `Unknown1` only with comments or docs explaining source evidence.

## Folder Structure Rules

- Server runtime code belongs under the matching engine directory.
- Zone gameplay handlers belong under `AORebirth/Server/ZoneEngine_New/Core/MessageHandlers`.
- Core entity/inventory/item logic belongs under `AORebirth/Libraries/Source/AORebirth.Core`.
- Packet models belong under AOtomation unless a custom ZoneEngine_New packet implementation is intentionally used.
- Temporary capture/test tooling belongs under `tools-temp`.
- Active AI workflow documentation belongs under `docs/ai`.
- Active project documentation belongs under `docs/project`.
- Backlog documentation belongs under `docs/backlog`.
- Authored reports and large evidence references belong under `docs/reference` or `docs/reports`.
- Historical AI notes and dated handoffs belong under `docs/archive`.
- Generated enum/stat documentation remains under `AORebirth/Documentation`.

## Class Design Rules

- Keep changes scoped to the system being repaired.
- Prefer existing AO Rebirth patterns over new abstractions.
- Add abstractions only when they reduce real complexity or protect a verified contract.
- Avoid adding broad gameplay heuristics in packet-sensitive paths.
- Follow the root `AGENTS.md` existing-mechanism-first order before introducing
  abstractions, configuration systems, build mechanisms, or compatibility
  layers.
- A failing test or generated guard is diagnostic evidence; it is not by itself
  authority to change runtime behavior.

## Control-Flow Brace Style

- Omit braces when an `if` body contains exactly one bare `return`, `continue`,
  `break`, or `throw` statement.
- Keep braces when the body has more than one statement or its single statement
  is not a bare control-flow exit.

## Data-Driven Content Rules

- Keep NPC-specific definitions, stats, appearances, IDs, dialogue, quests,
  rewards, vendor stock, spawn bindings, and similar gameplay content in the
  appropriate editable data source.
- Runtime C# should contain reusable mechanics, services, loaders, and
  validation rather than individual content definitions.
- Do not move private GameData into tracked public paths.
- For high-risk systems, separate evidence/model/test work from runtime behavior changes.

## Stat Rules

- Every stat is a named member of the `CharacterStat` enum
  (`AORebirth/Libraries/Source/AOtomation/AOtomation.Messaging/src/SmokeLounge.AOtomation.Messaging/GameData/CharacterStat.cs`).
- Do not define stat constants or aliases elsewhere and do not cast literal
  numbers to stats: no `const CharacterStat X = (CharacterStat)596;`, no
  `const CharacterStat X = CharacterStat.Y;`, and no `(CharacterStat)643` at a
  call site. A stat id that arrives as data (a content file, a spell argument,
  a packet field) may be converted where it is read.
- When an enum name does not describe what a stat means on an item or nano (on
  nano templates Level is the NCU cost and MetaType the strain), use the enum
  name and document the item meaning on the enum member instead of adding an
  alias.
- When a stat is missing from the enum, add it there with its id and a
  `<summary>` saying what it holds and where that is known from (AO's name for
  the id, the item or capture it was seen in, server-only use). Name it for what
  it means; when that differs from AO's name, record AO's name in the summary.
- Server-only stats (never sent to the client) also go on the
  `StatCollection.IsServerOnly` list. Set them without the wire flag
  (`dirty: true`); saving follows the base change, not that flag.

## Feedback Text Id Rules

- Client feedback text ids (category 110 FormatFeedback / Feedback message ids)
  have one home: `ClientFeedback`
  (`AORebirth/Server/ZoneEngine_New/Core/Helpers/ClientFeedback.cs`).
- Do not declare feedback id constants in other classes or pass raw feedback ids
  at call sites. Add a named constant to `ClientFeedback` with a `<summary>`
  holding the client text and, where known, the capture it came from, and use
  that.

## File Naming Rules

- Use existing C# naming style.
- Keep new markdown files uppercase when part of the root AI docs system.
- Use descriptive test/tool names that identify the target system.

## Comment Standards

- Add comments only where they preserve evidence or prevent future misuse.
- Packet comments should state the source: official live, private-server, AO stripdown, AOSharp, or local playtest.
- Do not add generic comments that restate obvious code.

## Formatting Standards

- Preserve the repo's existing formatting style.
- Do not churn unrelated formatting in active source files.
- Prefer ASCII in new files unless a file already uses non-ASCII or the content requires it.

## Dependency Rules

- Do not add third-party packages for small repairs without a clear need.
- Prefer existing libraries and tools.
- Treat external references such as AOSharp, AODB, and AO stripdown as evidence unless explicitly integrated.
- Do not copy large external code blocks blindly.

## Error Handling Rules

- Prefer explicit failure messages for smoke/test tooling.
- Runtime packet paths should fail safely and log targeted diagnostics when practical.
- Do not hide a bad packet value by clamping unless the source is proven and documented.

## Performance Expectations

- Avoid high-frequency logging in movement/combat ticks unless it is temporary and targeted.
- Do not spam movement updates without capture-backed need.
- Be mindful that Mike may run heavy compile jobs in parallel.

## Security And Data Safety

- Use only `cellao_codex_clean`.
- Do not expose or duplicate local passwords in new docs.
- Do not wipe or mass-edit database rows without explicit approval.
- Do not run client-side live tests unless Mike explicitly asks.

## Git And Worktree Rules

- Check `git status --short --branch` before editing.
- Do not revert user or previous-agent changes unless Mike explicitly asks.
- Keep commits split by system.
- Do not mix unverified NPC movement with stable equipment/trade/corpse fixes.
