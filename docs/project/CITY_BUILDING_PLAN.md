# Org city plots: implementation plan

Status: IMPLEMENTED, awaiting live client test (2026-11-24). Protocol/data reference:
[docs/reference/city-protocol.md](../reference/city-protocol.md); the client team's
spec is team request 26 (rubi-ka.org /tools/team/requests/26), LostEden.Protocol 0.5.0.

What exists:
- `Tools/RDBDataExtractor` `CityDataExporter` (`--skip-cities` to skip) writes private
  `CityControllers.json` (RDB 1000039) and `HouseTemplates.json` (RDB 1000040);
  `CityRules.json` is public GameData. Schemas: `CityRules`, `CityControllers`, `HouseTemplates`.
- `Core/Cities`: `CityCatalog` (reload name `Cities`), `CityPlacement` (the client's rules),
  `MySqlCityStore` (migration `20261124_city_plots.sql`: `city_plots`, `city_houses`),
  `CityService` (window, purchase, build, demolish, cloak, upkeep, advantages).
- `Entities/CityController` replaces the Dynels.dat static for type 0xC418; requests in
  `MessageHandlers/CityMessageHandlers.cs`; `PlayfieldAllCities` carries the houses.
- Tests: `ZoneEngine_New.Tests/CityPlotTests.cs`.

Deviations from the plan below: there is no lockout table (the lockout is on the
district row); a purchase is paid from the organization's bank, not the leader's
credits, in the same transaction that gives it the district; houses are reserved
first and only then paid for with their item, so a refused request takes nothing;
one member at a time holds the full terminal
(retail 110/240791923); districts are offered only in `CityRules.json` `Playfields`.
Advantages are applied server-side only: the client recomputes bonuses from what it
knows, so it does not show them yet (open question for the client team).

Scope of this plan: plot purchase, building (build/demolish), upkeep, controller
charge/cloak state, and city advantages. **Out of scope for now:** guest keys,
house interiors (`CityHouseEnter`), guards, landing points, instanced private
island cities (5001/5002/6010).

## Design decisions (fixed)

| Topic | Decision |
|---|---|
| Billing period | 30 days from purchase, then every 30 days. |
| Non-payment | City demolished: all houses removed (signal 9 each), plot returns to unowned, org loses the city. Feedback 508/254100788. |
| Upkeep source | Organization bank (`OrganizationStore.TryAdjustBank`). "Put money in Upkeep Account" = `OrgClient BankAdd(19)` targeting the controller; treat it as a normal org-bank deposit. |
| Purchase source | Character credits (retail text: withdrawn from the character). |
| HQ rules (retail) | Must build an HQ within 24 h of purchase or the plot is lost and the org is locked out of that plot for 48 h. HQ must exist before any other house. Demolishing the HQ demolishes all houses. Plot is lost if left empty 24 h+. |
| One city per org | Enforced. Members of an org that already owns a city get the limited menu (leaseMode 1) and Feedback 508/192779843. |
| Buildings | Physical plan item required. Build consumes it; server verifies item stat 620 == template, item `Side` == org side (0 Neutral / 1 Clan / 2 Omni). |
| Demolish | 2-minute delay (signal 8 immediately, signal 9 after 120 s). Item is not returned. Advantages drop immediately. |
| Controller charge | Fixed at 1.0 for now; no decay. Field is stored so later mechanics can drain/recharge it. Advantages are gated on `charge >= threshold` (threshold configurable, default 0.0 so they are always active until drain exists). |
| Cloaking | Toggle allowed at most once per 2 hours; `0x3F5E4B46` flips state when `now >= lastToggle + 7200 s`. Signal 14 = `(state, lastToggle + 7200 - now)`. |
| Controller identity | Spawn with RDB instance `0xC0000000 \| (idx << 16) \| pf`. |

## Phase 1: data

1. **Converter** `Tools/CityRdbConverter` (seed: the two scratch parsers; format in
   reference §1). Output to private GameData:
   - `CityControllers.json`: per playfield `{ tileExtentX, tileExtentZ, controllers:[{ index, instance, position (from RDB 1000026 dynel 249767), purchaseCost, upkeepCost, zones:[{ zoneId, mask(100 bits as 13-byte hex) }] }] }`.
   - `HouseTemplates.json`: `{ id, name, houseType, upkeep, tilesX, tilesZ, footprint(hex bits), doors, meshes }` from RDB 1000040.
   - `CityAdvantages.json`: building `advantagehash1..5` (stats 651-655) -> advantage
     item metatype group (see Phase 6). Hand-authored, small.
2. **Schemas** via `Tools/GameDataSchemas` for all three files (AGENTS.md rule).
3. Loader + validation in the engine (zone ids within extents, templates referenced
   by items exist, instance ids unique). Skip playfields 6/106/500/4898/4899.
4. Mirror to `AORebirth-Scripts\GameData`.

## Phase 2: controller dynel and window

1. Spawn one `CityController` dynel (template 249767, type 0xC418) per controller
   entry when the playfield loads, at the RDB position with the RDB instance.
2. Register an `IUsableDynel` for it. On `GenericCmd Use`:
   - resolve `leaseMode`: 3 if plot owned by player's org; 1 if owned by another
     org **or** player's org owns a different city; 2 if unowned and player's org
     owns nothing. No org -> 2 with the "no organization" text (captured).
   - send signals 5 (CityID, char, guiType 2, leaseMode, 0, ownerOrgName), 10
     (upkeepCost), 13 (seconds to next bill, or 0 when unowned), 14 (cloak), 15
     (charge 1.0), then the GenericCmd ack, then Feedback 110/8208531 (full access)
     or 110/29711188 (no access, limited menu).
   - CityID = `(0xC419, 0xC000|idx)`, `proxyInstance` (0 for static playfields;
     verify with the client - open question), `(0xC79E, playfieldId)`.
3. `0x1B3C614D` CloseGUI -> signal 7 with the same CityID. Add
   `CityControllerWindowClose` message/handler on the current engine.
4. Add `AOTransportSignal` writer with raw payload (no length prefix) if the
   LostEden.Protocol `TransportSignalMessage` does not already expose it.

## Phase 3: purchase

1. Message `0x55796602` `{ ctrlKey }`. Validate: player in org, rank allows
   (leader/officer - reuse `OrganizationRules`), org owns no city, plot unowned
   and not in the org's 48 h lockout, `character.Cash >= purchaseCost`, controller
   index exists in this playfield.
2. Atomically: debit character credits; create plot ownership row
   `{ playfield, controllerIndex, orgId, purchasedAt, nextBillAt = +30 d, hqDeadline = +24 h, cloakState 0, lastCloakToggle 0, charge 1.0 }`.
3. Reply: `Stat Cash`, signal 6 (3), 16 (org name), 10, 13, 14, 15.
4. Timers: at `hqDeadline` with no HQ -> release plot, record 48 h lockout for
   that org+plot, signal 6 (2) to anyone with the window open.

## Phase 4: build and demolish

1. Message `0x53011416` `{ ctrlKey, template(1000040,id), tile{x,z}, rotationDeg, planItem }`.
   Validate: player's org owns the plot; planItem is in the player's inventory,
   stat 620 == template id, `Side` == org side; template exists; HQ rule (first
   house must be `houseType` 1; only one HQ); house level within
   `houseMinLevel..houseMaxLevel`; rotation in {0,90,180,270}; server-side
   `CanBuildLayout` (every footprint tile inside a zone of this controller, every
   occupied tile on a set mask bit) and no `Intersects` with existing houses.
2. Atomically consume the plan item (write-behind lane, exploit review: double
   Build with the same item must fail on the second), persist the house
   `{ plotId, templateId, tileX, tileZ, rotation, builtAt }`, broadcast signal 2
   (`BaseClientHouseData_t`) to the playfield, recompute upkeep (plot + sum of
   template upkeep) and send 10 to open windows, apply advantages (Phase 6).
3. Message `0x3F1B6F70` `{ ctrlKey, tile }`: find house by tile; owner-org check;
   mark `demolishAt = now + 120 s`; signal 8 to playfield; drop advantages. On
   expiry remove row, signal 9. Demolishing the HQ queues demolish for every house.
4. `PlayfieldAllCities` in the ready block: `uint count` + per house
   `{ x,y,z, rotation, demolishing, template }`. Replace the opaque payload in
   `PlayfieldAllCitiesMessage` / its LostEden.Protocol equivalent. Position =
   `(tileX*4, groundY, tileZ*4)`; verify the client's anchor (tile corner vs
   centre) against a built house in the live client.
