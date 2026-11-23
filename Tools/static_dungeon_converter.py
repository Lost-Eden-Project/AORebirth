#!/usr/bin/env python3
"""Turns static-dungeon captures into GameData/Dungeons/Static/<entrance>.json files and the per-style site pool
GameData/Dungeons/Sites/<style>.json (team request 2, "static dungeons").

A capture folder (one per entered door) holds layout.json, objects.jsonl, spawns.jsonl and optionally anarchyf.hex,
as AOSharpLiveCapture's dungeon dump writes them. Its name is <time>_<entrance model>_<dungeon playfield>; the model
is the ACG entrance's identity instance (signed).

    python -I Tools/static_dungeon_converter.py --captures <folder of zone folders> --gamedata <GameData root>
        [--out <GameData root to write>] [--report <json file>]

The converter never reads anything but the captures and the named GameData root (Playfields/<style>/Rooms.json and
Districts.json, NpcTemplates.json, Missions/Destinations/MissionEntrancePlacements.json). Everything it writes is
runtime GameData; the report goes wherever --report points (default: beside the captures).
"""

from __future__ import annotations

import argparse
import base64
import collections
import glob
import json
import math
import os
import struct
import sys

ENTRANCE_TYPE = 56006  # 0xDAC6 ACG entrance
BLOCK = 10.0  # one ACG grid block in world units
FLOOR_GAP = 100.0  # stock's extra X gap between side-by-side floors (DungeonRoomPlacer.Place, FloorHeight 0)
POINT_MATCH = 1.0  # an NPC this close to a district point stands on it
SPOT_MERGE = 0.5  # chest/mine spots this close in one room template are one spot
DEFAULT_GRID = 30

ZONE_BEHAVIOUR = {
    "nasc": "nascence", "nascence": "nascence", "elysium": "elysium", "scheol": "scheol",
    "adonis": "adonis", "penumbra": "penumbra", "inferno": "inferno", "pandemonium": "pandemonium",
}

STAT_STATIC_INSTANCE = 23
STAT_ACG_ITEM_LEVEL = 701

TYPE_PLAYER = 40003
TYPE_TERMINAL = 51005
TYPE_TRAP = 51008
TYPE_MINE = 51009
TYPE_CONTROLLER = 51010
TYPE_CHEST = 51017

TURRET_NAMES = {"Guard Turret"}

# Stock's style 362 lift terminals (every captured SimpleItemFullUpdate agrees); used when a terminal's own full
# update was not captured because the client already knew it.
LIFT_PLATFORM_TEMPLATE = 159862
LIFT_BUTTON_TEMPLATES = {"down": 234528, "up": 234529, "boss": 234525}


def rot_y(v, quadrant):
    """DungeonRoomPlacer.RotateY: turns a template-space vector by quadrant * 90 degrees."""
    x, y, z = v
    quadrant &= 3
    if quadrant == 1:
        return (z, y, -x)
    if quadrant == 2:
        return (-x, y, -z)
    if quadrant == 3:
        return (-z, y, x)
    return (x, y, z)


def yaw_of(q):
    """Yaw (radians) of an XYZW heading; AO headings turn about Y."""
    x, y, z, w = q
    return 2.0 * math.atan2(y, w) if abs(x) < 0.5 and abs(z) < 0.5 else math.atan2(2 * (w * y + x * z), 1 - 2 * (y * y + z * z))


def yaw_heading(yaw):
    return [0.0, round(math.sin(yaw / 2), 6), 0.0, round(math.cos(yaw / 2), 6)]


def parse_stats(hex_payload):
    """
    The (stat, value) pairs of a captured full update, from its StaticInstance stat (23) on. The stat list is found
    by its shape, not its offset: the first candidate whose ACGItemTemplateID (702) repeats the StaticInstance value;
    a stray 0x17 inside a coordinate float does not pass that check.
    """
    b = bytes.fromhex(hex_payload)
    start = b.find(bytes.fromhex("00000017"))
    while start >= 0:
        out = {}
        i = start
        while i + 8 <= len(b):
            sid, val = struct.unpack_from(">II", b, i)
            if sid > 5000:
                break
            out[sid] = val
            i += 8
        if out.get(702) == out.get(23) and out.get(701) is not None:
            return out
        start = b.find(bytes.fromhex("00000017"), start + 1)
    return {}


