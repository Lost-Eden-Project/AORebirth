# Org city plots: client data and protocol reference

Reverse-engineered 2026-11-24 from the retail client (`city.dll`, `Gamecode.dll`,
`GUI.dll`, `N3.dll`; Ghidra project "AO", image base 0x10000000 for every DLL) and
the client resource database (aodb), cross-checked against the 2026-06 private-city
captures summarised in `docs/generated/*city*.md`. Nothing here is implemented in
`ZoneEngine_New` yet; this is the specification the implementation must follow.

**Corrections from the client team (team request 26, 2026-11-24; LostEden.Protocol 0.5.0
`CityMessages.cs`), which supersede the inferences below:** signal 5's guiType 1 is the
buy window (purchase enabled only at leaseMode 3) and guiType 2 the terminal (leaseMode 1
read-only; any other leaseMode the full tabs); signal 5's third int disables the build
controls at leaseMode 3 when below 2; signals 8 and 9 carry `int tileX, int tileZ`;
PlayfieldAllCities is a `ushort` length, then `uint count` and the houses; CityId's int is
the PlayfieldAnarchyF `PlayfieldId1` instance and its playfield identity is
`(Unknown3, SG)` (0:0 on an ordinary playfield); an item whose Side is 1234567890 is any
side's; CityAdvantages' fourth int is Active (non-zero) or Disabled. Two districts may split
one zone square (595, 4894, 6102), each with its own buildable tiles.

Markers: **P** = proven from decompiled code and/or a byte-exact record parse.
**I** = inferred (consistent with code and captures, not directly observed).

Addresses: `CY` = city.dll, `GC` = Gamecode.dll, `GUI` = GUI.dll, `N3` = N3.dll.

---

## 1. Static plot data (RDB)

### 1.1 Record type 1000039 (0xF4267): city controllers per playfield (P)

One record per playfield, record id = playfield id. Class `RDBPlayfieldCityData_t`;
factory registered at GC 1015208a, blob reader GC 1013059c / CY 1000cf57. All 20
records (6, 106, 500, 550, 585, 595, 620, 625, 647, 687, 716, 717, 4894, 4898, 4899,
5001, 5002, 6010, 6101, 6102) parse exactly to the last byte with this layout.
All integers little-endian.

```
int recType (1000039), int recInst (= playfield id), int unk (= 1)
int version (= 1)
int numControllers
int tileExtentX, int tileExtentZ            // playfield size in 4 m tiles
repeat numControllers:
  int  dynelType (50200 = 0xC418 CityController)
  int  dynelInst = 0xC0000000 | (controllerIndex << 16) | playfieldId
  int16 houseMinLevel, int16 houseMaxLevel  // 1, 1 in every record
  int32 areaPurchaseCost                    // credits
  int32 areaUpkeepCost                      // credits per billing period
  int (n+1)*1009 ; n x { uint32 id; float x, y, z }        // landing points
  int (n+1)*1009 ; n x { uint16 zoneId; uint8 type (always 0); 13 bytes mask }
```

Vector counts use the AO `(n+1)*1009` encoding; the reader rejects values not
divisible by 1009 or with n >= 30001 (CY 1000d52b, 1000d7d1).

A **controller is one plot ("district")**. Purchase and upkeep text:
"Buying this district will cost you %d credits. The upkeep is %d credits per month"
(client text category 10002 / 220109620).

### 1.2 Zones and the buildable-tile mask (P)

- Tile = 4.0 world units; grid origin is playfield (0,0); `tile = floor(pos / 4)`
  on X and Z (CY 100011a7 GetTilePos).
- A zone is a fixed 10x10-tile (40 m x 40 m) `HouseLayout_c` with tileSize 4.0
  (CY 1000d7d1, 1000cb37).
- `zonesPerRow = tileExtentX / 10`;
  `offsetTileX = (zoneId % zonesPerRow) * 10`,
  `offsetTileZ = (zoneId / zonesPerRow) * 10` (CY 1000cf57, 10013521).
- The 13-byte mask holds 100 bits, LSB-first, row-major by Z:
  bit index `= localZ * 10 + localX`. Set bit = building allowed on that tile.
- World position of a layout tile `(i, j)` = `((offsetTileX + i) * 4, (offsetTileZ + j) * 4)`
  (CY 10013590 ContainsPosXZ, 100136a9 GetTileAtPosXZ).

