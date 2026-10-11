# Current Task

Fix friendly-targeted inventory item use in ZoneEngine_New.

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

UNVERIFIED: two-client health/nano/self/hostile gameplay acceptance and obstructed
LOS geometry. The read-only local status check found no running core backend or
open core listeners; no two-client session was available. Automated cases use
server fixtures, not live clients. No engines were started, no client was
controlled and no master merge or production deployment is included.
