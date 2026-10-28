"""Offline, resumable evidence indexing and exact chest inventory correlation.

The canonical wire reader remains the mission analyzer's recovered protocol
implementation. This file preserves source rows and joins decoded identities;
it never generates gameplay loot or modifies runtime policy.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
import sqlite3

SCHEMA = 1
KEYS = {
    0x465A5D73: "ChestFullUpdate",
    0x4E536976: "InventoryUpdate",
    0x52526858: "GenericCmd",
    0x5469373F: "ClientMoveItemToInventory",
    0x47537A24: "ContainerAddItem",
}


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def integer(value):
    try:
        return int(value) if value not in (None, "") else None
    except (TypeError, ValueError):
        return None


def number(value):
    try:
        result = float(value)
        return result if math.isfinite(result) else None
    except (TypeError, ValueError):
        return None


def dump(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, sort_keys=True,
                                    indent=2, allow_nan=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def index_corpus(captures, work):
    captures, work = Path(captures).resolve(), Path(work).resolve()
    if captures == work or captures in work.parents:
        raise ValueError("Derived outputs must remain outside original captures")
    work.mkdir(parents=True, exist_ok=True)
    database = work / "capture-index.sqlite"
    connection = sqlite3.connect(database)
    connection.executescript("""
        CREATE TABLE IF NOT EXISTS source_files (
            session TEXT, kind TEXT, path TEXT, sha TEXT, rows INTEGER,
            PRIMARY KEY(session,kind));
        CREATE TABLE IF NOT EXISTS raw_rows (
            session TEXT, record INTEGER, ordinal INTEGER, key INTEGER,
            kind TEXT, data TEXT, PRIMARY KEY(session,record));
        CREATE TABLE IF NOT EXISTS memory (
            session TEXT, record INTEGER, identity_type INTEGER, instance INTEGER,
            pf INTEGER, model INTEGER, data TEXT, PRIMARY KEY(session,record));
        CREATE INDEX IF NOT EXISTS memory_identity ON memory(session,identity_type,instance,pf);
        CREATE INDEX IF NOT EXISTS raw_ordinal ON raw_rows(session,ordinal,record);
    """)
    session_dirs = sorted(path.parent for path in captures.glob("*/capture_info.json"))
    if not session_dirs:
        raise ValueError("No existing session capture_info.json records found")
    source_manifest, sessions = [], []
    for folder in session_dirs:
        info = json.loads((folder / "capture_info.json").read_text(encoding="utf-8-sig"))
        session_rows = {"Session": folder.name, "Metadata": info}
        for kind, filename in (("raw", "raw-packets.csv"), ("memory", "memory-dynels.csv")):
            path = folder / filename
            source_sha = sha(path)
            cached = connection.execute("SELECT sha,rows FROM source_files WHERE session=? AND kind=?",
                                        (folder.name, kind)).fetchone()
            count = cached[1] if cached and cached[0] == source_sha else None
            if count is None:
                table = "raw_rows" if kind == "raw" else "memory"
                with connection:
                    connection.execute("DELETE FROM " + table + " WHERE session=?", (folder.name,))
                    count = 0
                    with path.open(encoding="utf-8-sig", newline="") as stream:
                        for count, row in enumerate(csv.DictReader(stream), 1):
                            row["Session"] = folder.name
                            row["SourceRecord"] = count
                            row["SourceCsv"] = str(path)
                            row["SourceCsvSha256"] = source_sha
                            if kind == "raw":
                                key = integer(row.get("N3TypeValue"))
                                if key is None:
                                    try:
                                        packet = bytes.fromhex(row.get("RawHex") or "")
                                        key = int.from_bytes(packet[16:20], "big") if len(packet) >= 20 else None
                                    except ValueError:
                                        key = None
                                connection.execute("INSERT INTO raw_rows VALUES (?,?,?,?,?,?)",
                                    (folder.name, count, integer(row.get("GlobalOrdinal")), key,
                                     KEYS.get(key), json.dumps(row, ensure_ascii=False, sort_keys=True)))
                            else:
                                connection.execute("INSERT INTO memory VALUES (?,?,?,?,?,?,?)",
                                    (folder.name, count, integer(row.get("IdentityType")),
                                     integer(row.get("IdentityInstance")), integer(row.get("ObservedPlayfieldInstance")),
                                     integer(row.get("ObservedModelInstance")),
                                     json.dumps(row, ensure_ascii=False, sort_keys=True)))
                    if sha(path) != source_sha:
                        raise ValueError("Source changed during indexing: " + str(path))
                    connection.execute("INSERT OR REPLACE INTO source_files VALUES (?,?,?,?,?)",
                                       (folder.name, kind, str(path), source_sha, count))
            session_rows[kind + "_rows"] = count
        sessions.append(session_rows)
        for path in sorted(folder.iterdir()):
            if path.is_file():
                source_manifest.append({"Path": str(path.relative_to(captures)), "Bytes": path.stat().st_size,
                                        "Sha256": sha(path)})
    for path in sorted(captures.iterdir()):
        if path.is_file():
            source_manifest.append({"Path": path.name, "Bytes": path.stat().st_size, "Sha256": sha(path)})
    counts = dict(connection.execute("SELECT kind,count(*) FROM raw_rows WHERE kind IS NOT NULL GROUP BY kind"))
    selected = work / "selected-packets.jsonl"
    with selected.open("w", encoding="utf-8", newline="\n") as output:
        for (data,) in connection.execute("SELECT data FROM raw_rows WHERE kind IS NOT NULL ORDER BY session,ordinal,record"):
            output.write(data + "\n")
    dump(work / "source-manifest.json", source_manifest)
    dump(work / "session-index.json", sessions)
    dump(work / "index-summary.json", {"Schema": SCHEMA, "Sessions": len(sessions),
        "RawRows": connection.execute("SELECT count(*) FROM raw_rows").fetchone()[0],
        "MemoryRows": connection.execute("SELECT count(*) FROM memory").fetchone()[0],
        "SelectedByKind": counts, "SelectedJsonlSha256": sha(selected),
        "OriginalCaptureRoot": str(captures)})
    connection.close()
    return counts


def identity(value):
    return (integer((value or {}).get("Type")) or 0, integer((value or {}).get("Instance")) or 0)


def identity_json(value):
    return {"Type": value[0], "Instance": value[1]}


def reference(event):
    source = event["Source"]
    return {key: source.get(key) for key in ("Session", "GlobalOrdinal", "Sequence", "Direction",
        "CapturedUtc", "SourceRecord", "SourceCsv", "SourceCsvSha256", "ContextEpoch",
        "ObservedPlayfieldType", "ObservedPlayfieldInstance", "ObservedModelType", "ObservedModelInstance")} | {
        "PacketSha256": event.get("PacketSha256"),
        "FrameLengthStatus": event.get("Decoded", {}).get("FrameLengthStatus")}


def ordinal(event):
    return integer(event["Source"].get("GlobalOrdinal")) or 0


def world_key(session, instance, playfield):
    return session + "|51017:" + str(instance) + "|pf:" + str(playfield)


def correlate(work, decoded_path, index_path=None, world_controls=None, session_filter=None):
    work = Path(work)
    work.mkdir(parents=True, exist_ok=True)
    world_controls = world_controls or {}
    db = sqlite3.connect("file:" + Path(index_path or work / "capture-index.sqlite").resolve().as_posix() + "?mode=ro", uri=True)
    memory = defaultdict(list)
    for session, record, instance, pf, model, payload in db.execute(
            "SELECT session,record,instance,pf,model,data FROM memory WHERE identity_type=51017 ORDER BY session,record"):
        row = json.loads(payload)
        if session_filter is not None and session not in session_filter:
            continue
        if pf and model and integer(row.get("ObservedModelType")) == 51103:
            memory[(session, instance, pf)].append(row)
    events = [json.loads(line) for line in Path(decoded_path).read_text(encoding="utf-8-sig").splitlines() if line]
    events.sort(key=lambda event: (event["Source"]["Session"], ordinal(event), integer(event["Source"].get("SourceRecord")) or 0))
    worlds, chest_updates, unresolved = {}, [], []
    for event in events:
        decoded = event.get("Decoded", {})
        if decoded.get("Family") != "ChestFullUpdate":
            if event.get("Status") == "DECODE_FAILURE":
                unresolved.append({"Kind": "DECODER_FAILURE", "Source": reference(event), "Reason": event.get("Error")})
            continue
        session = event["Source"]["Session"]
        key = identity(decoded.get("Identity"))
        parent = identity(decoded.get("Parent"))
        pf = decoded["PlayfieldId"]
        bridge = memory.get((session, key[1], pf), []) if key[0] == 51017 else []
        kind = "CHARACTER_OWNED_CONTAINER" if parent[0] == 50000 else "OTHER_PARENTED_CONTAINER" if parent[0] else "UNRESOLVED_PARENTLESS_CONTAINER"
        control = world_controls.get((session, key, pf))
        if (parent == (0, 0) and pf > 0 and decoded.get("Coordinate") is not None
                and decoded.get("Heading") is not None and (bridge or control)):
            kind = "PLACED_CHEST_FAMILY_OBJECT"
            wid = world_key(session, key[1], pf)
            if wid not in worlds:
                worlds[wid] = make_world(wid, session, key, pf, bridge)
            world = worlds[wid]
            world["SpawnEvidence"].append(reference(event))
            world["SpawnFields"] = decoded
            world["ParentKnown"] = True
            world["Parent"] = identity_json(parent)
            world["Position"] = decoded["Coordinate"]
            world["Heading"] = decoded["Heading"]
            world["ChestTemplateId"] = next((stat["Value"] for stat in decoded["Stats"] if stat["StatId"] == 23), None)
            world["WorldEvidence"] = "EXACT_PARENTLESS_FULL_UPDATE_AND_WORLD_MEMORY_IDENTITY_PLAYFIELD" if bridge else "COMPLETED_RESEARCH_KNOWN_ANSWER_WORLD_CHEST"
            if control:
                world["KnownAnswerControl"] = control
            event["WorldId"] = wid
        event["ContainerClass"] = kind
        chest_updates.append({"Source": reference(event), "Identity": identity_json(key), "Parent": identity_json(parent),
                              "PlayfieldId": pf, "Class": kind, "WorldId": event.get("WorldId"), "Fields": decoded})
    # Memory-only world observations remain explicit. Exact-owner contents are still
    # valid contents evidence; no parent/template is fabricated for their missing spawn.
    for (session, instance, pf), bridge in sorted(memory.items()):
        wid = world_key(session, instance, pf)
        if wid not in worlds:
            world = make_world(wid, session, (51017, instance), pf, bridge)
            world["WorldEvidence"] = "WORLD_DYNEL_MEMORY_ONLY_PARENT_TEMPLATE_UNRECORDED"
            worlds[wid] = world
            unresolved.append({"Kind": "WORLD_MEMORY_WITHOUT_SAME_SESSION_SPAWN", "WorldId": wid,
                               "Identity": world["ChestIdentity"], "MemoryRecords": world["MemoryRecords"]})

    active_chests, handles, requests, interactions = {}, {}, defaultdict(list), []
    inventories, loot, transfers = [], [], []
    inventory_by_id = {}
    transfer_requests = defaultdict(list)
    consumed = set()
    all_inventory_owners = Counter()

    def resolve_world(session, owner, event):
        previous = active_chests.get((session, owner))
        if previous is not None:
            return previous.get("WorldId")
        pf = integer(event["Source"].get("ObservedPlayfieldInstance"))
        candidates = [wid for wid, world in worlds.items() if world["Session"] == session
                      and identity(world["ChestIdentity"]) == owner and (pf is None or world["PlayfieldInstance"] == pf)]
        return candidates[0] if len(candidates) == 1 else None

    for event in events:
        source, decoded = event["Source"], event.get("Decoded", {})
        session, family, direction = source["Session"], decoded.get("Family"), source.get("Direction")
        if family == "ChestFullUpdate":
            active_chests[(session, identity(decoded["Identity"]))] = event
        elif family == "GenericCmd":
            user = identity(decoded["User"])
            targets = tuple(identity(value) for value in decoded["Targets"])
            signature = (session, user, decoded["Action"], decoded["Count"], targets)
            if direction == "OUT" and decoded["Action"] in (3, 5):
                for target in targets:
                    wid = resolve_world(session, target, event)
                    if wid is not None:
                        interaction = {"WorldId": wid, "Source": reference(event), "Actor": identity_json(user),
                            "Action": decoded["Action"], "Kind": "DIRECT_USE3" if decoded["Action"] == 3 else "HELD_ITEM_USE5",
                            "Targets": decoded["Targets"], "RequestCount": decoded["Count"], "Acknowledgements": []}
                        requests[signature].append(interaction)
                        interactions.append(interaction)
                        worlds[wid]["Interactions"].append(interaction)
            elif direction == "IN" and signature in requests:
                candidates = [row for row in requests[signature] if not row["Acknowledgements"]]
                if len(candidates) == 1:
                    candidates[0]["Acknowledgements"].append(reference(event))
        elif family == "InventoryUpdate" and direction == "IN":
            owner, recipient = identity(decoded["Owner"]), identity(decoded["Identity"])
            wid = resolve_world(session, owner, event)
            iid = session + "|inventory:" + str(ordinal(event))
            state = {"InventoryId": iid, "WorldId": wid, "Session": session,
                "Owner": decoded["Owner"], "Recipient": decoded["Identity"], "Handle": decoded["Handle"],
                "NumberOfSlots": decoded["NumberOfSlots"], "InventoryFlags": decoded["InventoryFlags"],
                "OpenResponseFlags": decoded["OpenResponseFlags"], "Source": reference(event),
                "Entries": decoded["Entries"], "LootIds": [], "EntryBySlot": {}}
            handles[(session, recipient, decoded["Handle"])] = state
            all_inventory_owners["WORLD_IDENTIFIED" if wid else str(owner[0])] += 1
            if wid is None:
                # Keep every excluded inventory response with its exact owner, including
                # player/bank/corpse/unknown handles. It can supersede an earlier handle.
                unresolved.append({"Kind": "NON_WORLD_OR_UNRESOLVED_INVENTORY", "Owner": decoded["Owner"],
                    "Recipient": decoded["Identity"], "Handle": decoded["Handle"], "Source": reference(event),
                    "EntryCount": len(decoded["Entries"]),
                    "Reason": active_chests.get((session, owner), {}).get("ContainerClass", "NO_UNAMBIGUOUS_WORLD_IDENTITY_LOCALITY_BRIDGE")})
                continue
            world = worlds[wid]
            world["Inventories"].append(iid)
            candidates = [row for row in world["Interactions"] if identity(row["Actor"]) == recipient]
            state["OpenRequestCandidates"] = [row["Source"] for row in candidates]
            seen_slots = set()
            for entry in decoded["Entries"]:
                slot, low, high, quality = entry["Slot"], entry["LowId"], entry["HighId"], entry["Quality"]
                if low == high == 0:
                    continue
                if slot in seen_slots or slot < 0 or slot >= decoded["NumberOfSlots"] or low <= 0 or high <= 0 or quality < 0:
                    unresolved.append({"Kind": "INVALID_OR_AMBIGUOUS_INVENTORY_ENTRY", "InventoryId": iid,
                                       "Source": reference(event), "Entry": entry})
                    continue
                seen_slots.add(slot)
                lid = iid + "|slot:" + str(slot)
                row = {"LootId": lid, "InventoryId": iid, "WorldId": wid, "Session": session,
                    "ChestIdentity": decoded["Owner"], "InventoryOrdinal": ordinal(event), "Slot": slot,
                    "Handle": decoded["Handle"], "FamilyLowId": low, "FamilyHighId": high,
                    "TemplateId": low if low == high else None, "Quality": quality,
                    "QuantityRaw": entry["Counter"], "QuantityBasis": "RECORDED_NATIVE_STACK_OR_CHARGE_COUNTER",
                    "PhysicalUnitConversion": "UNVERIFIED", "InventoryEntryFlags": entry["Flags"],
                    "RecordedItemIdentity": entry["ItemIdentity"], "Reserved": entry["Reserved"],
                    "EvidenceStatus": "VERIFIED_CONTENTS", "Source": reference(event),
                    "SourceCsvSha256": source.get("SourceCsvSha256"), "RawPacketSha256": event.get("PacketSha256"),
                    "WorldEvidence": world["WorldEvidence"]}
                loot.append(row)
                state["LootIds"].append(lid)
                state["EntryBySlot"][slot] = row
            inventory_by_id[iid] = state
            inventories.append(state)
        elif family == "ClientMoveItemToInventory" and direction == "OUT":
            location = identity(decoded["SourceContainer"])
            if location[0] == 107:
                recipient = identity(decoded["Identity"])
                state = handles.get((session, recipient, location[1] >> 16))
                transfer_requests[(session, recipient, location)].append({
                    "Event": event, "InventoryId": state["InventoryId"] if state else None,
                    "TargetPlacement": decoded["TargetPlacement"]})
        elif family == "ContainerAddItem" and direction == "IN":
            location = identity(decoded["SourceContainer"])
            if location[0] != 107:
                unresolved.append({"Kind": "TRANSFER_FROM_OTHER_LOCATION", "Source": reference(event), "Fields": decoded})
                continue
            handle, slot = location[1] >> 16, location[1] & 0xffff
            recipient = identity(decoded["Identity"])
            state = handles.get((session, recipient, handle))
            pending = transfer_requests.get((session, recipient, location), [])
            matching_requests = []
            for pending_request in pending:
                if pending_request["InventoryId"] is None:
                    reason = "NO_ACTIVE_EXACT_RECIPIENT_HANDLE_INVENTORY_AT_REQUEST"
                elif state is None:
                    reason = "NO_ACTIVE_EXACT_RECIPIENT_HANDLE_INVENTORY_AT_CONFIRMATION"
                elif pending_request["InventoryId"] != state["InventoryId"]:
                    reason = "REQUEST_INVENTORY_EPOCH_SUPERSEDED"
                elif pending_request["TargetPlacement"] != decoded["TargetPlacement"]:
                    reason = "REQUEST_CONFIRMATION_TARGET_PLACEMENT_MISMATCH"
                else:
                    matching_requests.append(pending_request["Event"])
                    continue
                unresolved.append({"Kind": "UNMATCHED_TRANSFER_REQUEST", "Reason": reason,
                    "Source": reference(pending_request["Event"]), "Confirmation": reference(event),
                    "RequestInventoryId": pending_request["InventoryId"],
                    "ActiveInventoryId": state["InventoryId"] if state else None,
                    "RequestTargetPlacement": pending_request["TargetPlacement"],
                    "ConfirmationTargetPlacement": decoded["TargetPlacement"],
                    "SourceContainer": decoded["SourceContainer"]})
            transfer_requests[(session, recipient, location)] = []
            if pending and not matching_requests:
                unresolved.append({"Kind": "TRANSFER_REJECTED_REQUEST_BINDING",
                    "Reason": "NO_CAPTURED_REQUEST_MATCHES_ACTIVE_INVENTORY_EPOCH_AND_TARGET_PLACEMENT",
                    "Source": reference(event), "Fields": decoded,
                    "ActiveInventoryId": state["InventoryId"] if state else None,
                    "RejectedRequests": [reference(value["Event"]) for value in pending]})
                continue
            if state is None or state["WorldId"] is None or slot not in state["EntryBySlot"]:
                unresolved.append({"Kind": "TRANSFER_WITHOUT_VERIFIED_WORLD_SLOT", "Source": reference(event),
                    "Handle": handle, "Slot": slot, "OwnerInventory": state["InventoryId"] if state else None,
                    "Reason": "NO_ACTIVE_EXACT_RECIPIENT_HANDLE_WORLD_INVENTORY_SLOT", "Fields": decoded,
                    "MatchingRequests": [reference(value) for value in matching_requests]})
                continue
            lid = state["EntryBySlot"][slot]["LootId"]
            if lid in consumed:
                unresolved.append({"Kind": "REPEATED_TRANSFER_OF_CONSUMED_INVENTORY_EPOCH_SLOT", "Source": reference(event), "LootId": lid})
                continue
            row = state["EntryBySlot"][slot]
            target = identity(decoded["Target"])
            if target != recipient:
                unresolved.append({"Kind": "TRANSFER_RECIPIENT_TARGET_MISMATCH", "Source": reference(event), "Fields": decoded})
                continue
            request_refs = [reference(value) for value in matching_requests]
            transfers.append({"TransferId": session + "|add:" + str(ordinal(event)), "WorldId": state["WorldId"],
                "InventoryId": state["InventoryId"], "LootId": lid, "ChestIdentity": state["Owner"],
                "SourceContainer": decoded["SourceContainer"], "Handle": handle, "Slot": slot,
                "Recipient": decoded["Target"], "TargetPlacement": decoded["TargetPlacement"],
                "FamilyLowId": row["FamilyLowId"], "FamilyHighId": row["FamilyHighId"], "Quality": row["Quality"],
                "SourceQuantityRaw": row["QuantityRaw"], "TransferQuantity": None,
                "Requests": request_refs, "Confirmation": reference(event),
                "EvidenceStatus": "VERIFIED_TRANSFER_CHAIN" if request_refs else "VERIFIED_SOURCE_SLOT_CONFIRMATION_NO_MATCHING_CAPTURED_REQUEST"})
            consumed.add(lid)
    for pending in transfer_requests.values():
        for pending_request in pending:
            unresolved.append({"Kind": "UNMATCHED_TRANSFER_REQUEST", "Reason": "NO_MATCHING_CONTAINER_ADD_CONFIRMATION",
                "Source": reference(pending_request["Event"]),
                "RequestInventoryId": pending_request["InventoryId"],
                "RequestTargetPlacement": pending_request["TargetPlacement"],
                "SourceContainer": pending_request["Event"]["Decoded"]["SourceContainer"]})
    for state in inventories:
        del state["EntryBySlot"]
    summary = {"WorldObservations": len(worlds), "WorldIdentities": len({identity(world["ChestIdentity"]) for world in worlds.values()}),
        "FullUpdateWorldObservations": sum(bool(world["SpawnEvidence"]) for world in worlds.values()),
        "MemoryOnlyWorldObservations": sum(not world["SpawnEvidence"] for world in worlds.values()),
        "InventoryVerifiedWorldObservations": sum(bool(world["Inventories"]) for world in worlds.values()),
        "OutgoingChestInteractions": len(interactions), "DirectUse3": sum(row["Action"] == 3 for row in interactions),
        "HeldItemUse5": sum(row["Action"] == 5 for row in interactions), "RecoveredInventories": len(inventories),
        "OpenFlagContentsResponses": sum(bool(row["OpenResponseFlags"]) for row in inventories),
        "LootRecords": len(loot), "UniqueDescriptorPairs": len({(row["FamilyLowId"], row["FamilyHighId"]) for row in loot}),
        "DistinctEndpointTemplates": len({identity for row in loot for identity in (row["FamilyLowId"], row["FamilyHighId"])}),
        "QualityMinimum": min((row["Quality"] for row in loot), default=None),
        "QualityMaximum": max((row["Quality"] for row in loot), default=None),
        "VerifiedTransfers": len(transfers), "FullTransferChains": sum(bool(row["Requests"]) for row in transfers),
        "RawChestRecords": len(chest_updates), "InventoryOwnerClasses": dict(sorted(all_inventory_owners.items())),
        "UnresolvedByKind": dict(sorted(Counter(row["Kind"] for row in unresolved).items()))}
    dump(work / "MissionChestCaptureInventory.json", {"Schema": SCHEMA, "Summary": summary,
        "Chests": sorted(worlds.values(), key=lambda row: row["WorldId"]), "ChestFamilyRecords": chest_updates, "Inventories": inventories})
    dump(work / "MissionChestCapturedLoot.json", {"Schema": SCHEMA, "Records": loot})
    dump(work / "MissionChestLootTransfers.json", {"Schema": SCHEMA, "Records": transfers})
    dump(work / "MissionChestUnresolved.json", {"Schema": SCHEMA, "Records": unresolved})
    dump(work / "correlation-summary.json", summary)
    db.close()
    return summary


def make_world(wid, session, key, pf, bridge):
    rooms = sorted({(integer(row.get("RoomInstance")), integer(row.get("RoomFloor"))) for row in bridge}, key=str)
    memory_positions = []
    for row in bridge:
        position = [number(row.get("Position" + axis)) for axis in ("X", "Y", "Z")]
        if all(value is not None for value in position):
            memory_positions.append({"Position": position, "SourceRecord": row["SourceRecord"],
                "SourceCsvSha256": row["SourceCsvSha256"], "CapturedUtc": row.get("CapturedUtc"),
                "RoomInstance": integer(row.get("RoomInstance")), "RoomFloor": integer(row.get("RoomFloor"))})
    return {"WorldId": wid, "Session": session, "ChestIdentity": identity_json(key), "PlayfieldInstance": pf,
        "Models": sorted({integer(row.get("ObservedModelInstance")) for row in bridge if integer(row.get("ObservedModelInstance")) is not None}),
        "ObservedNames": sorted({row.get("Name") for row in bridge if row.get("Name")}),
        "Rooms": [{"RoomInstance": room, "RoomFloor": floor} for room, floor in rooms],
        "MemoryRecords": [row["SourceRecord"] for row in bridge],
        "MemorySourceSha256": sorted({row["SourceCsvSha256"] for row in bridge}),
        "MemoryPositionObservations": memory_positions,
        "MemoryPositionEvidence": "RECORDED_DYNEL_MEMORY_SEPARATE_FROM_NATIVE_FULL_UPDATE_TRANSFORM",
        "SpawnEvidence": [], "SpawnFields": None, "ParentKnown": False, "Parent": None,
        "Position": None, "Heading": None, "ChestTemplateId": None,
        "Inventories": [], "Interactions": []}


def validate_known_answer(work):
    work = Path(work)
    fixture_path = work / "MissionChestKnownAnswerFixture.json"
    fixture = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    expected, session = fixture["Expected"], fixture["CaptureSession"]
    decoded_path = work / "known-answer-decoded.jsonl"
    events = [json.loads(line) for line in decoded_path.read_text(encoding="utf-8-sig").splitlines() if line]
    chest = next(event for event in events if ordinal(event) == expected["ChestSpawnOrdinal"])
    body = chest["Decoded"]
    if identity(body["Identity"]) != tuple(expected["ChestIdentity"]) or identity(body["Parent"]) != tuple(expected["ChestParent"]):
        raise AssertionError("Original known-answer chest identity/parent differs")
    controls = {(session, tuple(expected["ChestIdentity"]), body["PlayfieldId"]):
                {"FixtureSha256": sha(fixture_path), "ResearchSources": fixture["ResearchSources"]}}
    output = work / "known-answer-validation"
    result = correlate(output, decoded_path, work / "capture-index.sqlite", controls, {session})
    inventories = json.loads((output / "MissionChestCaptureInventory.json").read_text(encoding="utf-8"))["Inventories"]
    inventory = next(row for row in inventories if integer(row["Source"]["GlobalOrdinal"]) == expected["ContentsOrdinal"])
    assert inventory["Handle"] == expected["Handle"]
    assert identity(inventory["Owner"]) == tuple(expected["OwnerIdentity"])
    assert len(inventory["Entries"]) == expected["EntryCount"]
    for actual, retained in zip(inventory["Entries"], expected["Entries"]):
        assert (actual["Slot"], actual["LowId"], actual["HighId"], actual["Quality"], actual["Counter"]) == (
            retained["slot"], retained["template_low"], retained["template_high"], retained["quality"], retained["raw_u16_1"])
    transfers = json.loads((output / "MissionChestLootTransfers.json").read_text(encoding="utf-8"))["Records"]
    transfer = next(row for row in transfers if integer(row["Confirmation"]["GlobalOrdinal"]) == expected["Transfer"]["ConfirmationOrdinal"])
    assert (transfer["Handle"], transfer["Slot"], transfer["FamilyLowId"], transfer["FamilyHighId"], transfer["Quality"]) == (
        expected["Transfer"]["SourceHandle"], expected["Transfer"]["SourceSlot"], expected["Transfer"]["LowId"],
        expected["Transfer"]["HighId"], expected["Transfer"]["Quality"])
    assert [integer(row["GlobalOrdinal"]) for row in transfer["Requests"]] == [expected["Transfer"]["RequestOrdinal"]]
    assert result["LootRecords"] == 4 and result["VerifiedTransfers"] == 1
    receipt = {"Status": "PASS", "SameCorrelationImplementation": True, "LatestCorpusTotalsIncludeThisFixture": False,
               "FixtureSha256": sha(fixture_path), "DecodedFixtureSha256": sha(decoded_path), "Results": result,
               "Transfer": transfer, "OriginalExpected": expected}
    dump(work / "known-answer-correlation-validation.json", receipt)
    return {"KnownAnswer": "PASS", "Contents": result["LootRecords"], "Transfers": result["VerifiedTransfers"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("index", "correlate", "known-answer"), default="index")
    parser.add_argument("--captures")
    parser.add_argument("--work", required=True)
    parser.add_argument("--decoded")
    args = parser.parse_args()
    if args.mode == "index":
        if not args.captures:
            parser.error("--captures is required for indexing")
        result = index_corpus(args.captures, args.work)
    elif args.mode == "correlate":
        if not args.decoded:
            parser.error("--decoded is required for correlation")
        work = Path(args.work)
        result = correlate(work / "correlation", args.decoded, index_path=work / "capture-index.sqlite")
    else:
        result = validate_known_answer(args.work)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