### 1.3 Plots per playfield (from 1000039)

Plot size is the count of buildable 4 m cells; there is no small/medium/large field.

| PF | Name | Plots | Zones/plot | Cells/plot | Purchase price |
|---|---|---|---|---|---|
| 550 | Athen Shire | 5 | 18-35 | 973-1755 | 78.4M-214.1M |
| 585 | Aegean | 10 | 21-41 | 1034-2363 | 86.7M-364.5M |
| 595 | Deep Artery Valley | 4 | 33-56 | 1618-3351 | 185.7M-692.3M |
| 620 | Eastern Fouls Plains | 10 | 28-47 | 1326-2549 | 131.6M-418.3M |
| 625 | Milky Way | 10 | 23-44 | 1038-2280 | 87.2M-341.6M |
| 647 | Greater Tir County | 10 | 20-45 | 1082-2644 | 93.4M-447.2M |
| 687 | Galway Shire | 4 | 23-27 | 1131-1191 | 100.6M-109.7M |
| 716 | Omni Forest | 2 | 33-45 | 1686-2149 | 199.6M-307.1M |
| 717 | Greater Omni Forest | 8 | 21-29 | 1122-1581 | 99.3M-178.3M |
| 4894 | Coast of Harmony | 29 (3 placeholders: 0 zones, price 10000) | 0-52 | <= 2703 | 32.9M-465.6M |
| 6101 | Three Craters West | 48 | 26-45 | 1565-2639 | 175M-446M |
| 6102 | Three Craters East | 48 | 28-42 | ~2200 | similar |
| 5001 | Playa del Desierto (instanced) | 1 | 23 | 495 | 300M / upkeep 5M |
| 5002 | Montroyal (instanced) | 1 | 64 | 2371 | 500M / 15M |
| 6010 | Serenity Islands (instanced) | 1 | 74 | 2569 | 500M / 15M |
| 6, 106, 500, 4898, 4899 | house/town templates, Parnassos, Broken Shores demo | - | - | - | placeholder values |

Upkeep is ~0.245 x price on Rubi-Ka plots, ~0.83 x price on 4894.
Controller dynel positions come from RDB 1000026 (PlayfieldDynels) as dynels of
item template 249767 "City Controller"; the dynel position differs from the
landing point(s). Open issue: 647 lists 10 controllers but no 0xC418 dynels were
found in its 1000026 record.

### 1.4 Record type 1000040 (0xF4268): house templates (P for the parts listed)

Class `RDBHouseTemplate_t`, registered GC 1015204e, reader CY 1000f1a7 (format
versions 1-4). About 87 records: ids `(templatePF << 16) | n`, e.g. 0x6A0001.
Version 4:

```
int ver
Identity id
int insideTemplateInst
int numFixtures
int16-len string name
int unk94
float stealthFactor
int upkeep
int houseType
HouseLayout { int bitCount, int tilesX, int tilesZ, ceil(bits/8) bytes footprint }
vector<HouseVisualData { int meshId, float x, y, z, int rotDeg }>
vector<DoorData { uint, Vector3 pos, int rot }>
GuardData, Advantages, KD-tree blob      // not decoded
```

`HouseType_e` (CY dfb9-e009): 1 HQ, 2 Market, 3 Grid, 4 Guard House, 5 Radar
Station, 6 Cloaking Device, 7 Mini Cloaking Device, 8 Tax Office, 9 Unique House;
0 "Normal House" (I).

Rotation is 0/90/180/270 degrees; 90 and 270 swap tilesX/tilesZ (CY 10013bc9,
10013d95).

### 1.5 Building items -> templates (P)

Item stat **620 `housetemplate`** holds the 1000040 record id. Item stat 33 `Side`
0/1/2 = Neutral/Clan/Omni. Observed: Clan template id = Neutral id + 0x3C,
Omni = Neutral id + 0x64. All buildings are DynelType 51005, mesh 9013.

