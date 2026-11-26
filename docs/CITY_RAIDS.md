# City raids (alien attacks on org cities)

What a live raid looked like, from one full packet capture (2026-11-25, 09:12-09:36 UTC, one org city; the zone was a
Playfield2 instance, 2159836) and the AO-Universe guide "Alien Raiding in your City" (ao-universe.com/guides/35).
Nothing here is in the client: Gamecode, City.dll and GUI.dll hold no raid logic (only the raid *team* window,
RaidViewModule_c). The client side of a raid is the Script messages below and ordinary NPCs.

## Timeline of the capture

| Time | Event |
|---|---|
| 09:12:26 | Cloak toggled (ToggleCloak). CloakingDeviceState goes from 1 (cloaked) to -1 (not cloaked), timer 3600 s before it can be toggled again. |
| 09:15:26 | Script, Kind 3, `EP_02\Alien_attack`, Duration 3600, DistrictName "City": the raid's district music. 3 min after the cloak went down. |
| 09:16:56 | Script, Kind 0, `## Alien_Event_Lander_01 Float StartX 587.491638 Float StartY 160.759583 Float StartZ 685.370850 Float LifeTime %i Unsigned StartTime %uu`, Duration 90. |
| 09:17:26 | Script, Kind 3, `EP_02\Alien_attack_drama`, Duration 45, "City". |
| 09:17:29 | Wave 1 lands. |
| 09:17:39 on | Script, Kind 4, every 15 s for the rest of the raid: `## Alien_Event_Flyby_01..05 ##` and `## Alien_Event_Circle_01..02 ##`, X 587 Z 685 (the landing point), Duration 60. |
| 09:19:38 | Script, Kind 0, `## Alien_Event_Landing_01 ...` at the same point, Duration 3600 (the ship stays landed). |
| ~every 2 min | `EP_02\Alien_attack_drama` (Kind 3, 45 s) with each wave: 09:17:26, 09:19:38, 09:21:58, 09:23:58, ... |
| 09:17-09:32 | 8 waves, about 2 minutes apart (09:17:29, 09:20:00, 09:22:02, 09:24:03, 09:26:00, 09:28:01, 09:30:01, 09:31:58). |
| 09:34:04 | The General (`General - Cha'Khaz`, level 67, 40714 health). Killed 09:34:46. |
| 09:35:41 | Loot chests (ChestFullUpdate) on the General's corpse. The capture ends 09:36 (no boarding seen). |

The Kind 0/2/4 scripts are FXS scripts the client runs (FXS_t::ExecuteScript; the `Alien_Event_*` scripts are in the
client's ResourceDatabase). The Lost Eden client plays Kind 3 (district music) and does not run the FXS kinds yet.

## Waves

Each wave lands at the landing point (within a few metres of 587, 161, 680) and walks to the City Controller
(waypoint around 586, 161, 612). Every wave has one **Hacker'Uri** (always level 68, 10222 health, MonsterData 254304,
scale 151, run 383) plus two ranked aliens, and some waves two **Zix** (spider, MonsterData 247728) that land a little
apart (597, 627 / 595, 657) with no waypoint.

| Wave | Aliens (level, health) |
|---|---|
| 1 | Scout - Xoch'Sinuh (59, 3026), Hacker'Uri, Guard - Ankari'Sinuh (71, 4039), 2 Zix (79, 3159) |
| 2 | Scout - Jaax'Heru (58, 8825), Specialist - Jaax'Nakthi (71, 12117), Hacker'Uri |
| 3 | Scout - Cha'Uri (58, 5883), Commando - Jaax'Uri (72, 8247), Hacker'Uri |
| 4 | Commando - Rimah'Uri (58, 5883), Hacker'Uri, Commando - Jaax'Heru (71, 4039) |
| 5 | Scout - Rimah'Heru (58, 8825), Guard - Jaax'Heru (71, 12117), Hacker'Uri |
| 6 | Scout - Cha'Sinuh (72, 4124), Hacker'Uri, Scout - Ankari'Nakthi (58, 8825), 2 Zix (50, 1518) |
| 7 | Scout - Xoch'Sinuh (59, 3026), Guard - Ankari'Uri (71, 8078), Hacker'Uri, 2 Zix (82, 3329) |
| 8 | Hacker'Uri, Commando - Jaax'Heru (58, 2942), Commando - Ilari'Heru (71, 4039) |
| Boss | General - Cha'Khaz (67, 40714, MonsterData 248110) |

MonsterData of the ranked aliens: 251774-251788 (by rank and name). All are Breed Monster, appearance 1227 (Zix 1064).
The pattern in the capture: one alien near 58-59 and one near 71-72 per wave, which fits the guide's "levels based on
the organisation members present".

**None of these templates is in our NpcTemplates.json** (Hacker'Uri, the Scout/Guard/Commando/Specialist names,
General - Cha'Khaz). Only "Advance Sentry - Jaax'Uri"-style and Brigadier General templates exist. They need making
(the capture has level, health, MonsterData, scale and run speed per spawn; 5597 Stat messages hold more).

## Rules from the guide

- No raid while the controller's charge is below 50%; none at all once it is below 10% (the aliens have won; a running
  raid stops sending waves and no boss comes).
- Turning the cloak off (from on) guarantees a raid within 5 minutes (the capture: 3 minutes). The cloak cannot be
  toggled during a raid; after any toggle it is locked for one hour.
- Random raids: on average one every 7 days cloaked, one every 3 days uncloaked or with no cloaking device, when 5+
  org members are online; when nobody is in the city, a low-level alien comes to damage the controller.
- Aliens scale to the owning org's members in the city (not inside buildings); more members, more aliens; guests add
  nothing. Hacker'Uri drains the controller's charge; that is the threat to stop.
- The boss (General) comes after about 20 minutes (a raid runs 20-40 minutes), or sooner when the defenders are winning.
  Boss level from the org members present, at most 300.
- After the General dies, teams with an org member near the controller may be invited to board the alien ship (4-18
  players a boarding team, up to 6 insides); not in this capture.
- Alien XP: at most 10% of the next title per kill; dying to an alien loses up to 25%.

## Feedback ids seen

Feedback category 110: 124550313 (7 times), 154558667 (6), 206104233 (6), 214890029 (3), sent around waves.
Their texts are in the client's LDB table 110.