def parse_owner(hex_payload):
    """(owner type, owner instance) of a captured full update; (0, 0) for a positioned world object."""
    b = bytes.fromhex(hex_payload)
    # header 8 bytes, sender/receiver/n3 12, identity 8, unknown 4, version 1
    return struct.unpack_from(">II", b, 8 + 12 + 8 + 4 + 1)


def outdoor_playfield(hex_payload):
    """The playfield id of a captured outdoor PlayfieldAnarchyF (the one the character stood on when the capture started)."""
    h = bytes.fromhex(hex_payload)
    marker = h.find(bytes.fromhex("00009C50"))
    return struct.unpack_from(">I", h, marker + 4)[0] if marker >= 0 else None


def is_acg_anarchyf(hex_payload):
    """True when the captured PlayfieldAnarchyF carries an inline ACG building generator (0xC7A1 identity)."""
    h = bytes.fromhex(hex_payload)
    first = h.find(bytes.fromhex("0000C7A1"))
    return first >= 0 and h.find(bytes.fromhex("0000C7A1"), first + 4) >= 0


def parse_anarchyf(hex_payload):
    """Width, height, floor height, style, ambient and the room rows of a captured PlayfieldAnarchyF generator."""
    h = bytes.fromhex(hex_payload)
    first = h.find(bytes.fromhex("0000C7A1"))
    inline = h.find(bytes.fromhex("0000C7A1"), first + 4)
    p = inline + 8 + 4  # inline identity, inline version
    version, width, height, floor_height = struct.unpack_from(">HHHH", h, p)
    p += 8
    style = struct.unpack_from(">I", h, p)[0]
    p += 4
    ambient = list(h[p:p + 3])
    p += 3
    count = struct.unpack_from(">I", h, p)[0]
    p += 4
    rows = [struct.unpack_from(">HbBBB", h, p + 6 * i) for i in range(count)]
    if version != 3:
        raise ValueError("unexpected ACG generator version " + str(version))
    return width, height, floor_height, style, ambient, rows


class Style:
    """One ACG style playfield's Rooms.json and Districts.json."""

    def __init__(self, gamedata, style_id):
        self.id = style_id
        folder = os.path.join(gamedata, "Playfields", str(style_id))
        rooms = json.load(open(os.path.join(folder, "Rooms.json"), encoding="utf-8"))
        rooms = rooms.get("rooms") or rooms.get("Rooms")
        self.rooms_by_name = {}
        for room in rooms:
            name = room.get("name") or room.get("Name")
            self.rooms_by_name[name] = room.get("index") if "index" in room else room.get("Index")
        districts = json.load(open(os.path.join(folder, "Districts.json"), encoding="utf-8"))
        zone_map = districts.get("zoneToDistrictMap") or districts.get("ZoneToDistrictMap")
        if isinstance(zone_map, str):
            zone_map = list(base64.b64decode(zone_map))
        self.zone_to_district = zone_map or []
        self.districts = {}
        for district in districts.get("districts") or districts.get("Districts") or []:
            index = district.get("districtIndex", district.get("DistrictIndex"))
            points = district.get("spawnPoints") or district.get("SpawnPoints") or []
            self.districts[index] = [p.get("position") or p.get("Position") for p in points]

    def room_index(self, name):
        return self.rooms_by_name.get(name)

    def points(self, room_name):
        zone = self.room_index(room_name)
        if zone is None:
            return []
        district = self.zone_to_district[zone] if zone < len(self.zone_to_district) else zone
        return self.districts.get(district, [])