| Item (Neutral AOID) | Price | housetemplate (Neutral) | Footprint (tiles) |
|---|---|---|---|
| Large Org HQ 254195/6 (Omni 254197, Clan 254199) | 60M | 0x6A0001 (Clan 0x6A003D, Omni 0x6A0065) | 20x20 |
| Small Org HQ 254207/8 (Omni 254209, Clan 254211) | 45M | 0x6A0009 | 15x15 |
| Grid House 254135/6 (Omni 254137, Clan 254139) | 15M | 0x6A0002 (Clan 0x6A003E, Omni 0x6A0066) | 5x5 |
| Notum Silo 254141 | 45M | 0x6A0003 | 15x15 |
| Landing Pad 254147 | 30M | 0x6A0004 | 10x10 |
| Swimming Pool 254159 | 45M | 0x6A0006 | 20x15 |
| Guard House 254171 | 45M | 0x6A000A | 5x5 |
| Mining Operations 254177 | 30M | 0x6A000B | 10x10 |
| Sidewalk Cafe 254183 | 10M | 0x6A000C | 10x15 |
| Sky Bar 254189 | 10M | 0x6A000D | 15x10 |
| Satellite Uplink 254447 (Clan 254893, Omni 254895) | 15M | 0x6A000E | 5x5 |
| City Park 296239 (Clan 296241, Omni 296243) | 10M | 0x6A00B9 | 10x10 |
| Private City Guest Key Generator 281588 / 297211 / 297212 | 600k | 0x6A0022 (no 1000040 record) | - |

Related items: City Controller 249767 (DynelType 50200); City Access Card 280642;
Controller Recompiler Units 254326-254369 (stat 642 `cityterminalrechargepercent`);
City Terminal Recharger 254278; Floorplans 255490-255498, 254801; Broken City
Building 254153-254206, 255499-255510; vendors 254309/254376/254379 and
295890-295893; Shop for Rent 254117; City Voucher 296318; City Resettlement Beacon
286457/8; Organization City Door 281586 (OnEnter `InstancedPlayerCity` 51102:6010).

---

## 2. Client-side placement rules (P)

`CityAI_c::CanBuildLayout(template, tilePos)` (CY 10002882) accepts a placement
when, for every tile of the (rotated) template layout, some zone of the controller
satisfies `LayoutTileAllowedAtGlobalTpos` (CY 100139ea):

1. the global tile lies inside that zone's 10x10 square, and
2. if the layout tile is occupied, the zone's mask bit at that tile is set.

`HouseLayout::Intersects` (CY 100138fc) rejects overlap with an existing house
(occupied tile on occupied tile). GUI.dll runs these checks for the build preview.

city.dll contains **no** level, cost, org, side or one-HQ checks. The server must
enforce them (the GUI also checks item Side vs player, text
"CityTerminal_HouseCanOnlyBeBuiltBy").

---

## 3. Protocol

### 3.1 N3 message ids (P)

`n3InfoItemRemote_t::MapToKey` (N3 10009826) hashes the IIR class name:
`key ^= (uint8)name[i] << ((i & 3) * 8)`.

| IIR | N3 type |
|---|---|
| ClientRequestBuyIIR_c | 0x55796602 |
| ClientRequestBuildIIR_c | 0x53011416 |
| ClientRequestDemolishIIR_c | 0x3F1B6F70 |
| ClientRequestCloseGUIIIR_c | 0x1B3C614D (repo: `CityControllerWindowClose`) |
| ClientRqToggleCloakingIIR_c | 0x3F5E4B46 |
| AOTransportSignalIIR_c | 0x62741E15 (repo: `AoTransportSignal`) |
| PlayfieldAllCitiesIIR_t | 0x59210126 |
| CityAdvantagesIIR_t | 0x365E555B |

Only these five client city requests exist (GC strings 10172114-1017221c). There is
no pay-upkeep / lease / charge request; upkeep is server-driven.

### 3.2 Client -> server requests (P)

Body after the normal N3 header (`Identity` = local character, `byte`). Ints are
int32 big-endian; Identity = (type, instance).

| Request | Fields | Serializer |
|---|---|---|
| Buy | `int ctrlKey` | GC 1012ff50 |
| CloseGUI | `int ctrlKey` | GC 10130011 |
| ToggleCloaking | `int ctrlKey` | GC 101301ca |
| Demolish | `int ctrlKey`, `TilePos {int x, int z}` | GC 101300e9 |
| Build | `int ctrlKey`, `Identity template`, `TilePos {x, z}`, `int rotationDeg`, `Identity planItem` | GC 1012fe29 |

