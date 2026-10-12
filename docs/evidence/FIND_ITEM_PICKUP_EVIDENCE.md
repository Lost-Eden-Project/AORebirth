# FindItem objective item and pickup evidence

Date: 2026-10-11. Scope: objective content references, accepted-journal target
projection, and the existing item-pickup wire route. Completion on successful
pickup is the requested AORebirth implementation policy; it is not established
here as the universal retail completion trigger.

## Existing data owners

OBSERVED in the current selected GameData:

- `ItemTemplates.json` already maps `URSE` to 100341 (Urgent Sensitive
  Information), `COSE` to 100231 (Contained Sensitive Information), and `RUWO`
  to 100353 (Rubi-Ka World Collectables).
- `Quests.json` contains 58 `FindItem` definitions with 27 distinct `WorldItem`
  references. All have null `SpecItem`; no definition supplies a paired
  `SpecItem`/`WorldItem`. None references `URSE`, `COSE`, or `RUWO`.
- Nineteen of those 27 authored `WorldItem` hashes are existing item leaves.
  They include machines, jars, and other authored objectives. Resolution alone
  does not establish that every such item supports random-mission pickup.

The minimal explicit policy is an objective reference to existing `URSE` in
`DungeonGeneration.json`, resolved through the existing item hash/catalog
mechanisms. No private item catalog, template statistics, or duplicate catalog
is copied into source. This policy does not claim retail selected the hash
`URSE`: the captured template is 100341, and the hash is the existing AORebirth
catalog's exact reference to that template.

## Exact observed item and pickup route

The full source path set is recorded in the
[foundation inventory](PROCEDURAL_DUNGEON_FOUNDATION_EVIDENCE.md#complete-independent-layout-inventory).
The bounded [wire fixture](FIND_ITEM_PICKUP_WIRE.json) preserves original
references, full-packet hashes, six interaction packets, and two accepted action
record slices. It is offline evidence, not runtime GameData.
Verification compared 12 original packet SHA256 values, both exact action
slices, and both complete publication windows; no mismatch was found. This
fixture verification did not run a runtime build or test suite.

| Capture stem | Objective identity | Template | Pickup request | Matching response |
| --- | --- | ---: | ---: | ---: |
| mission-20261011-001616 | 51005:1482214240 | 100231 | OUT 820 | IN 821 |
| mission-20261011-025629 | 51005:1482014915 | 100341 | OUT 3878 | IN 3889 |

PROVEN serialized observations:

- Both requests are `ClientGetItem` (`0x37136C6B`). After the 16-byte transport
  header, both contain the 4-byte message type, actor identity
  `50000:1248653930`, one zero `Unknown` byte, then the exact world identity
  in `ClientGetItemMessage.Identity1`. The actor starts at byte 20, `Unknown`
  at byte 28, and the world target at byte 29. The previous custom serializer
  omitted the actor and `Unknown`, so it decoded the actor as the world target.
  The corrected serializer reads and writes all four fields in that order.
- Both responses are `ContainerAddItem` (`0x47537A24`). The existing fields
  decode as `SourceContainer = requested world identity`, `Target = 0:0`, and
  `TargetPlacement = 0`. The message header identifies the player.
- The response retains the world item's exact identity. For 025629, later
  SimpleItemFullUpdate 4185 still uses 51005:1482014915 and parents it to the
  same player. This does not establish how retail allocates or persists a
  separate database row or inventory slot.
- In 025629, world SimpleItemFullUpdate 3524 is ownerless, with template
  100341/100341, item QL 1, and stats 0=2147483651, 23=100341, 701=1,
  702=100341, 703=100341, 412=1, 491=1. Stat 491 is the existing enum
  `QuestInstance`. Its observed value is 1, not the accepted quest identity.

The positive observation supports configuring the world objective's marker as
`QuestInstanceStat = 1`. It does not prove that all quest items use this value.
Whether the older `items.dat` template itself contains 491 was not decoded by
this audit; no replacement item statistics are inferred. Objective item QL 1
must not be confused with the mission QL or terminal-advertised reward QL.

The complete pickup-to-snapshot windows were also inspected: 001616 packets
820-1247 and 025629 packets 3878-4223 contain zero outbound
`ClientMoveItemToInventory` or `ClientContainerAddItem` requests. Later bounded
FullCharacter records place the same objective identities in main inventory
slots 82 and 81 respectively. For 025629, pre-pickup snapshot 3543 has first
free main slot 75; the advertised reward appears in slot 75 and the objective
in slot 81. Mission-key slot 69 is removed later. These observations are
consistent with automatic next-free placement. They do not establish a cursor
drag operation; the zero target in the pickup response alone must not be used
to infer one. Publishing an additional AddTemplate for that same objective is
not part of the observed exchange and must not create a duplicate client item.

## Accepted journal projection

PROVEN in accepted QuestFullUpdate 64 from 001616 and 240 from 025629:

- The 136-byte action record has `Version = 15`.
- `Action`, the first identity after Version, is the exact objective world
  identity from the table above.
- `UnknownId1` through `UnknownId6` are all 0:0. No `NpcHash` or item template
  ID appears in `UnknownId2`.
- `UnknownId7` is type 54001 with that action's instance value. The subsequent
  WorldPos still identifies the exterior mission entrance.

These field names/order come from the existing `QuestActionInfo` and
`QuestFullUpdateMessageSerializer`, checked against the original action bytes.
For this FindItem path, filling the ordinary NPC-hash field would contradict
these captures. The generated world identity must be bound before projecting
the accepted journal and retained across instance reconstruction.

## Existing runtime reuse and security boundary

`SpawnService.SpawnStaticInstance`, `WorldItem`, `HashItemMinter`, and
`HashItemCatalog` already resolve and present editable item families. WorldItem
sends owner 0:0 so runtime coordinates are serialized. Before this increment,
the active engine had the ClientGetItem codec but no registered handler.
`ClientMoveItemToInventory` handles inventory/corpse slots; it is not the
observed loose-world-item request. Generic Use is also not the observed pickup.

The server should resolve the exact target from the player's current generated
instance and accepted objective binding, validate owner/session/quest state,
range, inventory availability and duplicate state, then grant only the bound
item. Captured identity equality supports exact-target matching; those security
checks are AORebirth requirements, not deductions about unseen retail checks.
Never accept arbitrary client-supplied template IDs or complete another quest
because its item template happens to match.

## Limits and reward separation

Both recordings contain a preceding LookAt request. In 001616, reward and quest
removal precede ClientGetItem; in 025629, LookAt and ClientGetItem precede reward
and removal. Cross-direction callback chronology alone cannot establish which
request triggered retail completion. No additional capture is required for the
authorized pickup-completion policy, but it must be labeled as policy.

Other reviewed FindItem observations (010014 and 053902) reference `RUWO`
template 100353 using type 51022 and do not demonstrate the same pickup
exchange. They are not silently treated as equivalent loose-item examples.

The objective item and the terminal-advertised reward are distinct. In 025629,
reward/add packets 3883/3884 are separate from exact-object pickup response
3889. The runtime must preserve the accepted advertised reward and award it
once; picking up the objective must not select, replace, or reroll that reward.
