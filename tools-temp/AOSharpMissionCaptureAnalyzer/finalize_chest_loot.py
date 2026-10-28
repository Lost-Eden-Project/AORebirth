"""Finish offline chest recovery reports from indexed, decoded, classified evidence.

All inputs and outputs are explicit private working files. No game runtime,
allowlist, source capture or original historical catalog is modified.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path

from recover_chest_loot import correlate, dump, identity, integer, sha

OUTPUTS = (
    "MissionChestCaptureInventory.json", "MissionChestCapturedLoot.json",
    "MissionChestLootTransfers.json", "MissionChestUniqueItems.json",
    "MissionChestUnresolved.json", "MissionChestLootSummary.md",
)


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def cell(value):
    return str(value).replace("|", "\\|").replace("\n", " ")


def names(classification):
    return " / ".join(dict.fromkeys(row["Name"] for row in classification.get("Names", []) if row.get("Name")))


def category(classification):
    result = []
    for row in classification.get("CategoryEvidence", []):
        current = row.get("CurrentItemClass") or {}
        label = row.get("ExistingOrdinaryRkReviewCategory") or current.get("LiveItemClassName") or current.get("FileItemClassName")
        if label and label not in result:
            result.append(label)
    return " / ".join(result) or "UNKNOWN"


def snapshot_key(row):
    return (row["WorldId"], tuple((entry["Slot"], entry["LowId"], entry["HighId"], entry["Quality"], entry["Counter"])
                                  for entry in row["Entries"]))


def finish(work):
    work = Path(work).resolve()
    base = work / "correlation"
    inventory = load(base / "MissionChestCaptureInventory.json")
    raw_loot = load(base / "MissionChestCapturedLoot.json")["Records"]
    loot = load(work / "classified-loot.json")["Records"]
    transfers = load(base / "MissionChestLootTransfers.json")
    unresolved = load(base / "MissionChestUnresolved.json")
    definitions = load(work / "classification-items.json")
    descriptors = load(work / "classification-descriptors.json")
    classification = load(work / "classification-summary.json")
    template_rows = load(work / "world-template-classification.json")["Rows"]
    templates = {row["TemplateId"]: row for row in template_rows}
    sessions = load(work / "session-index.json")
    index = load(work / "index-summary.json")
    decoded_summary = load(work / "decoded-packets.jsonl.summary.json")

    # Classification is additive: every original field, identity and packet
    # reference must survive unchanged. Counts are derived, never inferred drops.
    raw_by_id = {row["LootId"]: row for row in raw_loot}
    by_id = {row["LootId"]: row for row in loot}
    assert len(raw_by_id) == len(raw_loot) == len(by_id) == len(loot)
    assert raw_by_id.keys() == by_id.keys()
    for lid, row in raw_by_id.items():
        assert all(by_id[lid][key] == value for key, value in row.items()), lid
    assert not classification["MissingDefinitionIds"]
    assert len(definitions["Items"]) == len({value for row in loot for value in (row["FamilyLowId"], row["FamilyHighId"])})

    worlds = {row["WorldId"]: row for row in inventory["Chests"]}
    for world in worlds.values():
        template = templates.get(world["ChestTemplateId"])
        world["TemplateClassificationEvidence"] = template
        if template and set(template["FunctionNames"]) == {"Set", "CastNano"}:
            world["WorldObjectRole"] = "WORLD_RIFT_WITH_TYPED_SET_CASTNANO_PROGRAM"
        elif template and not template["FunctionNames"] and template["Can"] == 8 and template["DynelType"] == 51017:
            world["WorldObjectRole"] = "WORLD_LOOT_CONTAINER"
        else:
            world["WorldObjectRole"] = "WORLD_CHEST_FAMILY_ROLE_UNRESOLVED"
        world["TemplateName"] = template["Name"] if template else None
        world["ContentsStatus"] = "VERIFIED_INVENTORY" if world["Inventories"] else "NO_INVENTORY_RESPONSE_RECORDED"
    rifts = {wid for wid, row in worlds.items() if row["WorldObjectRole"].startswith("WORLD_RIFT")}
    ordinary = {wid for wid, row in worlds.items() if row["WorldObjectRole"] == "WORLD_LOOT_CONTAINER"}
    unknown_worlds = set(worlds) - rifts - ordinary

    # Positive non-world evidence belongs in exclusions, not unresolved joins.
    excluded_inventories, excluded_transfers, remaining = [], [], []
    for row in unresolved["Records"]:
        if row["Kind"] == "NON_WORLD_OR_UNRESOLVED_INVENTORY":
            if row["Owner"]["Type"] == 51050:
                row["ExclusionReason"] = "CORPSE_IDENTITY_TYPE"
            elif row["Reason"] == "CHARACTER_OWNED_CONTAINER":
                row["ExclusionReason"] = "EXACT_CHARACTER_PARENT_IN_NATIVE_FULL_UPDATE"
            else:
                remaining.append(row)
                continue
            row["InventoryId"] = row["Source"]["Session"] + "|inventory:" + str(row["Source"]["GlobalOrdinal"])
            excluded_inventories.append(row)
        elif row["Kind"] == "TRANSFER_FROM_OTHER_LOCATION":
            row["ExclusionReason"] = "SOURCE_LOCATION_TYPE_IS_NOT_CONTAINER_HANDLE_SLOT_107"
            excluded_transfers.append(row)
        else:
            remaining.append(row)
    excluded_ids = {row["InventoryId"]: row for row in excluded_inventories}
    still_unresolved = []
    for row in remaining:
        if row["Kind"] == "TRANSFER_WITHOUT_VERIFIED_WORLD_SLOT" and row.get("OwnerInventory") in excluded_ids:
            owner = excluded_ids[row["OwnerInventory"]]
            row["ExclusionReason"] = owner["ExclusionReason"]
            row["Owner"] = owner["Owner"]
            excluded_transfers.append(row)
        else:
            still_unresolved.append(row)

    rift_inventories, recovered = [], []
    for row in inventory["Inventories"]:
        world = worlds[row["WorldId"]]
        row["WorldObjectRole"] = world["WorldObjectRole"]
        row["OpenRequestAssociation"] = "PRECEDING_EXACT_WORLD_OWNER_ACTOR_CANDIDATES_NOT_UNIQUE_REQUEST_PROOF"
        (rift_inventories if row["WorldId"] in rifts else recovered).append(row)
    assert not any(row["LootIds"] for row in rift_inventories), "Rift contents require separate reporting rather than omission"
    inventory["Inventories"] = recovered
    inventory["ExcludedRiftInventories"] = rift_inventories
    inventory["ExcludedNonWorldInventories"] = excluded_inventories

    for row in loot:
        world = worlds[row["WorldId"]]
        row["Names"] = row["Classification"]["Names"]
        row["CategoryEvidence"] = row["Classification"]["CategoryEvidence"]
        row["WorldObjectRole"] = world["WorldObjectRole"]
        assert row["WorldId"] not in rifts
    for row in transfers["Records"]:
        source_loot = by_id[row["LootId"]]
        assert (row["FamilyLowId"], row["FamilyHighId"], row["Quality"]) == (
            source_loot["FamilyLowId"], source_loot["FamilyHighId"], source_loot["Quality"])
        row["Names"] = source_loot["Classification"]["Names"]
    transfers["ExcludedNonWorldOrOtherLocationNotifications"] = sorted(
        excluded_transfers, key=lambda row: (row["Source"]["Session"], integer(row["Source"]["GlobalOrdinal"]) or 0))

    grouped = defaultdict(list)
    for row in loot:
        grouped[(row["FamilyLowId"], row["FamilyHighId"])].append(row)
    descriptor_by_pair = {(row["FamilyLowId"], row["FamilyHighId"]): row for row in descriptors["Pairs"]}
    pair_rows = []
    transfer_ids = {row["LootId"] for row in transfers["Records"]}
    for pair, observations in sorted(grouped.items()):
        row = dict(descriptor_by_pair[pair])
        row["ObservationCount"] = len(observations)
        row["WorldObservationCount"] = len({value["WorldId"] for value in observations})
        row["DistinctRuntimeChestIdentities"] = len({identity(value["ChestIdentity"]) for value in observations})
        row["VerifiedTransferCount"] = sum(value["LootId"] in transfer_ids for value in observations)
        row["DistinctChestSlotDescriptorCount"] = len({(value["WorldId"], value["Slot"], value["Quality"], value["QuantityRaw"]) for value in observations})
        row["Qualities"] = [{"Quality": ql, "Observations": sum(value["Quality"] == ql for value in observations),
            "LootIds": [value["LootId"] for value in observations if value["Quality"] == ql]}
            for ql in sorted({value["Quality"] for value in observations})]
        pair_rows.append(row)

    interactions = [event for world in worlds.values() for event in world["Interactions"]]
    container_interactions = [event for event in interactions if event["WorldId"] not in rifts]
    unique_slot_descriptors = {(row["WorldId"], row["Slot"], row["FamilyLowId"], row["FamilyHighId"], row["Quality"], row["QuantityRaw"]) for row in loot}
    summary = {
        "Sessions": len(sessions), "FinalizedSessions": sum(bool(row["Metadata"].get("finalized")) for row in sessions),
        "IncompleteSessions": sum(not row["Metadata"].get("finalized") for row in sessions),
        "RawPackets": index["RawRows"], "RawChestFamilyUpdates": len(inventory["ChestFamilyRecords"]),
        "ParentlessChestFamilyUpdates": sum(identity(row["Parent"]) == (0, 0) for row in inventory["ChestFamilyRecords"]),
        "CharacterOwnedChestFamilyUpdates": sum(row["Class"] == "CHARACTER_OWNED_CONTAINER" for row in inventory["ChestFamilyRecords"]),
        "DecodedSelectedPackets": decoded_summary["Decoded"], "DecoderFailures": decoded_summary["Failures"],
        "WorldFamilyObservations": len(worlds), "DistinctWorldFamilyIdentities": len({identity(row["ChestIdentity"]) for row in worlds.values()}),
        "SpawnVerifiedWorldLootContainers": sum(bool(worlds[wid]["SpawnEvidence"]) for wid in ordinary),
        "MemoryOnlyWorldFamilyObservations": sum(not row["SpawnEvidence"] for row in worlds.values()),
        "WorldObservationsWithUnresolvedRole": len(unknown_worlds),
        "WorldRiftsWithExactTypedTemplateEvidence": len(rifts),
        "NonRiftWorldObservationsIncludingIncompleteRole": len(worlds) - len(rifts),
        "AllWorldFamilyInteractions": len(interactions), "NonRiftWorldInteractions": len(container_interactions),
        "DirectUse3AllWorldFamily": sum(row["Action"] == 3 for row in interactions),
        "HeldItemUse5AllWorldFamily": sum(row["Action"] == 5 for row in interactions),
        "AcknowledgedWorldFamilyInteractions": sum(bool(row["Acknowledgements"]) for row in interactions),
        "UniqueSuccessfulOpeningCount": None,
        "OpeningCountLimitation": "Use callbacks include closing/tool uses; report exact interactions and open-flag contents responses separately",
        "RecoveredContainerInventories": len(recovered), "ContainerOwnersWithInventories": len({row["WorldId"] for row in recovered}),
        "RecoveredAllWorldFamilyInventories": len(recovered) + len(rift_inventories), "ExcludedEmptyRiftInventories": len(rift_inventories),
        "EmptyContainerInventoryResponses": sum(not row["LootIds"] for row in recovered),
        "DistinctContainerSnapshotSignatures": len({snapshot_key(row) for row in recovered}),
        "LootObservationRecords": len(loot), "DistinctChestSlotDescriptors": len(unique_slot_descriptors),
        "RepeatedChestSlotDescriptorObservations": len(loot) - len(unique_slot_descriptors),
        "UniqueItemDescriptorPairs": len(pair_rows), "DistinctEndpointDefinitionIds": len(definitions["Items"]),
        "ExactFixedTemplateIds": len({row["TemplateId"] for row in loot if row["TemplateId"] is not None}),
        "QualityMinimum": min(row["Quality"] for row in loot), "QualityMaximum": max(row["Quality"] for row in loot),
        "DistinctCapturedQualities": sorted({row["Quality"] for row in loot}),
        "VerifiedTransfers": len(transfers["Records"]), "VerifiedFullTransferChains": sum(bool(row["Requests"]) for row in transfers["Records"]),
        "ExcludedNonWorldInventoryResponses": len(excluded_inventories), "ExcludedAddItemNotifications": len(excluded_transfers),
        "UnresolvedProtocolRecordsByKind": dict(sorted(Counter(row["Kind"] for row in still_unresolved).items())),
        "NonRiftWorldObservationsWithoutContents": sum(not row["Inventories"] and wid not in rifts for wid, row in worlds.items()),
    }
    session_rows = []
    for session in sessions:
        sid = session["Session"]
        sw = [row for row in worlds.values() if row["Session"] == sid]
        sl = [row for row in loot if row["Session"] == sid]
        session_rows.append({"Session": sid, "Finalized": bool(session["Metadata"].get("finalized")),
            "RawPackets": session["raw_rows"], "MemoryDynelRows": session["memory_rows"],
            "SpawnVerifiedLootContainers": sum(row["WorldId"] in ordinary for row in sw),
            "MemoryOnlyWorldObservations": sum(not row["SpawnEvidence"] for row in sw),
            "WorldObservationsWithUnresolvedRole": sum(row["WorldId"] in unknown_worlds for row in sw),
            "Rifts": sum(row["WorldId"] in rifts for row in sw),
            "Interactions": sum(len(row["Interactions"]) for row in sw),
            "RecoveredContainerInventories": sum(row["Session"] == sid for row in recovered),
            "LootObservations": len(sl), "DistinctDescriptorPairs": len({(row["FamilyLowId"], row["FamilyHighId"]) for row in sl}),
            "Transfers": sum(row["Confirmation"]["Session"] == sid for row in transfers["Records"]),
            "OriginalMetadata": session["Metadata"]})
    inventory["SummaryBeforeWorldRoleSeparation"] = inventory["Summary"]
    inventory["Summary"] = summary
    inventory["Sessions"] = session_rows
    inventory["SourceManifest"] = {"Path": str(work / "source-manifest.json"), "Sha256": sha(work / "source-manifest.json")}
    inventory["ContextCaution"] = "Native FullUpdate playfield and exact world identity are authoritative; raw callback context may lag. Model and room observations retain their original memory provenance."
    inventory["ProtocolCaution"] = "Lockable identity list is an authorization list, not trap-child identities."
    dump(work / OUTPUTS[0], inventory)
    dump(work / OUTPUTS[1], {"Schema": 1, "Summary": summary, "ClassificationSummary": classification,
        "CountingSemantics": "Each exact inventory slot appearance is preserved. Repeated appearances and descriptor/QL variants are not distinct physical items.", "Records": loot})
    transfers["Summary"] = {"VerifiedTransfers": len(transfers["Records"]), "ExcludedNotifications": len(excluded_transfers),
        "AllContainerAddItemNotificationsAccountedFor": len(transfers["Records"]) + len(excluded_transfers),
        "QuantitySemantics": "Native source counter preserved; transferred physical quantity is unrecorded"}
    dump(work / OUTPUTS[2], transfers)
    dump(work / OUTPUTS[3], {"Schema": 1, "Summary": summary, "ItemDescriptorPairs": pair_rows,
        "EndpointDefinitions": definitions["Items"],
        "IdentitySemantics": "A captured LowId/HighId/QL descriptor is preserved without guessing a single effective template. Exact fixed TemplateId exists only where LowId equals HighId. Endpoint definitions are not physical item counts."})
    dump(work / OUTPUTS[4], {"Schema": 1, "Records": still_unresolved,
        "NoRecordedContents": [{"WorldId": wid, "Identity": row["ChestIdentity"], "Role": row["WorldObjectRole"],
            "Reason": "NO_EXACT_OWNER_INVENTORY_RESPONSE_IN_PROVIDED_SESSIONS"}
            for wid, row in worlds.items() if not row["Inventories"] and wid not in rifts],
        "ClassificationCoverage": {"HistoricalUnknownOnlyLootIds": [row["LootId"] for row in loot if not row["Classification"]["HistoricalContentAssociations"]],
            "Conflicts": classification["Conflicts"], "MissingCurrentDefinitionIds": classification["MissingDefinitionIds"],
            "MissingHistoricalCatalogIds": classification["MissingHistoricalCatalogIds"],
            "MissingRequirementCatalogIds": classification["MissingRequirementCatalogIds"],
            "NotCurrentlyAllowedLootIds": [row["LootId"] for row in loot if not row["Classification"]["CurrentRkChestPolicyAllows"]]},
        "Limitations": ["Memory-only world observations have no same-session native spawn, parent or template; no role is inferred from their names.",
            "No contents response means unknown contents, not an empty container.",
            "Use3 and held-item Use5 include operations other than successful opening; candidate request links are not unique causal proof.",
            "Raw stack/charge counter is retained without inventing physical units or transfer quantity.",
            "Historical UNKNOWN and absent expansion requirements do not authorize ordinary RK chest generation."]})

    write_summary(work, summary, classification, session_rows, pair_rows, transfers["Records"], by_id)
    return summary


def write_summary(work, summary, classification, sessions, pairs, transfers, loot_by_id):
    s, c = summary, classification
    fixture = load(work / "MissionChestKnownAnswerFixture.json")
    known = fixture["Expected"]
    index_manifest = load(work / "item-classification-index.sqlite.manifest.json")
    selected = load(work / "decoded-packets.jsonl.summary.json")
    integration = c["EvidenceIntegration"]
    inventory = load(work / OUTPUTS[0])
    exclusions = Counter(row["ExclusionReason"] for row in inventory["ExcludedNonWorldInventories"])
    excluded_transfers = load(work / OUTPUTS[2])["ExcludedNonWorldOrOtherLocationNotifications"]
    transfer_exclusions = Counter(row["ExclusionReason"] for row in excluded_transfers)
    conflicts = []
    for conflict in c["Conflicts"]:
        observed = loot_by_id[conflict["LootId"]]
        conflicts.append(f'{observed["FamilyLowId"]}/{observed["FamilyHighId"]}, {names(observed["Classification"])}, QL{observed["Quality"]}: {", ".join(conflict["Conflicts"])}; source {observed["LootId"]}')
    lines = ["# Mission chest loot recovery", "",
        f'PROVEN from the supplied completed evidence: every selected packet decodes, every recovered slot retains an exact chest-owner inventory association, and all recovered definition IDs resolve in the current {index_manifest["Counts"]["CurrentDefinitions"]:,}-definition database. This is offline extraction; runtime loot selection, approvals, rarity, QL, GameData, original captures, databases and services were not changed.', "",
        "## Recovery totals", "", "| Measure | Result |", "| --- | --- |"]
    labels = [("Sessions processed (including incomplete finalization)", f'{s["Sessions"]} ({s["IncompleteSessions"]} incomplete)'),
        ("Raw packet records", s["RawPackets"]), ("Chest-family FullUpdates, all decoded", s["RawChestFamilyUpdates"]),
        ("Parentless / character-owned FullUpdates", f'{s["ParentlessChestFamilyUpdates"]} / {s["CharacterOwnedChestFamilyUpdates"]}'),
        ("Spawn-proven world loot containers", s["SpawnVerifiedWorldLootContainers"]),
        ("Additional memory-only world observations, role unresolved", s["MemoryOnlyWorldFamilyObservations"]),
        ("Separately identified world rifts", s["WorldRiftsWithExactTypedTemplateEvidence"]),
        ("All world-family observations / distinct runtime identities", f'{s["WorldFamilyObservations"]} / {s["DistinctWorldFamilyIdentities"]}'),
        ("World-family Use interactions / exact acknowledgements", f'{s["AllWorldFamilyInteractions"]} / {s["AcknowledgedWorldFamilyInteractions"]}'),
        ("Use3 / held-item Use5", f'{s["DirectUse3AllWorldFamily"]} / {s["HeldItemUse5AllWorldFamily"]}'),
        ("Non-rift interactions", s["NonRiftWorldInteractions"]),
        ("Recovered loot-container contents responses / owners", f'{s["RecoveredContainerInventories"]} / {s["ContainerOwnersWithInventories"]}'),
        ("Separate empty rift responses", s["ExcludedEmptyRiftInventories"]),
        ("Empty loot-container responses", s["EmptyContainerInventoryResponses"]),
        ("Individual slot appearances", s["LootObservationRecords"]),
        ("Distinct same-session chest/slot descriptors / repeated appearances", f'{s["DistinctChestSlotDescriptors"]} / {s["RepeatedChestSlotDescriptorObservations"]}'),
        ("Unique LowId/HighId pairs", s["UniqueItemDescriptorPairs"]),
        ("Distinct endpoint definition IDs", s["DistinctEndpointDefinitionIds"]),
        ("Exact fixed-template IDs where low equals high", s["ExactFixedTemplateIds"]),
        ("Observed item QL range", f'{s["QualityMinimum"]}–{s["QualityMaximum"]}'),
        ("Verified full transfer chains", s["VerifiedFullTransferChains"]),
        ("Decoder failures remaining", s["DecoderFailures"])]
    lines.extend(f"| {cell(label)} | {cell(value)} |" for label, value in labels)
    lines += ["", "The requested opening count cannot be equated to all Use commands: requests include closing and held-item/tool operations. The exact counts above separate observed interactions from open-flag contents responses. No unopened/unrecorded container is assumed empty. Memory-only observations with exact-owner inventory responses retain those contents; their missing native spawn does not erase the verified slots.", "",
        "## What failed and what was corrected", "",
        f'PROVEN: the recorder\'s supplied AOSharp.Common ChestFullUpdate model omitted the conditional 28-byte coordinate/heading region for parentless objects and the final chest marker. The {s["ParentlessChestFamilyUpdates"]:,} previously failing parentless records contain those transforms; the {s["CharacterOwnedChestFamilyUpdates"]:,} formerly successful records are character-owned containers without that region. The offline reader now follows the recovered common FullUpdate prefix, conditional parent locality, lockable marker, integer, scaled authorization-identity list and chest marker, and requires complete byte consumption. No speculative custom tail was introduced. Every current opaque blob and authorization list is empty; a nonempty-blob explanation was disproven.', "",
        f'All {selected["Decoded"]:,} selected messages decode: {", ".join(f"{count:,} {family}" for family, count in sorted(selected["Families"].items()))}. Original OUT GenericCmd/move frames with a recorded zero header length are explicitly marked as captured; incoming length checks remain strict. The source headers are never rewritten.', "",
        f'World status requires exact native identity, parent/locality and transform plus same-session world-memory identity/playfield. Exact template IDs and existing typed Set/CastNano programs distinguish {s["WorldRiftsWithExactTypedTemplateEvidence"]} rifts from loot containers; names alone do not. All {s["RawChestFamilyUpdates"]:,} native updates remain in the machine-readable inventory. Positively excluded inventory responses: {dict(exclusions)}. All add notifications are accounted for: {s["VerifiedTransfers"]} verified world transfers; excluded confirmations by exact reason: {dict(transfer_exclusions)}.', "",
        "## Exact joins and validation", "",
        "InventoryUpdate.Owner binds each contents response to its world object. Session, recipient, current handle registration and exact slot bind type107 source locations. Handle reassignment supersedes prior inventories. Transfer requests must match the active inventory epoch and destination; confirmations identify the same recipient. Timestamps alone never establish item ownership. Native playfield IDs take priority over stale recorder context. Room/floor and optional memory transforms remain separately sourced observations.", "",
        f'The original known-answer chest {known["ChestIdentity"][0]}:{known["ChestIdentity"][1]} in {fixture["CaptureSession"]} passes the same decoder and correlator: {known["EntryCount"]} slots at contents ordinal{known["ContentsOrdinal"]}, handle{known["Handle"]}, and item{known["Transfer"]["LowId"]} QL{known["Transfer"]["Quality"]} from slot{known["Transfer"]["SourceSlot"]} through request{known["Transfer"]["RequestOrdinal"]} and confirmation{known["Transfer"]["ConfirmationOrdinal"]}. The fixture is separate and excluded from latest-corpus totals. Completed AO Stripdown Mission Dungeon Interactives Recovery001–003 and their machine-readable evidence were reused; no Ghidra work or new captures were run.', "",
        "Decoder output was replayed with byte-identical results. Final reports are generated twice from the cached index and decoded records and compared by SHA256. Every original capture file is checked against the retained pre-extraction source manifest. See final-validation.json for actual check results and output hashes. Independent join/coverage audits, decoder-validation.json, known-answer-correlation-validation.json and classification-final-review.json retain separate evidence.", "",
        "## Classification and catalog integration", "", "| Existing classification axis | Loot observations |", "| --- | --- |",
        f'| Historical Rubi-Ka association | {c["HistoricalRubiKaAssociationLootRows"]} |',
        f'| Historical UNKNOWN only | {c["HistoricalUnknownOnlyLootRows"]} |',
        f'| Expansion requirement association | {c["ExpansionRequirementLootRows"]} |',
        f'| Seasonal / Halloween / quest-progression / special exclusion | {c["SeasonalExcludedLootRows"]} / {c["HalloweenExcludedLootRows"]} / {c["QuestProgressionExcludedLootRows"]} / {c["SpecialExcludedLootRows"]} |',
        f'| Current ordinary RK exact-pair and QL approval | {c["CurrentRkChestPolicyAllowsLootRows"]} |',
        f'| Not currently approved | {s["LootObservationRecords"] - c["CurrentRkChestPolicyAllowsLootRows"]} |', "",
        f'These axes overlap. All recovered definitions lack an explicit expansion requirement, so the requirement-only axis remains UNKNOWN for all observations; absence is not proof of RK origin. Historical associations are retained separately. All {s["DistinctEndpointDefinitionIds"]} endpoint IDs occur in current item definitions and both existing classification catalogs. None is missing. Historical classification is UNKNOWN for {c["EndpointHistoricalBuckets"].get("Unknown", 0)} endpoints. Endpoint-history disagreements: {"; ".join(conflicts) or "None"}. These are coverage conflicts, not proof of expansion-exclusive drops.', "",
        f'The additive catalog version catalog-integration/HistoricalEvidence.captured-20261028.json retains all {integration["PreviousAssertionsPreserved"]:,} previous assertions and {integration["PreviousSourcesPreserved"]:,} sources, adds {integration["AppendedSources"]} exact capture sources and {integration["AppendedAssertions"]} endpoint acquisition assertions, and preserves the original catalog. Endpoint assertions are not additional physical drops or approvals. The existing three runtime policies are unchanged. Captured acquisition evidence does not automatically grant ordinary RK eligibility.', "",
        "## Remaining limits", "",
        f'{s["MemoryOnlyWorldFamilyObservations"]} world-memory observations lack same-session native spawn, parent and template. {s["NonRiftWorldObservationsWithoutContents"]} non-rift world observations have no exact-owner contents response; their contents are unknown. There are no undecoded selected packets or unexplained recovered inventory-slot associations. Repeated views remain recorded and are not counted as distinct physical items. QuantityRaw is the native stack/charge counter; transferred physical quantity and unit conversion remain UNVERIFIED. Item names below are endpoint names, not a guessed interpolated display name. No drop probabilities can be inferred from this sample.', "",
        "## Per-session coverage", "", "| Session | Finalized | Raw | Loot containers | Memory only | Rifts | Uses | Inventories | Loot rows | Pairs | Transfers |", "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for row in sessions:
        lines.append("| " + " | ".join(cell(row[key]) for key in ("Session", "Finalized", "RawPackets", "SpawnVerifiedLootContainers", "MemoryOnlyWorldObservations", "Rifts", "Interactions", "RecoveredContainerInventories", "LootObservations", "DistinctDescriptorPairs", "Transfers")) + " |")
    lines += ["", "## Verified transfers", "", "| Item endpoint names | Low / high | QL | Chest | Session / contents ordinal / slot | Request → confirmation |", "| --- | --- | ---: | --- | --- | --- |"]
    for row in transfers:
        observed = loot_by_id[row["LootId"]]
        lines.append("| " + " | ".join(cell(value) for value in (
            names(observed["Classification"]), f'{row["FamilyLowId"]} / {row["FamilyHighId"]}', row["Quality"],
            f'{row["ChestIdentity"]["Type"]}:{row["ChestIdentity"]["Instance"]}',
            f'{observed["Session"]} / {observed["InventoryOrdinal"]} / {row["Slot"]}',
            f'{", ".join(str(value["GlobalOrdinal"]) for value in row["Requests"])} → {row["Confirmation"]["GlobalOrdinal"]}')) + " |")
    lines += ["", "## Complete recovered item list", "",
        "Each row is one exact low/high descriptor pair, with captured QL variants and every appearance preserved in JSON. Endpoint definitions are listed separately in MissionChestUniqueItems.json. Observation/chest counts do not assert distinct physical units.", "",
        "| Endpoint item names | Low / high | Captured QLs | Category evidence | Observations | Chest observations | Historical association | Currently allowed at observed QLs |", "| --- | --- | --- | --- | ---: | ---: | --- | --- |"]
    for row in pairs:
        classifications = row["ClassificationByQuality"]
        associations = sorted({value for cl in classifications for value in cl["HistoricalContentAssociations"]})
        allowed = [str(cl["Quality"]) for cl in classifications if cl["CurrentRkChestPolicyAllows"]]
        lines.append("| " + " | ".join(cell(value) for value in (
            names(row), f'{row["FamilyLowId"]} / {row["FamilyHighId"]}', ", ".join(map(str, row["ObservedQualities"])),
            category(row), row["ObservationCount"], row["WorldObservationCount"], ", ".join(associations) or "UNKNOWN",
            ", ".join(allowed) or "No")) + " |")
    lines += ["", "## Outputs and provenance", ""]
    lines.extend(f'- [{filename}]({filename})' for filename in OUTPUTS)
    lines += ["- [Updated additive historical catalog](catalog-integration/HistoricalEvidence.captured-20261028.json)",
        "- [Validation receipt and report hashes](final-validation.json)", "",
        "All outputs are private derived research under this directory, outside canonical GameData. Public changes contain offline extraction tooling only. No master merge, deployment, server restart, client operation or live database modification was performed.", ""]
    (work / OUTPUTS[5]).write_text("\n".join(lines), encoding="utf-8", newline="\n")


def validate(work, summary):
    work = Path(work).resolve()
    original_manifest = load(work / "source-manifest.json")
    root = Path(load(work / "index-summary.json")["OriginalCaptureRoot"])
    current_files = {str(path.relative_to(root)) for path in root.rglob("*") if path.is_file()}
    expected_files = {str(Path(row["Path"])) for row in original_manifest}
    assert current_files == expected_files, "Original capture file set changed"
    checked = 0
    for row in original_manifest:
        path = root / row["Path"]
        assert path.stat().st_size == row["Bytes"] and sha(path) == row["Sha256"], str(path)
        checked += 1
    decoder = load(work / "decoded-packets.jsonl.summary.json")
    assert decoder["Failures"] == 0 and decoder["Rows"] == decoder["Decoded"]
    assert sha(work / "decoded-packets.jsonl") == sha(work / "decoded-packets-repeat.jsonl")
    known = load(work / "known-answer-correlation-validation.json")
    assert known["Status"] == "PASS" and known["SameCorrelationImplementation"]
    assert known["LatestCorpusTotalsIncludeThisFixture"] is False
    assert sha(work / "MissionChestKnownAnswerFixture.json") == known["FixtureSha256"]
    assert sha(work / "known-answer-decoded.jsonl") == known["DecodedFixtureSha256"]
    assert load(work / "classification-validation.json")["NoRuntimePolicyChanges"]
    class_review = load(work / "classification-final-review.json")
    assert all(class_review[key] for key in ("AllSourceFieldsPreserved", "EachRecoveredLootIdHasCaptureSource",
        "GenerationApprovalsUnchanged", "SourceLootIdsUniqueAndAligned"))
    class_manifest = load(work / "item-classification-index.sqlite.manifest.json")
    protected = {key: row for key, row in class_manifest["Sources"].items()
                 if key.startswith("policies/") or key in ("items.dat", "ItemTemplates.json", "HistoricalEvidence.json")}
    for key, row in protected.items():
        assert sha(row["Path"]) == row["Sha256"], "Protected catalog/policy input changed: " + key
    assert sha(work / "catalog-integration" / "HistoricalEvidence.captured-20261028.json") == class_review["MergedHistoricalEvidenceSha256"]
    base = work / "correlation"
    base_names = (OUTPUTS[0], OUTPUTS[1], OUTPUTS[2], OUTPUTS[4], "correlation-summary.json")
    initial_base = {name: sha(base / name) for name in base_names}
    correlate(base, work / "decoded-packets.jsonl", index_path=work / "capture-index.sqlite")
    assert initial_base == {name: sha(base / name) for name in base_names}, "Correlations are not deterministic"
    first = {name: sha(work / name) for name in OUTPUTS}
    repeated = finish(work)
    second = {name: sha(work / name) for name in OUTPUTS}
    assert first == second and summary == repeated, "Reports are not deterministic"
    receipt = {"Status": "PASS_OFFLINE_RECOVERY", "OriginalCaptureFilesUnchanged": checked,
        "OriginalSourceManifestSha256": sha(work / "source-manifest.json"),
        "OriginalFileSetUnchanged": True, "DecodedOutputRepeatedByteIdentical": True,
        "CorrelationsRepeatedByteIdentical": True, "ProtectedInputSha256Unchanged": {key: row["Sha256"] for key, row in protected.items()},
        "AllSixReportsRepeatedByteIdentical": True, "OutputSha256": first,
        "KnownAnswerSamePipeline": "PASS", "KnownAnswerIncludedInLatestCorpus": False,
        "AllRecoveredDefinitionIdsFound": True, "AllClassifiedSourceFieldsPreserved": True,
        "SourceSha256": {"finalize_chest_loot.py": sha(__file__),
            "recover_chest_loot.py": sha(Path(__file__).with_name("recover_chest_loot.py"))},
        "ProductionRuntimeChanges": False, "RuntimePolicyChanges": False, "Summary": summary}
    dump(work / "final-validation.json", receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", required=True)
    parser.add_argument("--validate", action="store_true", help="Hash originals and require deterministic reports and retained validation receipts")
    args = parser.parse_args()
    result = finish(args.work)
    if args.validate:
        receipt = validate(args.work, result)
        result = {"Status": receipt["Status"], "OriginalFilesUnchanged": receipt["OriginalCaptureFilesUnchanged"], "Summary": result}
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