5. Empty-plot timer: plot owned with zero houses for 24 h -> release (same as
   non-payment, without the bank message).

## Phase 5: upkeep and timers

1. Hourly tick per owned plot: 24 h before `nextBillAt` send Feedback
   508/196585349 to online org members once.
2. At `nextBillAt`: `TryAdjustBank(-upkeep)`. Success -> Feedback 508/192652356,
   `nextBillAt += 30 d`, signal 13 refresh. Failure -> demolish city
   (508/254100788), release plot.
3. Timers must survive restarts: compute from persisted timestamps on load, not
   from in-memory schedules.

## Phase 6: city advantages

Known from data (see reference and aodb):
- Building items carry `advantagehash1..5` (stats 651-655), 4-char tags
  (`GIHO` Grid House, `SMOR` Small HQ, `LAOR` Large HQ, `NMSN` Notum Silo,
  `LNPA` Landing Pad, `SIPB` Pool, `MEOG` Guard House, `MNOO` Mining Ops).
- Advantage effect items come in QL groups sharing a `metatype` (570000001 Grid
  House Computer Literacy 254384-7, 570000003 Landing Pad 254392-5, 570000005
  Small HQ Management 254400-3, 570000006 Guard House 254404-6, Notum Silo
  254388-91, Mining Ops 254396-9, Large HQ 254408-9). Each has `OnUse` with
  `ScalingModify` (53175) functions, e.g. Grid House QL300: stat 161 +54.
