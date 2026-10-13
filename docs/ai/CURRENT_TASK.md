# Current Task

ACCEPTED TOKEN AND SIDE XP RELEASE (2026-10-12).

Mike confirms the local token display and 10% Side XP changes passed his client
testing. Integrate precisely these changes into public master, then reconcile,
build, validate and publish the derived private Linux result and deploy it live.
Reuse the existing configuration, XP and reward projection paths. Preserve all
unrelated local work and protected data. No additional gameplay or schema work.

Existing local token correction in Player.cs is preserved: publish MetaType
(Omni stat 75) in FullCharacterStats2. Shade's two saved Omni token points are
unchanged.

PROVEN: Original Character.cs sent SideXp = 0. GameplaySettings owns existing
XP configuration. Regular kills use Character.AwardXp; generated/authored quest
rewards use DirectXpRewardPlan.CreateQuestReward inside the existing transaction.
The configurable bonus is applied before existing grant caps in those two paths.
AXP, SK, GM level snaps and DOJA's special exactly-one-level reward are unchanged.

PROVEN display units: original GUI.dll (SHA256 ecaa2c686db3e0e17032ac69b14a14f030bc3185c51a10e04beec18ba3ac5306)
calls GetInfoPacketSideXpBonus at RVA 0x35072, converts the result to floating
point, divides by the double 100.0 at RVA 0x35080, then formats "%.02f%%".
Therefore 10% is wire value 1000. The existing CharacterInfoPacket field now
uses the same Gameplay.json percentage as XP awards, for players of every side.

Local base source is public master 590a7d60d788b9d9e9356af78b2906d8da98fe41
plus uncommitted local Windows corrections. Existing local configuration,
private GameData and unrelated edits are retained. PROVEN: approved Windows
build/restart and exact core process/listener ownership passed. Startup loads
sideXpBonusPercent=10 from the existing built Config/Gameplay.json. Servers
were left running for Mike. He now confirms local client acceptance. The current
request authorizes the Linux build/deployment validations and normal live
installation. It does not authorize launching or controlling the AO client.

Evidence: build-verify/side-xp-wire-unit-proof.json and
build-verify/side-xp-local-restart.log. Prior token correction is preserved.
