# Current Task

LOGIN CHARACTER CREATION REPAIR (2026-10-17).

Mike authorized correcting the confirmed character-creation defects on public
master 245cfaa41182f1e7c574f9d8a9fb4d6db8ebcf23, focused regression tests,
the approved Windows build, and commit/push reporting. Branch:
codex/login-character-creation. Preserve the dirty primary checkout and private
GameData; no production character records, schema or live deployment changes.

Scope: stage NewCharacter.json for Login build/publish; log and eagerly validate
Login's effective creation data; cache successful loads without caching errors;
validate creation inputs before insertion; use one existing database transaction
for all mandatory creation writes; return success only after commit; exclude
incomplete historical characters from the login list without editing them.

Tests explicitly requested: all four breeds, GameData path selection and load
failures/recovery, missing account/invalid breed, injected persistence failures,
rollback without orphaned rows, successful mandatory state, and character-list
filtering. Use isolated test persistence only. Mike performs AO-client checks.

PROVEN: the base source has the staging, transaction, missing-stat publication
and Lazy exception-caching defects. UNVERIFIED: Bitnykk's particular triggering
exception and the cause of the reported GUI.dll crash. Do not conflate these.

IMPLEMENTED AND VALIDATED: all mandatory creation writes share one caller-owned
transaction. Approved Windows core build PASS; 45 focused tests PASS; actual
four-breed creation and injected-failure rollback in disposable MySQL PASS with
zero orphan rows and no production contact. Login's staged JSON matches source.
Historical records and private GameData are unchanged. No backend engines were
started or restarted. No AO client or Linux action.

See docs/reports/LOGIN_CHARACTER_CREATION_REPAIR.md for exact files, commands,
transaction boundaries and limitations. Publish this feature branch; public
master merge and client acceptance remain separate.
