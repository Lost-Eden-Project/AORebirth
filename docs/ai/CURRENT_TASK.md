# Current Task

Integrate the client-accepted friendly-item recipient and visual repair into
Windows/public master, then reconcile that authoritative source into the
private Linux repository through its normal build/package/push workflow.
Production deployment has not been requested.

OBSERVED acceptance (2026-10-11): Mike confirms both healing and the corrected
visual work on the selected other character. Windows build and focused tests
passed before this client acceptance; the accepted runtime is 78e0b302f.

Visual follow-up (2026-10-11): Mike's two-client test confirms that the healing
reaches the selected recipient, but the stim effect appears on the user.
PROVEN by source: ExecuteUse passes the recipient to effects, then drops it
when AnnounceUsed fills TemplateAction's recipient fields with the user.
The scoped candidate passes the already accepted recipient into AnnounceUsed,
preserving performer Identity and the user's inventory Placement. It follows
the captured inventory-use performer/recipient layout. Mike has now confirmed
the corrected visual in the local two-client test.
Focused message assertions cover self, another recipient, delayed target
changes and rejection without an announcement.

PROVEN capture evidence: official inventory Use at
2026-09-23T02:59:57.5534400Z, item 297044, keeps user 50000:1248653930 in
Identity and user inventory 104:78 in Placement, while Unknown3/Unknown4
identify the friendly recipient 50000:2052536079. The request/ack and world
snapshot independently correlate the item, user and recipient. The complete
available raw-CSV corpus under the private capture root yielded 802
TemplateActions, including 56 Use actions; other distinct-recipient offensive
uses agree with this layout. No 291043 Use appeared in that scan. The captured
friendly example is an NPC recipient; Mike's live test separately accepts the
player-to-player visual. No capture data was copied into runtime.

Visual follow-up validation: PASS approved Windows core build; PASS all 37
FriendlyItemUseTests, including the added announcement assertions. No broader
suite was rerun. Local source matches the isolated candidate, local config is
unchanged, and core process/listener ownership passed after startup. Chat,
Login and ZoneEngine_New are left running under Mike's explicit testing request.
No restoration amount, effect function, skill lock, requirement or consumption
behavior changed in this visual follow-up. No client was controlled.

Base: public master `3805aa851fcbe9ba81a9147954a9f1bf26e4fa8f`.
Branch: `codex/friendly-item-targeting`.

PROVEN by source inspection: ItemUseService defaulted the recipient to the
user and only resolved hostile/fighting targets. ToUse requirements also
received only the user's context. Health/nano effect functions already
supported a separate recipient and did not require modification.

Implemented behavior:

- ApplyOnFriendly resolves the character selected by LookAt/SetTarget before
  ToUse requirements or queuing. No selection/explicit self uses self only
  with ApplyOnSelf. Invalid nonzero selections never fall back to self.
- Target stats, runtime CharState and state-dependent requirement resolvers
  read the resolved recipient. User requirements still read the user.
- Reuses the template AttackRange (normalized meters, touch range when absent
  or zero), edge distance and LOS checks used by item-backed perk actions.
  Requirements and reach remain start-only, preserving queue timing.
- The queued use keeps the same recipient object. Before effects it checks
  life, playfield, registry identity and player InPlay status. A removed,
  replaced, dead, disconnected or transferred recipient aborts without
  effects, new skill locks or consumption.
- Existing hostile/fighting-target priority remains. Eligibility restrictions
  remain template-driven, including SelectedTargetType NPC restrictions.
- Source, inventory ownership, consumption and individual function ApplyOn
  skill-lock ownership remain in the existing ExecuteUse path.

Files changed:

- `AORebirth/Server/ZoneEngine_New/Core/Inventory/Item.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Inventory/ItemUseService.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Inventory/UseTargetRules.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/FriendlyItemUseTests.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj`
- `docs/ai/CURRENT_TASK.md`
- `docs/project/PROJECT_STATE.md`

Additional source inspected: LookAtMessageHandler, GenericCmdMessageHandler,
Player (selection/perks), Character, Dynel, ItemTemplate, ItemUseFunctions,
RequirementFeedback, CanFlags, NanoCastFunctions, TestDoubles,
InventoryActionTests, ItemUseDelayTests, SkillLockTests and
RequirementExpressionTests. ItemBehavior and restoration helpers are unchanged.

Validation (2026-10-11):

- PASS: `cmd /d /c tools\build_aorebirth_debug.cmd`; existing dependency and
  compiler warnings remain.
- PASS: 37 FriendlyItemUseTests, 10 SkillLockTests, 5 RequirementExpressionTests;
  52 total, zero failures or skipped cases. Covers self/A-to-B health and nano,
  full user/injured recipient, delayed selection changes, invalid/lost targets,
  target/user requirements, explicit skill locks, exactly one/zero user charges,
  NPC restrictions and hostile/fighting-target preservation.
- PASS: source review and `git diff --check`.
- BLOCKED: the normal test-project compile reports six CS0246 errors in
  GeneratedMissionCorpseInteractionTests, GeneratedMissionMaterializationTests
  and MissionContentEditabilityTests. These unrelated tests are unchanged.
  No full-suite pass is claimed. The explicit FriendlyItemUseFocusedTests
  property follows the project's existing focused-build mechanism; the
  default test project retains its original diagnostics.

Reproduce focused coverage from the repository root:

```cmd
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:FriendlyItemUseFocusedTests=true --filter FullyQualifiedName~FriendlyItemUseTests
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:FriendlyItemUseFocusedTests=true --no-build --no-restore --filter FullyQualifiedName~SkillLockTests
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:FriendlyItemUseFocusedTests=true --no-build --no-restore --filter FullyQualifiedName~RequirementExpressionTests
```

OBSERVED: Mike confirms two-client healing reaches the selected recipient.
UNVERIFIED: nano/self/hostile gameplay acceptance and
obstructed LOS geometry. Automated cases use server fixtures, not live clients.
Actual stim data has ApplyOnFriendly without ApplyOnSelf; the current self-use
flag rule can reject that real template with no selection or explicit self.
That compatibility finding is separate from this visual-only follow-up and
has not been changed. No client control or production deployment. Windows
master integration and private Linux reconciliation are explicitly authorized.