- `ctrlKey = 0xC000 | controllerIndex` (second dword of `CityID_t`, see 3.4). It is
  the **only** controller identification in any request; the dynel identity is never
  sent. Captured 0xC000 = plot 0. Buy/Close wire length 33 = 20 framing + 8 + 1 + 4.
- `template` = `Identity(0xF4268 = 1000040, item stat 620)` built by the GUI
  (GUI 100e90fc). Not an item-template identity.
- `planItem` = the inventory item (building plan) dragged onto the city map
  (GUI 100e56a8 -> 100e4d71 -> 100e9099). Existing houses carry planItem 0:0 and
  are not sent. The GUI sends one Build per placed plan, HQ first (GUI 100e498a).
  **Server must consume the plan item and verify its stat 620 == template id.**

`TilePos` streams x then z (CY 1001b7a3 / 1001b785).

### 3.3 Server -> client: AOTransportSignal (P)

Envelope (GC 1012f854 write / 1012f881 read): N3 header `Identity` (target
character), `byte`, `int32 signal`, then **raw payload bytes with no length prefix**.
Dispatch GC 1012fb80 feeds the payload to every receiver registered for the signal.
Slot wiring GC 1012bbf3.

| Signal | Payload | Client slot / meaning | Captured values |
|---|---|---|---|
| 2 | `BaseClientHouseData_t` | HouseAdded (playfield) | - |
| 5 | `CityID_t` (20 B), `Identity char` (8 B), `int guiType`, `int leaseMode`, `int unused`, `int16 len + ASCII ownerOrgName` | SlotOpenGUISession (CY 1000a35d): accepted only if CityID matches the client's own (CY 1000a59f); stores guiType (+0x34), leaseMode (+0x38), org name (+0x3c); opens window. Third int ignored. Serializer GC 1012dbc6; string GC 10038a59. | guiType 2 in all captures. leaseMode **1 = another org's city, 2 = unowned ("no organization"), 3 = own org** (I). Text "Est. 2024", "Identifies As Clan". |
| 6 | `int leaseMode` | SlotUpgradeLease (CY 1000a3b6) | expected after a successful Buy (3) (I) |
| 7 | `CityID_t` | SlotCloseGUISession (CY 1000a3db): closes window, leaseMode 0 | the 53-byte signal after OUT 0x1B3C614D |
| 8 | `TilePos` | DemolishStarted | - |
| 9 | `TilePos` | HouseRemoved | - |
| 10 | `int credits` | SlotUpdateCityUpkeepCost | 60,000,000 (unowned 120000 plot); 15,000,000 (Serenity) |
| 13 | `int seconds` | SlotUpdateCityUpkeepSecondsTillBill: client stores `now + seconds` | 2,503,359 s (~29 d); 2,590,725 s (~30 d) |
| 14 | `int cloakState`, `int secondsUntilToggleAllowed` | SlotCloakingDeviceState (CY 1000a46c): toggle possible when state != 0 and seconds < 0 | state 1 / 0; seconds = `0 - now` (huge negative) |
| 15 | `float charge` | SlotControllerCharge | 1.0f |
| 16 | `int16 len + ASCII` | SlotCityOrgName | "Est. 2024" post-Buy (I) |

`BaseClientHouseData_t` (CY 1000dec3): `float x, y, z`, `int rotationDeg`,
`byte demolishing`, `Identity template (1000040, id)`.

### 3.4 CityID_t (P)

Wire order (CY 100114a9): `Identity(0xC419, 0xC000 | controllerIndex)`,
`int proxyInstance`, `Identity(0xC79E, playfieldModelId)`.

Built client-side in GC LoadPlayfieldCityData (101224a0): type from CityInterface
vfunc +0x94 (0xC419 in every capture, I), index = `(rdbDynelInst >> 16) & 0xFFFF`,
proxyInstance = PlayfieldProxy+0xc (captured 1370122 = org id of the private city
instance, I), model = `n3Playfield_t::GetModelID` (e.g. 0xC79E:0x177A Serenity,
0x138A Montroyal). The server must echo exactly these values in signals 5 and 7.

### 3.5 PlayfieldAllCities (P)

Handler GC 101302ac -> `PlayfieldCityHolderClient_c::UpdateNewHouses`:
`uint count`, then `count x BaseClientHouseData_t`. Sent in the playfield ready block
(`SpawnService.SendRetailWorldEntryCompletion` currently always sends it empty). The
repo's `PlayfieldAllCitiesMessage` models the body as an opaque Int16-prefixed blob
and needs reworking.

