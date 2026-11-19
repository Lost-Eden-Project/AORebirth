# Player changelog

Player-facing change notes, one small file per change, so a deployment can list exactly what players get.

## Adding an entry

Any change players will notice adds one file under `changelog/entries/`, **in the same commit as the change**:

```
changelog/entries/YYYY-MM-DD-short-slug.md
```

Each line in the file is one entry, written for players:

```
- Added: Need or Greed team loot. Team leaders turn it on with /team loot needgreed.
- Fixed: Rooms with two doors to the same room no longer stay blocked when one of them is locked.
```

Rules:

- Start every line with `- Added:`, `- Changed:`, `- Fixed:` or `- Removed:`.
- Describe what a player sees or does: commands, items, NPCs, missions, zones, UI, balance numbers they would notice.
- Never mention code, classes, files, packets, message ids, database tables, migrations, engines, servers, builds,
  refactors, logging or tooling. A change with no player-visible effect gets no entry.
- Never mention private infrastructure, hosts, credentials or unreleased private content details.
- Mark a change that needs the updated Lost Eden client with `(needs the latest Lost Eden client)`.
- Keep each line to about one sentence. Several lines per file are fine; one file per commit or feature.
- If the change is reverted before it ships, delete its entry in the same revert. Once an entry has shipped, leave it
  alone: a later correction is a new entry.

## Reading entries for a deployment

A release's notes are the entry files **added** between the public-master SHA that is live and the accepted release
SHA:

```
git diff --name-only --diff-filter=A <LIVE_PUBLIC_MASTER_SHA> <ACCEPTED_PUBLIC_MASTER_SHA> -- changelog/entries/
```

Read those files at `<ACCEPTED_PUBLIC_MASTER_SHA>`, group the lines by Added / Changed / Fixed / Removed (in that
order, oldest file first), and post them as `- ` list items. Entry files are never moved or edited by a deployment;
the git range alone decides what is new. When the live SHA is unknown, ask instead of guessing a range.