- Retail sent `CityAdvantages (0x365E555B)` with 4 entries `{low, high, QL, 0}`
  all at QL300 in response to `OrgClient CityAdvantages(31)`; the client only
  displays it ("show city advantages held by the organization").
- Retail text: advantages benefit **all organization members**; inactive when
  controller energy drops below a threshold; lost as soon as the house is removed.

Plan:
1. `CityAdvantages.json`: `hash -> { lowItem, highItem, ql }` (default QL300,
   low=high=QL300 item as captured).
2. `CityAdvantageService`: for each owned plot with `charge >= threshold`, the
   active set = union of advantages of its non-demolishing houses. Apply each
   advantage item's `OnUse` functions as a server-side modifier to every online
   org member (on login, zone, org join; remove on org leave, house demolish,
   city loss, charge below threshold). `ScalingModify` already exists in
   `Core/Inventory/StatModifierSpells.cs` (value = amount at level 200, scaled
   linearly by holder level); reuse it through the character Bonus path.
3. `OrgClient CityAdvantages(31)` -> `CityAdvantagesMessage` listing the active
   set. Add the missing case in `OrganizationService.Commands`.

## Persistence (needs schema approval before Phase 3)

- `city_plots (id, playfield, controller_index, org_id, purchased_at, next_bill_at, hq_deadline_at, cloak_state, last_cloak_toggle_at, charge)`; unique (playfield, controller_index); unique org_id.
- `city_houses (id, plot_id, template_id, tile_x, tile_z, rotation, built_at, demolish_at NULL)`.
- `city_plot_lockouts (org_id, playfield, controller_index, until)`.
- `org_organizations.city_plot_id` or derive from `city_plots.org_id` (prefer derive).

## Tests (ship with each phase)

`AORebirth/Server/ZoneEngine_New.Tests`: converter round-trip on a 1000039 fixture;
placement rules (inside zone, mask bit, overlap, rotation swap); purchase
rejections (no org, second city, lockout, credits) and the exact signal set;
build consumes the plan item exactly once under the lane soak
(`TradeQueuedInsertOrderTests` pattern); demolish timing and HQ cascade; upkeep
success/failure paths; advantage apply/remove on build/demolish/charge.

## Exploit review items

Duplicate Build with the same plan item (race / relog); Build on a plot the org
lost between validation and commit; purchase race between two officers of the same
org or two orgs on one plot; demolish on a house already demolishing; upkeep
deducted twice after restart; advantage stacking from re-login.

## Open questions to settle during Phase 2

1. `proxyInstance` value in `CityID_t` for a static (non-instanced) playfield
   (captured only for the private island instance = org id). Test 0 first.
2. House world-position anchor for `BaseClientHouseData_t` (tile corner vs centre)
   and the Y value to send.
3. Order of the post-Buy signals (client is order-independent; cosmetic).