### 3.6 Controller identity on the live server (P)

The client finds a GUI session's plot by `CityID_t`, never by dynel instance or
proximity (CY 10009f57 -> GetCityHolderForPF -> GetCity). However it also resolves
the controller dynel `CityAI+0x28 = GetRemappedDynelID(0xC418:0xC0000000|(idx<<16)|pf)`
(GC 101224a0) and dereferences `GetDynel(...)->GetGlobalPos()` with **no null check**
(GC 1012e392). `GetRemappedDynelID` (GC 10121fae) remaps only in template-generated
(instanced) playfields using the remap list sent with the playfield; elsewhere the
RDB id is used as-is.

**Rule: spawn city controllers with their RDB instance id
`0xC0000000 | (idx << 16) | pf`.** (Live used 0x9C182E in an instanced private city,
which only works because the PlayfieldAnarchyF init carried the remap entry.)

---

## 4. Observed flows

### 4.1 Opening the controller (captures 20260623-081344, -021643)

Client: `GenericCmd Use` on the CityController dynel.
Server: signals 5, 10, 13, 14, 15 to the character, then the GenericCmd ack, then
Feedback 110 / 8208531 = **"You have full access to this city terminal."** (normal,
not an error). Legacy implementation: `git show ecefa673^:AORebirth/Server/ZoneEngine/Core/MessageHandlers/CityControllerInteractionHandler.cs`.

### 4.2 Closing

Client: `0x1B3C614D {ctrlKey}`. Server: signal 7 with the CityID.

### 4.3 Purchase (capture 20260623-015602)

Client: Buy `0x55796602 {ctrlKey}` (~3.4 s after open).
Server (live, 6 signals + `Stat Cash`; exact order not hex-decoded): consistent
reply is `Stat Cash`, signal 6 (leaseMode 3), 16 (org name), then 10/13/14/15 (I).
No CityAdvantages, PlayfieldAllCities or OrgClient traffic appeared. Client ownership
state is purely leaseMode + org-name string.

### 4.4 Build / demolish

Client: Build `0x53011416` per placed plan (HQ first); Demolish `0x3F1B6F70 {ctrlKey, TilePos}`.
Server: signal 2 (house added) / 8 (demolish started) / 9 (house removed) to
playfield clients; full list via PlayfieldAllCities on entry. Not captured live.

---

## 5. Server responsibilities (not enforced by the client)

Org membership and one city per org; credits for purchase, buildings and upkeep;
the 24-hour HQ rule and HQ-before-other-buildings (client text category 10002);
house level/type limits (`houseMinLevel`/`houseMaxLevel`, template `houseType`);
plan-item consumption and stat-620 validation; building Side vs org side; upkeep
billing and `leaseMode` transitions; controller charge; cloaking toggle timing;
persistence of plots, houses and guest keys (no tables exist yet; the current
`org_organizations` table has no CityId column).

---

## 6. Open questions

1. Exact order/ids of the six post-Buy signals (needs the raw purchase capture,
   not on this machine).
2. LeaseMode enum names beyond 1/2/3; other TerminalGUIType values.
3. Signal-5 third int (-1 / 1): client ignores it.
4. 1000040 tail: GuardData, Advantages, KD-tree; field `unk94`.
5. ZoneData `type` byte (always 0); landing point usage; guest key generator
   template 0x6A0022 has no 1000040 record.
6. Greater Tir County (647): 10 controllers in 1000039, no 0xC418 dynels found in 1000026.

## 7. Repo state (2026-11-24)

- `ZoneEngine_New` has no city code. Stat enums exist (`HouseTemplate=620`,
  `CityInstance=640`, `CityTerminalRechargePercent=642`); `IdentityType.CityController=0xC418`.
- Legacy handlers (controller window, guest key, private-city entry, CityAdvantages)
  were deleted in `ecefa673`; read with `git show ecefa673^:<path>`.
- Surviving rule fixtures: `Tests/Fixtures/Gameplay/MessageHandlers/CityControllerInteractionRules.cs`,
  `GuestKeyGeneratorInteractionRules.cs`, `GenericCmdUseRouteClassifier.cs`.
- No 1000039/1000040 data has been extracted into GameData yet.
