# Current Task

Correct ClientGetItem decoding on `codex/all-mission-objectives`, based on
installed `fa5dae8aa`. Mike still cannot collect a new Find Item objective.

OBSERVED: Shade's D2086798944 has four pickup callbacks, no collection or DAO
error, and an ordinary Use denial for exact target 51005:2086798945.
PROVEN from retained original request bytes: pickup contains N3 type, player
Identity, one Unknown byte, then item Identity1. The custom codec previously
read the player into Identity1 and never consumed the item. The earlier handler
repair removed a first rejection but left target lookup broken. Its description
of the wire payload was incorrect and is superseded by the raw-byte analysis.

Correct only this codec's read/write field order, restore the actor/session
identity check and log pickup target/result. Preserve owner/binding, dungeon,
range, inventory and transactional checks. Keep the installed 150-percent item
size policy unchanged. No DAO/schema/reward/content changes or test suites.

Compile with the approved wrapper, install with local overlays preserved, and
restart the core engines for Mike's ongoing client testing. Original-client
pickup/completion/reward acceptance remains UNVERIFIED until Mike retries.

Approved Windows core compilation and git diff --check passed. Offline byte
inspection confirms both original pickup requests are 37 bytes with distinct
actor and item fields. The raw wire fixture is unchanged; no suites run.

Implementation and validation: `docs/reports/ALL_FIVE_MISSION_OBJECTIVES.md`.