class RoomTransform:
    """A captured room's placement: template space <-> dungeon space."""

    def __init__(self, room):
        self.name = room["Name"]
        self.instance = room["Instance"]
        self.floor = room["Floor"]
        self.position = tuple(room["Position"])
        self.template_pos = tuple(room["TemplatePos"])
        self.delta = ((room["Rotation"] - room["TemplateRotation"]) // 90) & 3
        self.rect = room["Rect"]
        self.rotation_quadrant = (room["Rotation"] // 90) & 3

    def to_world(self, template_point):
        local = (template_point[0] - self.template_pos[0], template_point[1] - self.template_pos[1], template_point[2] - self.template_pos[2])
        rx, ry, rz = rot_y(local, self.delta)
        return (self.position[0] + rx, self.position[1] + ry, self.position[2] + rz)

    def to_template(self, world):
        """Template X/Z of a dungeon point; Y is its height above this room's floor."""
        local = (world[0] - self.position[0], 0.0, world[2] - self.position[2])
        lx, _, lz = rot_y(local, (-self.delta) & 3)
        return (round(lx + self.template_pos[0], 3), round(world[1] - self.position[1], 3), round(lz + self.template_pos[2], 3))

    def yaw_to_template(self, yaw):
        return yaw - self.delta * (math.pi / 2)


class NpcTemplates:
    """NpcTemplates.json: hash by (name, MonsterData), the nearest level band winning when several hashes share them."""

    def __init__(self, gamedata):
        data = json.load(open(os.path.join(gamedata, "NpcTemplates.json"), encoding="utf-8"))
        self.by_key = collections.defaultdict(list)
        for hash_text, entry in data.items():
            for template in entry.get("Templates", []):
                stats = template.get("Stats", {})
                name = template.get("Name")
                monster_data = stats.get("359")
                level = stats.get("54")
                if name is None or monster_data is None:
                    continue
                self.by_key[(name, int(monster_data))].append((hash_text, int(level) if level is not None else None))

    def resolve(self, name, monster_data, level):
        candidates = self.by_key.get((name, monster_data))
        if not candidates:
            return None
        best = min(candidates, key=lambda c: (abs((c[1] if c[1] is not None else level) - level), c[0]))
        return best[0]


def load_placements(gamedata):
    path = os.path.join(gamedata, "Missions", "Destinations", "MissionEntrancePlacements.json")
    data = json.load(open(path, encoding="utf-8"))
    rows = data.get("Placements") if isinstance(data, dict) else data
    return {(row["IdentityType"], row["IdentityInstance"]): row for row in rows}


def read_jsonl(path):
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                yield json.loads(line)


def mode(values, default=None):
    counts = collections.Counter(values)
    return counts.most_common(1)[0][0] if counts else default


class SitePool:
    """Chest, mine and turret spots per room template, in template coordinates, over every capture."""

    def __init__(self, style_id):
        self.style = style_id
        self.rooms_placed = collections.Counter()
        self.spots = {kind: collections.defaultdict(list) for kind in ("Chests", "Mines", "Turrets")}
        self.counts = {kind: collections.Counter() for kind in ("Chests", "Mines", "Turrets")}

    def add_room(self, name):
        self.rooms_placed[name] += 1

    def add(self, kind, room_name, template_point, heading):
        self.counts[kind][room_name] += 1
        spots = self.spots[kind][room_name]
        for spot in spots:
            if math.dist((spot["Position"][0], spot["Position"][2]), (template_point[0], template_point[2])) <= SPOT_MERGE:
                spot["Seen"] += 1
                return
        entry = {"Position": [template_point[0], template_point[1], template_point[2]], "Seen": 1}
        if heading is not None:
            entry["Heading"] = heading
        spots.append(entry)

    def to_json(self):
        rooms = {}
        for name in sorted(self.rooms_placed):
            placed = self.rooms_placed[name]
            room = {}
            for kind in ("Chests", "Mines", "Turrets"):
                spots = self.spots[kind].get(name)
                if not spots:
                    continue
                room[kind] = {
                    "PerRoom": round(self.counts[kind][name] / placed, 3),
                    "Spots": sorted(spots, key=lambda s: (s["Position"][0], s["Position"][2])),
                }
            if room:
                rooms[name] = room
        return {"SchemaVersion": 1, "Style": self.style, "Rooms": rooms}


def convert_capture(folder, zone, style, npc_templates, placements, pool, report):
    """One capture folder -> a static dungeon definition dict, or None (reported) when it cannot be converted."""
    name = os.path.basename(os.path.normpath(folder))
    parts = name.split("_")
    model = int(parts[1])
    entrance_instance = model & 0xFFFFFFFF
    layout = json.load(open(os.path.join(folder, "layout.json"), encoding="utf-8"))
    rooms = [RoomTransform(room) for room in layout["Rooms"]]
    by_instance = {room.instance: room for room in rooms}
    problems = []

    placement = placements.get((ENTRANCE_TYPE, entrance_instance))
    if placement is None:
        problems.append("entrance " + str(entrance_instance) + " is not in MissionEntrancePlacements.json; its playfield is unknown")

    # Building rows, exactly as stock sends them (checked against the captured PlayfieldAnarchyF where present).
    width = height = DEFAULT_GRID
    floor_height = 0
    ambient = [50, 50, 50]
    captured_rows = None
    entrance_playfield = placement["PlayfieldId"] if placement else None
    anarchyf = os.path.join(folder, "anarchyf.hex")
    if os.path.exists(anarchyf):
        blob = open(anarchyf).read().strip()
        if is_acg_anarchyf(blob):
            width, height, floor_height, hex_style, ambient, captured_rows = parse_anarchyf(blob)
            if hex_style != style.id:
                problems.append("PlayfieldAnarchyF style " + str(hex_style) + " differs from " + str(style.id))
        elif entrance_playfield is None:
            # The outdoor playfield the character stood on when the capture started: the entrance's playfield.
            entrance_playfield = outdoor_playfield(blob)
            if entrance_playfield is not None:
                problems[-1] += "; taken from the captured outdoor PlayfieldAnarchyF: " + str(entrance_playfield)
    min_floor = min(room.floor for room in rooms)
    building_rows = []
    for room in rooms:
        index = style.room_index(room.name)
        if index is None:
            problems.append("room template " + room.name + " is not in Rooms.json of style " + str(style.id))
            continue
        floor_delta = room.floor - min_floor
        grid_x = (room.rect["MinX"] - floor_delta * (width * BLOCK + FLOOR_GAP)) / BLOCK
        grid_z = (height * BLOCK - room.rect["MaxY"]) / BLOCK
        if grid_x != int(grid_x) or grid_z != int(grid_z) or not 0 <= grid_x <= 0x20 or not 0 <= grid_z <= 0x20:
            problems.append("room " + str(room.instance) + " (" + room.name + ") does not sit on the " + str(width) + "-block grid")
            continue
        building_rows.append({"Room": index, "Floor": room.floor, "X": int(grid_x), "Z": int(grid_z), "Rotation": room.rotation_quadrant})
        pool.add_room(room.name)
    if captured_rows is not None:
        mismatches = sum(1 for i, row in enumerate(captured_rows)
                         if i >= len(building_rows) or (row[0], row[1], row[2], row[3], row[4]) != (
                             building_rows[i]["Room"], building_rows[i]["Floor"], building_rows[i]["X"], building_rows[i]["Z"], building_rows[i]["Rotation"]))
        if mismatches or len(captured_rows) != len(building_rows):
            problems.append("rebuilt building rows differ from the captured PlayfieldAnarchyF in " + str(mismatches) + " rows")
    room_slot = {room.instance: i for i, room in enumerate(rooms)}

    # World objects: chests, mines, traps, controllers, lift terminals, the player's arrival.
    chest_dynels, mine_dynels, controller_dynels, terminal_dynels = {}, {}, {}, {}
    chest_updates, mine_updates, controller_updates, terminal_updates = {}, {}, {}, {}
    trapped_chests = {}
    arrival = None
    for obj in read_jsonl(os.path.join(folder, "objects.jsonl")):
        type_id = obj["IdentityTypeId"]
        if type_id == TYPE_PLAYER and arrival is None and obj.get("Position"):
            arrival = (obj["Position"], obj["Rotation"])
            continue
        # A world object the capture saw in place: as a dynel line, or as a full update that also carried its room.
        if obj.get("Position") and obj.get("Room"):
            target = {TYPE_CHEST: chest_dynels, TYPE_MINE: mine_dynels, TYPE_CONTROLLER: controller_dynels, TYPE_TERMINAL: terminal_dynels}.get(type_id)
            if target is not None and obj["Instance"] not in target:
                target[obj["Instance"]] = obj
        if not obj.get("Hex"):
            continue
        stats = parse_stats(obj["Hex"])
        owner_type, owner_instance = parse_owner(obj["Hex"])
        source = obj.get("Source")
        if source == "ChestFullUpdate" and owner_type == 0:
            chest_updates[obj["Instance"]] = stats
        elif source == "MineFullUpdate" and owner_type == 0:
            mine_updates[obj["Instance"]] = stats
        elif source == "CentralControllerFullUpdate" and owner_type == 0:
            controller_updates[obj["Instance"]] = stats
        elif source == "SimpleItemFullUpdate":
            terminal_updates[obj["Instance"]] = stats
        elif source == "TrapItemFullUpdate" and owner_type == 0xC749:
            trapped_chests[owner_instance] = stats
    if arrival is None:
        problems.append("no player position captured for the arrival point")

    # NPCs: on their room's district point; wanderers on the nearest free point; turrets into the site pool.
    npcs = []
    turret_levels = []
    turret_hashes = collections.Counter()
    turret_names = {}
    unresolved = collections.Counter()
    taken_points = collections.defaultdict(set)
    off_point = 0
    dropped = 0
    for spawn in read_jsonl(os.path.join(folder, "spawns.jsonl")):
        if spawn.get("Source") != "scfu":
            continue
        room_ref = spawn.get("Room")
        if not room_ref or room_ref["Instance"] not in by_instance:
            dropped += 1
            continue
        room = by_instance[room_ref["Instance"]]
        hash_text = npc_templates.resolve(spawn["Name"], spawn["MonsterData"], spawn["Level"])
        if hash_text is None:
            unresolved[(spawn["Name"], spawn["MonsterData"], spawn["Family"])] += 1
            report["unresolvedLevels"][(spawn["Name"], spawn["MonsterData"], spawn["Family"])].add(spawn["Level"])
        if spawn["Name"] in TURRET_NAMES:
            template_point = room.to_template(spawn["Position"])
            heading = yaw_heading(room.yaw_to_template(yaw_of(spawn["Heading"])))
            pool.add("Turrets", room.name, template_point, heading)
            turret_levels.append(spawn["Level"])
            if hash_text:
                turret_hashes[hash_text] += 1
            turret_names[(spawn["Name"], spawn["MonsterData"])] = True
            continue
        points = style.points(room.name)
        if not points:
            problems.append("room " + room.name + " has no district spawn points; NPC " + spawn["Name"] + " dropped")
            dropped += 1
            continue
        distances = []
        for index, point in enumerate(points):
            world = room.to_world(point)
            distances.append((math.dist((world[0], world[2]), (spawn["Position"][0], spawn["Position"][2])), index))
        distances.sort()
        nearest_distance, point_index = distances[0]
        if nearest_distance > POINT_MATCH:
            off_point += 1
            free = [entry for entry in distances if entry[1] not in taken_points[room.instance]]
            point_index = (free[0] if free else distances[0])[1]
        taken_points[room.instance].add(point_index)
        entry = {
            "Hash": hash_text,
            "Name": spawn["Name"],
            "MonsterData": spawn["MonsterData"],
            "Level": spawn["Level"],
            "Room": room_slot[room.instance],
            "Point": point_index,
            "Heading": [round(c, 6) for c in spawn["Heading"]],
            "Captured": [round(c, 3) for c in spawn["Position"]],
        }
        npcs.append(entry)

    # Chests: templates, quality and trap share for this dungeon; spots into the site pool.
    chest_templates = collections.Counter()
    chest_qualities = []
    trapped = 0
    trap_templates = collections.Counter()
    trap_qualities = []
    for instance, dynel in chest_dynels.items():
        room = by_instance.get(dynel["Room"]["Instance"])
        if room is None:
            continue
        stats = chest_updates.get(instance)
        if stats:
            chest_templates[stats.get(STAT_STATIC_INSTANCE)] += 1
            chest_qualities.append(stats.get(STAT_ACG_ITEM_LEVEL))
            if instance in trapped_chests:
                trapped += 1
                trap_templates[trapped_chests[instance].get(STAT_STATIC_INSTANCE)] += 1
                trap_qualities.append(trapped_chests[instance].get(STAT_ACG_ITEM_LEVEL))
        heading = yaw_heading(room.yaw_to_template(yaw_of(dynel["Rotation"]))) if dynel.get("Rotation") else None
        pool.add("Chests", room.name, room.to_template(dynel["Position"]), heading)
    chests_seen = sum(1 for instance in chest_dynels if instance in chest_updates)

    mine_templates = collections.Counter()
    mine_qualities = []
    for instance, dynel in mine_dynels.items():
        room = by_instance.get(dynel["Room"]["Instance"])
        if room is None:
            continue
        stats = mine_updates.get(instance)
        if stats:
            mine_templates[stats.get(STAT_STATIC_INSTANCE)] += 1
            mine_qualities.append(stats.get(STAT_ACG_ITEM_LEVEL))
        pool.add("Mines", room.name, room.to_template(dynel["Position"]), None)

    controllers = []
    for instance, dynel in controller_dynels.items():
        room = by_instance.get(dynel["Room"]["Instance"])
        stats = controller_updates.get(instance)
        if room is None or not stats:
            continue
        controllers.append({
            "Template": stats.get(STAT_STATIC_INSTANCE),
            "Quality": stats.get(STAT_ACG_ITEM_LEVEL),
            "Name": dynel.get("Name"),
            "Position": [round(c, 3) for c in dynel["Position"]],
            "Room": room_slot[room.instance],
        })

    # Lifts: a floor's departure (down or boss button) pairs with the next lower floor's arrival (up button).
    endpoints = collections.defaultdict(dict)  # floor -> role -> endpoint
    platform_template = None
    for instance, dynel in terminal_dynels.items():
        room = by_instance.get(dynel["Room"]["Instance"])
        if room is None:
            continue
        label = dynel.get("Name") or ""
        stats = terminal_updates.get(instance, {})
        template = stats.get(STAT_STATIC_INSTANCE)
        key = (room.floor, room.instance)
        slot = endpoints[key]
        slot["Room"] = room_slot[room.instance]
        slot["Floor"] = room.floor
        slot["Position"] = [round(c, 3) for c in dynel["Position"]]
        if label.startswith("Platform"):
            slot["Platform"] = template
            platform_template = platform_template or template
        elif label.startswith("Button"):
            slot["Button"] = template
            slot["Role"] = "up" if "(up)" in label else "boss" if "(boss)" in label else "down"
    arrivals = {}
    departures = collections.defaultdict(list)
    for slot in endpoints.values():
        role = slot.get("Role")
        if role == "up":
            if slot["Floor"] in arrivals:
                problems.append("two arrival lifts on floor " + str(slot["Floor"]))
            arrivals[slot["Floor"]] = slot
        elif role in ("down", "boss"):
            departures[slot["Floor"]].append(slot)
    transitions = []
    for floor in sorted(departures, reverse=True):
        for slot in departures[floor]:
            target = arrivals.get(floor - 1)
            if target is None:
                problems.append("lift on floor " + str(floor) + " has no arrival on floor " + str(floor - 1))
                continue
            for endpoint in (slot, target):
                if endpoint.get("Platform") is None:
                    endpoint["Platform"] = platform_template or LIFT_PLATFORM_TEMPLATE
                if endpoint.get("Button") is None:
                    endpoint["Button"] = LIFT_BUTTON_TEMPLATES[endpoint["Role"]]
            transitions.append({
                "From": {"Room": slot["Room"], "Position": slot["Position"], "Platform": slot.get("Platform"), "Button": slot.get("Button")},
                "To": {"Room": target["Room"], "Position": target["Position"], "Platform": target.get("Platform"), "Button": target.get("Button")},
            })
    for floor, slot in arrivals.items():
        if not any(floor + 1 == f for f in departures):
            problems.append("arrival lift on floor " + str(floor) + " has no departure above it")

    behaviour = ZONE_BEHAVIOUR.get(zone.lower(), zone.lower())
    turret_hash = turret_hashes.most_common(1)[0][0] if turret_hashes else None
    turret_name = next(iter(turret_names), (None, None))
    definition = {
        "SchemaVersion": 1,
        "Name": placement["DisplayName"] if placement else os.path.basename(os.path.dirname(folder)),
        "Entrance": {
            "IdentityType": ENTRANCE_TYPE,
            "IdentityInstance": entrance_instance,
            "Playfield": entrance_playfield,
        },
        "Building": {
            "Width": width, "Height": height, "FloorHeight": floor_height, "TemplatePlayfield": style.id,
            "Ambient": ambient, "Rooms": building_rows,
        },
        "Arrival": {
            "Position": [round(c, 3) for c in arrival[0]] if arrival else None,
            "Heading": [round(c, 6) for c in arrival[1]] if arrival else None,
        },
        "Instance": "Team",
        "RespawnSeconds": None,
        "Behaviour": behaviour,
        "DoorStyle": style.id,
        "Transitions": transitions,
        "Npcs": npcs,
        "Chests": {
            "Templates": sorted(t for t in chest_templates if t),
            "Quality": mode(chest_qualities, 1),
            "Loot": behaviour + "Chest",
            "LockDifficulty": 0,
            "TrapChancePercent": int(round(100.0 * trapped / chests_seen)) if chests_seen else 0,
            "Traps": sorted(t for t in trap_templates if t),
        },
        "Mines": {"Templates": sorted(t for t in mine_templates if t), "Quality": mode(mine_qualities, mode(chest_qualities, 1))},
        "Turrets": {
            "Hash": turret_hash, "Name": turret_name[0], "MonsterData": turret_name[1],
            "Level": mode(turret_levels, None),
        } if turret_levels else None,
        "Controllers": controllers,
    }
    report["dungeons"].append({
        "capture": folder, "entrance": entrance_instance, "playfield": definition["Entrance"]["Playfield"],
        "name": definition["Name"], "rooms": len(building_rows), "npcs": len(npcs), "npcsOffPoint": off_point,
        "npcsDropped": dropped, "npcsUnresolved": sum(unresolved.values()), "turrets": len(turret_levels),
        "chests": len(chest_dynels), "trapped": trapped, "mines": len(mine_dynels), "controllers": len(controllers),
        "transitions": len(transitions), "problems": problems,
    })
    for key, count in unresolved.items():
        report["unresolvedNpcs"][key] = report["unresolvedNpcs"].get(key, 0) + count
    return definition, problems


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--captures", required=True, help="folder holding one folder per zone, each holding one folder per dungeon")
    parser.add_argument("--gamedata", required=True, help="GameData root to read Playfields, NpcTemplates and the entrance placements from")
    parser.add_argument("--out", help="GameData root to write Dungeons/Static and Dungeons/Sites into (default: --gamedata)")
    parser.add_argument("--style", type=int, default=362, help="ACG style playfield of the captured dungeons (default 362)")
    parser.add_argument("--report", help="where to write the conversion report (default: static_dungeon_conversion.json beside the captures)")
    args = parser.parse_args(argv)

    out_root = args.out or args.gamedata
    style = Style(args.gamedata, args.style)
    npc_templates = NpcTemplates(args.gamedata)
    placements = load_placements(args.gamedata)
    pool = SitePool(args.style)
    report = {"dungeons": [], "unresolvedNpcs": {}, "unresolvedLevels": collections.defaultdict(set)}

    static_dir = os.path.join(out_root, "Dungeons", "Static")
    sites_dir = os.path.join(out_root, "Dungeons", "Sites")
    os.makedirs(static_dir, exist_ok=True)
    os.makedirs(sites_dir, exist_ok=True)

    written = 0
    for layout_path in sorted(glob.glob(os.path.join(args.captures, "*", "*", "*", "layout.json"))):
        folder = os.path.dirname(layout_path)
        zone = os.path.basename(os.path.dirname(os.path.dirname(folder)))
        definition, problems = convert_capture(folder, zone, style, npc_templates, placements, pool, report)
        path = os.path.join(static_dir, str(definition["Entrance"]["IdentityInstance"]) + ".json")
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(definition, handle, indent=2)
            handle.write("\n")
        written += 1
        tag = " PROBLEMS: " + "; ".join(problems) if problems else ""
        print(f"{definition['Entrance']['IdentityInstance']} {definition['Name']!r} playfield={definition['Entrance']['Playfield']} "
              f"rooms={len(definition['Building']['Rooms'])} npcs={len(definition['Npcs'])} lifts={len(definition['Transitions'])}{tag}")

    with open(os.path.join(sites_dir, str(args.style) + ".json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(pool.to_json(), handle, indent=2)
        handle.write("\n")

    levels = report.pop("unresolvedLevels")
    report["unresolvedNpcs"] = [
        {"name": key[0], "monsterData": key[1], "family": key[2], "levels": sorted(levels[key]), "count": count}
        for key, count in sorted(report["unresolvedNpcs"].items(), key=lambda kv: -kv[1])
    ]
    report_path = args.report or os.path.join(args.captures, "static_dungeon_conversion.json")
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(f"wrote {written} static dungeons and the style {args.style} site pool; report at {report_path}")
    if report["unresolvedNpcs"]:
        print("NPC names with no NpcTemplates match (Name + MonsterData); add templates and run the StaticDungeons reload:")
        for row in report["unresolvedNpcs"]:
            print(f"  {row['name']!r} MonsterData={row['monsterData']} Family={row['family']} levels={row['levels'][0]}-{row['levels'][-1]} x{row['count']}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
