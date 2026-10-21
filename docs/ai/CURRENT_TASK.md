# Current Task

EXPANSION ENTITLEMENTS (2026-10-21).

PROVEN: `Expansions.All` combines all eleven supported expansion/entitlement
flags (2047). Registration and console account creation use that shared policy;
new characters retain the existing account-to-expansion-stat initialization.

The operator migration updates only account expansion values and account-linked
character expansion stats in one rollback-protected transaction, then sets the
login column default. It refuses missing linked expansion stats and preserves
unlinked characters. Repeating it changes no entitlement rows.

VALIDATION: approved Windows core build PASS; 49 focused Login checks PASS;
152 isolated-MySQL migration/account-creation checks PASS; all four player breeds
pass persisted new-character initialization/rollback checks; fresh hydration,
reconnect and spawn-publication entitlement regression PASS. Unrelated account,
statistic, inventory, equipment and progression preservation is covered.

Production operations and private Linux acceptance/deployment receipts remain
private. Original AO client acceptance remains Mike's step.
