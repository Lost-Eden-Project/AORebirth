# Current Task

XP OFF WITH PERSONAL RESEARCH ACTIVE (2026-10-15).

Mike authorized repairing the ignored DisableXP client action and the earned-XP
paths while preserving personal research. Base: public master
5ce47adc4e14253884415e5c0a107e6d21ad8113. Work is isolated from the dirty local
installation on codex/xp-off-research.

OBSERVED: live logs contain unhandled DisableXP(165) requests. PROVEN: current
master lacks the handler and applies normal XP after research without a disabled-XP
check, including independent quest/DOJA projections.

PROVEN client encoding: DisableXPGain uses AutoAttackFlags (stat 349), bit 0x10.
The original GUI sets/clears that bit while preserving other bits, then sends
EventFeedback 0x3c; Gamecode sends CharacterAction 165 with the complete flag word
in Parameter2 and updates stat 349 locally. PlayerOptions 576 is not this setting.
Installed GUI.dll SHA256:
ECAA2C686DB3E0E17032AC69B14A14F030BC3185C51A10E04BEEC18BA3AC5306.
GUI callback 0x100c017f, event call 0x100c01d1. Existing Gamecode export:
tools-temp/gamecode-disasm.txt:38014-38058, 158149-158272, 442857-442870.
The retained GUI constructor independently agrees (gui-visualflags-consumers.txt,
stat 349 read and bit 4 binding). Completed captures contain Action165 Parameter2
5 and 21; the client code establishes their enabled/disabled meaning.
Reuse existing stats, persistence and reward APIs; no action-echo mechanism.

Preserve research/caps, independent quest rewards, reward-ledger transactions and
existing enabled-XP behavior. No schema or GameData changes. Compile through the
approved Windows wrapper; no automated suites or client control are requested.
Windows client acceptance is Mike's responsibility. No master merge or Linux
deployment is authorized for this candidate yet.

IMPLEMENTED, CLIENT VERIFICATION PENDING: CharacterAction 165 stores the complete
AutoAttackFlags word through the existing authoritative stats/save path. Login
publication includes stat 349. Normal kill and quest XP is suppressed after
research allocation. Quest/DOJA projections capture the preference at planning
time and preserve XP, level, watermarks and death pool. DOJA retains its existing
reward ledger with an unchanged-XP mutation, independent tokens and cooldowns.
Enabled-XP, research caps, SK and alien-XP behavior are unchanged.

VALIDATION: approved Windows core build PASS, source/encoding/persistence/reward
path review and git diff --check PASS. No automated suites or client launch.
The Codex process PATH exceeded CMD's limit; the unchanged approved wrapper ran
successfully with a short child-process PATH. No build-system files changed.
Build log: build-verify/xp-off-research/build.log (private, not committed).
Mike verifies XP-off/research-active, re-enabling XP, research cap/finish and
zoning/relog persistence on Windows before promotion. Quest/DOJA live acceptance
and broader SK/alien-XP toggle semantics remain UNVERIFIED.
