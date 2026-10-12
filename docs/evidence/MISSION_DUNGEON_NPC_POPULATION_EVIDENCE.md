# Mission dungeon NPC population evidence

Date: 2026-10-11. Scope: replace the ordinary mission population's alien-only
placeholder with references to existing nonalien NPC families. This is not a
complete reconstruction of retail population selection, objectives, or loot.

## Corpus and original-packet validation

The complete input path set and all 23 independent mission instances are listed
in [the foundation capture inventory](PROCEDURAL_DUNGEON_FOUNDATION_EVIDENCE.md#complete-independent-layout-inventory).
This audit inspected `mission-dungeon.json` in all 22 corresponding analysis
folders, the existing population reviews, and the original `raw-packets.csv`
files. The two `*-level-analysis` duplicates were excluded. Reentries were
deduplicated by dynamic playfield, generator instance, and NPC identity.

PROVEN by this audit:

- 23 independent dynamic-playfield/generator pairs.
- 268 unique observed NPC identities across those instances.
- All 268 first-observation SHA256 values matched the corresponding original
  packet bytes, selected by `GlobalOrdinal`; zero mismatches.
- The NPC records preserve name, level, MonsterData, NPCFamily, identity,
  position, and original packet reference. Repeated full updates were not
  counted as new NPCs.

These are observed lower bounds. Receiving a complete room layout does not
establish complete NPC visibility; the QL220 visit did not reach every floor.
The earlier approved analyzer availability limitation remains as recorded in
the foundation report. This was an independent original-packet hash audit of
existing analyses, not a new analyzer pass or a new retail capture.

## Existing catalog mappings

OBSERVED: each example below has an existing `NpcTemplates.json` leaf matching
its captured name, MonsterData and NPCFamily. The family references are already
present in that same catalog's `Children` relationships. No duplicate NPC
catalog or copied template statistics are required.

Capture stems resolve through the linked complete inventory. All example
identities are type 50000; the table shows their instance component.

| Existing family / matched leaf | Captured name | MonsterData / NPCFamily | Capture stem / packet ordinal | NPC instance |
| --- | --- | --- | --- | ---: |
| `ANDR` / `AD01` | 34-I Helper | 17649 / 4 | 20261010-221548 / 301 | 2060659136 |
| `ROLL` / `RR02` | Contagious Rollerrat | 17687 / 55 | 20261011-025629 / 3519 | 2060700155 |
| `CLAW` / `CL02` | Clawfinger Ruffian | 38387 / 18 | 20261011-062013 / 266 | 2060726486 |
| `TENT` / `TM01` | Limp Tentaclemutant | 17695 / 70 | 20261011-000406 / 142 | 2060651773 |
| `SHAD` / `SH01` | Shadowmutant Speck | 30464 / 60 | 20261011-000406 / 144 | 2060651780 |
| `UNBR` / `UB01` | Unfinished Breed Bully | 44143 / 73 | 20261011-004618 / 178 | 2060666589 |

Additional corroboration: the QL3 team recording at 20261011-062013, packet 252,
contains Contagious Rollerrat 2060726484 at level 2 with MonsterData 17687,
NPCFamily 55, scale 91 and health 38. The existing `RR02` level-2 template has
the same values. This corroborates that example, not every field or version of
the complete NPC catalog.

UNVERIFIED: the original retail ACGHash/terminal NPC-template key is not exposed
by these observations. The exact NPC identity is not an ACGHash. Matching a
name, MonsterData, family, or appearance does not prove the original server
selected our existing hash. Selecting these six existing family roots is
explicit AORebirth configuration policy informed by the observations.

## Level coverage and older-catalog limits

The existing NPC dataset is from an older game version. No exact retail
version equivalence or full statistical parity is established. Family-level
extents below include gaps; they are not continuous guaranteed coverage.
The requested mission QL is passed to existing family selection/interpolation
and clamping. This does not imply that every resulting NPC has that exact level.

| Family | Catalog level extent | Existing selection around requested QL25 |
| --- | --- | --- |
| `ANDR` | 1-106 | Nearest branch `AD04`, 34-V Worker, levels 30-34; clamps to 30 |
| `ROLL` | 1-47 | Nearest branch `RR08`, Plague Rollerrat, levels 21-23; clamps to 23 |
| `CLAW` | 1-85 | `CL08`, Claw-C22 Escapee, levels 22-25; supports 25 |
| `TENT` | 1-85 | `TM08`, Tac-V85 Escapee, levels 22-25; supports 25 |
| `SHAD` | 1-85 | `SH08`, Shade-Y42 Escapee, levels 22-25; supports 25 |
| `UNBR` | 1-85 | `UB08`, Breed-B37 Escapee, levels 22-25; supports 25 |

Requests beyond these extents retain existing catalog clamping. In particular,
this six-family pool does not provide a retail-equivalent level-220 population.
Do not claim exact QL25 coverage for all six families or continuous level 1-220
coverage. Expanding the configurable pool and improving older-template coverage
remain subsequent population work.

## Deliberately limited implementation policy

The six configured families are a nonexhaustive repair for the alien-only
placeholder, not the complete set of monsters observed in the 23-instance
corpus. Other observations include low-level human professions, level 38-43
human opponents in the earlier PF321 recording, and high-level Pain Lords,
Hellhounds, Bioarranged Beasts and Sewer Scuttlers in the QL220 recording.
Named NPCs, players' pets, security objects and bosses require separate role
classification rather than indiscriminate promotion into the regular pool.

No observed selection probability, family mixture, mandatory style association,
or population weight is established. Applying this configurable subset across
generated interiors is AORebirth policy. The existing 35-percent placement
chance is retained implementation behavior, not a measured retail probability.
Terminal-advertised rewards, normal loot, boss loot, and objective-specific
targets remain independent concerns outside this repair.

Runtime configuration references existing NPC hashes only. Raw captures,
research datasets, and private NPC/MonsterData catalogs are neither copied into
this report nor loaded by the dungeon runtime as a new dependency.
