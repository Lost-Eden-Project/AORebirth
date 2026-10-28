"""Join captured chest descriptors to existing private classifications without approving loot.

The index and evidence overlay are offline research artifacts. They must never be
placed in canonical GameData or published with the product source.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
from typing import Iterable

SCHEMA_VERSION = 1
EXPANSION_BUCKETS = (
    "RubiKa", "Shadowlands", "AlienInvasion", "LostEden", "Xan", "NotumWars",
    "PMVObsidianEdition", "MultipleExpansions", "Unknown",
)
HISTORICAL_BUCKETS = (
    "RubiKa", "Shadowlands", "AlienInvasion", "LostEden", "Xan", "NotumWars",
    "OtherContent", "MultipleExpansions", "Unknown",
)


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def read_records(path: Path):
    value = read_json(path)
    records = value.get("Records") if isinstance(value, dict) else value
    if not isinstance(records, list) or any(not isinstance(row, dict) for row in records):
        raise ValueError("Expected a record array or a capture catalog containing Records")
    return records


def digest(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def canonical(value) -> str:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":"))


def write_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_suffix(path.suffix + ".partial")
    partial.write_text(json.dumps(value, ensure_ascii=True, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    partial.replace(path)


def array_records(path: Path):
    """Read one existing top-level JSON array without loading its provenance tails together."""
    decoder = json.JSONDecoder()
    with path.open(encoding="utf-8-sig") as source:
        data, position, ended = "", 0, False

        def extend():
            nonlocal data, position, ended
            data = data[position:]
            position = 0
            block = source.read(1024 * 1024)
            ended = not block
            data += block

        extend()
        while position < len(data) and data[position].isspace():
            position += 1
        if position >= len(data) or data[position] != "[":
            raise ValueError(f"Expected a top-level catalog array: {path}")
        position += 1
        while True:
            while True:
                while position < len(data) and (data[position].isspace() or data[position] == ","):
                    position += 1
                if position < len(data):
                    break
                if ended:
                    raise ValueError(f"Unterminated catalog array: {path}")
                extend()
            if data[position] == "]":
                position += 1
                if data[position:].strip() or source.read().strip():
                    raise ValueError(f"Unexpected data after catalog array: {path}")
                return
            while True:
                try:
                    row, position = decoder.raw_decode(data, position)
                    break
                except json.JSONDecodeError:
                    if ended:
                        raise
                    if len(data) - position > 32 * 1024 * 1024:
                        raise ValueError(f"Catalog record exceeds the bounded reader limit: {path}")
                    extend()
            if not isinstance(row, dict):
                raise ValueError(f"Catalog entry is not an object: {path}")
            yield row
            if position > 1024 * 1024:
                extend()


def input_paths(metadata_root: Path, archive: Path, gamedata: Path):
    paths = {
        "items.dat": gamedata / "items.dat",
        "ItemTemplates.json": gamedata / "ItemTemplates.json",
        "item-metadata.jsonl": metadata_root / "item-metadata.jsonl",
        "item-metadata-source.json": metadata_root / "item-metadata-source.json",
        "ordinary-rk-review-results.json": metadata_root / "ordinary-rk-review-results.json",
        "negative-count-summary.json": metadata_root / "negative-count-summary.json",
        "ClassificationSummary.json": archive / "ClassificationSummary.json",
        "HistoricalSummary.json": archive / "HistoricalReconstruction" / "catalogs" / "Summary.json",
        "HistoricalEvidence.json": archive / "HistoricalReconstruction" / "research" / "HistoricalEvidence.json",
    }
    for name in EXPANSION_BUCKETS:
        paths["requirements/" + name + ".json"] = archive / (name + ".json")
    for name in HISTORICAL_BUCKETS:
        paths["historical/" + name + ".json"] = archive / "HistoricalReconstruction" / "catalogs" / (name + ".json")
    for name in ("MissionChestRKAllowlist.json", "MissionChestSeasonalExclusions.json", "MissionChestSpecialExclusions.json"):
        paths["policies/" + name] = gamedata / "Loot" / name
    return paths


def build_index(metadata_root: Path, archive: Path, gamedata: Path, destination: Path):
    metadata_root, archive, gamedata, destination = (path.resolve() for path in (metadata_root, archive, gamedata, destination))
    if destination.is_relative_to(gamedata) or destination.is_relative_to(archive):
        raise ValueError("Research lookup must stay outside canonical GameData and the preserved classification archive")
    paths = input_paths(metadata_root, archive, gamedata)
    sources = {name: {"Path": str(path), "Sha256": digest(path)} for name, path in sorted(paths.items())}
    receipt = read_json(paths["item-metadata-source.json"])
    if receipt["SourceSha256"] != sources["items.dat"]["Sha256"] or receipt["OutputSha256"] != sources["item-metadata.jsonl"]["Sha256"]:
        raise ValueError("Existing metadata export is not an exact match for current items.dat")
    requirement_summary = read_json(paths["ClassificationSummary.json"])
    history_summary = read_json(paths["HistoricalSummary.json"])
    for summary in (requirement_summary, history_summary):
        if summary["ItemsDatSha256"] != sources["items.dat"]["Sha256"] or summary["ItemTemplatesSha256"] != sources["ItemTemplates.json"]["Sha256"]:
            raise ValueError("Classification source identity differs from the current item/family database")
    manifest_identity = {"SchemaVersion": SCHEMA_VERSION, "SourceFiles": sources, "ToolSha256": digest(Path(__file__))}
    fingerprint = hashlib.sha256(canonical(manifest_identity).encode()).hexdigest()
    cache_receipt = destination.with_suffix(destination.suffix + ".manifest.json")
    if destination.exists():
        old = read_json(cache_receipt)
        if old.get("InputFingerprint") != fingerprint or old.get("IndexSha256") != digest(destination):
            raise ValueError("Existing lookup cache has different/corrupt inputs; choose a versioned destination")
        with sqlite3.connect("file:" + destination.as_posix() + "?mode=ro", uri=True) as db:
            if db.execute("PRAGMA quick_check").fetchone()[0] != "ok":
                raise ValueError("Lookup cache structural validation failed")
        return old
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    if partial.exists():
        raise ValueError("A partial classification index exists; retain it and choose a new destination")
    db = sqlite3.connect(partial)
    counts = {}
    try:
        db.executescript("""
            PRAGMA journal_mode=DELETE;
            CREATE TABLE metadata(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE requirements(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE history(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE exclusions(id INTEGER NOT NULL, catalog TEXT NOT NULL, ordinal INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX exclusions_id ON exclusions(id);
            CREATE TABLE approvals(low INTEGER NOT NULL, high INTEGER NOT NULL, minimum INTEGER NOT NULL, maximum INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX approvals_pair ON approvals(low,high);
            CREATE TABLE reviews(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE seasons(id INTEGER NOT NULL, season TEXT NOT NULL);
            CREATE INDEX seasons_id ON seasons(id);
            CREATE TABLE source_manifest(payload TEXT NOT NULL);
        """)
        metadata_keys = ("Id", "Name", "Quality", "Flags", "ItemType", "DynelType", "Stats", "NamedStats", "LiveItemClass", "NanoTemplate", "Criteria", "RdbPresent", "ReadComplete", "BytesLeft", "ReadStopReason", "FunctionNames")
        count = 0
        with paths["item-metadata.jsonl"].open(encoding="utf-8-sig") as stream:
            for count, line in enumerate(stream, 1):
                item = json.loads(line)
                value = {key: item.get(key) for key in metadata_keys}
                value["MetadataSourceRecord"] = count
                db.execute("INSERT INTO metadata VALUES (?,?)", (item["Id"], canonical(value)))
        if count != receipt["RecordCount"] or count != 131807:
            raise ValueError("Metadata record count differs from the required current database")
        counts["CurrentDefinitions"] = count
        requirement_total = 0
        for bucket in EXPANSION_BUCKETS:
            for item in array_records(paths["requirements/" + bucket + ".json"]):
                value = {key: item.get(key) for key in ("ExpansionClassification", "VerifiedExpansions", "OriginalClassificationValue", "SourceField", "ClassificationConfidence", "ClassificationReason", "ClassificationEvidence")}
                value["Bucket"] = bucket
                value["CatalogSource"] = "requirements/" + bucket + ".json"
                db.execute("INSERT INTO requirements VALUES (?,?)", (item["TemplateId"], canonical(value)))
                requirement_total += 1
        if requirement_total != count:
            raise ValueError("Requirement catalog does not cover the current database exactly")
        counts["RequirementRecords"] = requirement_total
        historical_total = 0
        historical_keys = ("PrimaryClassification", "ContentAssociations", "ContentConfidence", "RequiredExpansionAssociations", "RequirementEvidence", "FirstKnownPatch", "ActualIntroductionStatus", "Category", "CurrentCanFlags", "CurrentCanCarry", "AcquisitionSources", "Roles", "SpecialPurposeRoles", "MissionChestEligibility", "ChestStatus", "ChestEligibility", "SourceIds")
        for bucket in HISTORICAL_BUCKETS:
            for item in array_records(paths["historical/" + bucket + ".json"]):
                value = {key: item[key] for key in historical_keys if key in item}
                value["Bucket"] = bucket
                value["CatalogSource"] = "historical/" + bucket + ".json"
                db.execute("INSERT INTO history VALUES (?,?)", (item["TemplateId"], canonical(value)))
                historical_total += 1
        if historical_total != count:
            raise ValueError("Historical catalog does not cover the current database exactly")
        counts["HistoricalRecords"] = historical_total
        families = {key: row["Templates"] for key, row in read_json(paths["ItemTemplates.json"]).items() if row.get("Templates")}
        for catalog in ("MissionChestSeasonalExclusions.json", "MissionChestSpecialExclusions.json"):
            policy = read_json(paths["policies/" + catalog])
            if policy["Version"] != 1:
                raise ValueError("Unsupported exclusion schema")
            for ordinal, item in enumerate(policy["Entries"]):
                identities = set(item.get("TemplateIds", []))
                if item.get("FamilyHash"):
                    if item["FamilyHash"] not in families:
                        raise ValueError("Exclusion references an unresolved leaf family")
                    identities.update(families[item["FamilyHash"]])
                for identity in sorted(identities):
                    db.execute("INSERT INTO exclusions VALUES (?,?,?,?)", (identity, catalog, ordinal, canonical(item)))
        approvals = read_json(paths["policies/MissionChestRKAllowlist.json"])
        if approvals["Version"] != 1:
            raise ValueError("Unsupported approval schema")
        for item in approvals["Entries"]:
            db.execute("INSERT INTO approvals VALUES (?,?,?,?,?)", (item["LowId"], item["HighId"], item["MinQuality"], item["MaxQuality"], canonical(item)))
        reviews = read_json(paths["ordinary-rk-review-results.json"])
        for item in reviews["ApprovedDefinitions"]:
            db.execute("INSERT INTO reviews VALUES (?,?)", (item["Id"], canonical(item)))
        negatives = read_json(paths["negative-count-summary.json"])
        for name in ("MissionChestSeasonalExclusions.json", "MissionChestSpecialExclusions.json"):
            if negatives["OutputHashes"].get(name) != sources["policies/" + name]["Sha256"]:
                raise ValueError("Seasonal review summary does not bind the current exclusion policies")
        for season, identities in sorted(negatives["SeasonTemplateIds"].items()):
            for identity in identities:
                db.execute("INSERT INTO seasons VALUES (?,?)", (identity, season))
        db.execute("INSERT INTO source_manifest VALUES (?)", (canonical(manifest_identity),))
        db.execute("PRAGMA user_version=" + str(SCHEMA_VERSION))
        db.commit()
        if db.execute("PRAGMA quick_check").fetchone()[0] != "ok":
            raise ValueError("Built lookup index failed structural validation")
    finally:
        db.close()
    partial.replace(destination)
    result = {"Schema": "mission-chest-item-classification-index/1", "InputFingerprint": fingerprint,
              "IndexSha256": digest(destination), "Sources": sources, "Counts": counts,
              "Status": "OFFLINE_LOOKUP_NOT_RUNTIME_APPROVAL", "PolicyFilesModified": False}
    write_json(cache_receipt, result)
    return result


class ClassificationLookup:
    def __init__(self, path: Path):
        self.path = path.resolve()
        receipt = read_json(self.path.with_suffix(self.path.suffix + ".manifest.json"))
        if receipt["IndexSha256"] != digest(self.path):
            raise ValueError("Classification index no longer matches its receipt")
        self.db = sqlite3.connect("file:" + self.path.as_posix() + "?mode=ro", uri=True)
        if self.db.execute("PRAGMA user_version").fetchone()[0] != SCHEMA_VERSION:
            raise ValueError("Unsupported classification index schema")
        self.receipt = receipt
        self._items = {}

    def close(self):
        self.db.close()

    def _one(self, table: str, identity: int):
        row = self.db.execute("SELECT payload FROM " + table + " WHERE id=?", (identity,)).fetchone()
        return json.loads(row[0]) if row else None

    def item(self, identity: int):
        identity = int(identity)
        if identity in self._items:
            return self._items[identity]
        metadata = self._one("metadata", identity)
        requirements = self._one("requirements", identity)
        historical = self._one("history", identity)
        review = self._one("reviews", identity)
        exclusions = [{"Catalog": row[0], "EntryIndex": row[1], **json.loads(row[2])}
                      for row in self.db.execute("SELECT catalog,ordinal,payload FROM exclusions WHERE id=? ORDER BY catalog,ordinal", (identity,))]
        seasons = [row[0] for row in self.db.execute("SELECT DISTINCT season FROM seasons WHERE id=? ORDER BY season", (identity,))]
        categories = sorted({row["Category"] for row in exclusions})
        marker_names = {"QuestStat", "QuestInstance", "AccessKey", "ApartmentAccessCard"}
        markers = []
        if metadata:
            markers = [{"Source": "NamedStats." + key, "Value": value} for key, value in metadata["NamedStats"].items() if key in marker_names and value]
            markers += [{"Source": "Criteria", "Term": term} for term in metadata.get("Criteria", []) if term.get("StatName") in marker_names]
        value = {"TemplateId": identity, "DefinitionPresent": metadata is not None,
                 "CurrentDefinition": metadata, "RequiredExpansion": requirements,
                 "HistoricalContent": historical, "OrdinaryRkReview": review,
                 "SeasonalExcluded": any(row["Catalog"] == "MissionChestSeasonalExclusions.json" for row in exclusions),
                 "SeasonalAssociations": seasons, "HalloweenExcluded": "Halloween" in seasons,
                 "SpecialExcluded": any(row["Catalog"] == "MissionChestSpecialExclusions.json" for row in exclusions),
                 "QuestProgressionExcluded": any(category.startswith(("SpecialPurpose.GardenKey", "SpecialPurpose.SanctuaryKey", "SpecialPurpose.StartingQuestReward", "SpecialPurpose.Quest", "SpecialPurpose.Progression")) for category in categories),
                 "QuestRequirementMarkers": markers, "ExclusionCategories": categories, "ExclusionEvidence": exclusions,
                 "AllowlistEndpointMember": self.db.execute("SELECT 1 FROM approvals WHERE low=? OR high=? LIMIT 1", (identity, identity)).fetchone() is not None}
        self._items[identity] = value
        return value

    def descriptor(self, low: int, high: int, quality: int, template_id=None):
        low, high, quality = int(low), int(high), int(quality)
        endpoints = [self.item(identity) for identity in sorted({low, high})]
        approvals = [json.loads(row[0]) for row in self.db.execute(
            "SELECT payload FROM approvals WHERE low=? AND high=? AND minimum<=? AND maximum>=? ORDER BY minimum,maximum", (low, high, quality, quality))]
        negatives = any(item["SeasonalExcluded"] or item["SpecialExcluded"] for item in endpoints)
        requirements = sorted({value for item in endpoints for value in (item.get("RequiredExpansion") or {}).get("VerifiedExpansions", [])})
        origins = sorted({value for item in endpoints for value in (item.get("HistoricalContent") or {}).get("ContentAssociations", [])})
        conflicts = []
        if approvals and negatives:
            conflicts.append("APPROVAL_AND_CURRENT_NEGATIVE_POLICY_OVERLAP_NEGATIVE_WINS")
        if "RubiKa" in origins and requirements:
            conflicts.append("HISTORICAL_RK_ASSOCIATION_AND_CURRENT_EXPANSION_REQUIREMENT_ARE_DIFFERENT_AXES")
        if len({(item.get("HistoricalContent") or {}).get("Bucket", "Unknown") for item in endpoints}) > 1:
            conflicts.append("ENDPOINT_HISTORICAL_CLASSIFICATIONS_DIFFER")
        exact = self.item(int(template_id)) if template_id is not None else None
        return {"FamilyLowId": low, "FamilyHighId": high, "Quality": quality,
                "ExactTemplateId": int(template_id) if template_id is not None else None,
                "ExactDefinition": exact, "EndpointDefinitions": endpoints,
                "MissingCurrentDefinitions": [item["TemplateId"] for item in endpoints if not item["DefinitionPresent"]],
                "RequiredExpansionAssociations": requirements, "HistoricalContentAssociations": origins,
                "SeasonalExcluded": any(item["SeasonalExcluded"] for item in endpoints),
                "HalloweenExcluded": any(item["HalloweenExcluded"] for item in endpoints),
                "QuestProgressionExcluded": any(item["QuestProgressionExcluded"] for item in endpoints),
                "SpecialExcluded": any(item["SpecialExcluded"] for item in endpoints),
                "RkAllowlistExactPairAtQuality": bool(approvals),
                "CurrentRkChestPolicyAllows": bool(approvals) and not negatives,
                "ApprovalEvidence": approvals, "Conflicts": conflicts,
                "CaptureObservationDoesNotApproveGeneration": True}


def classify_records(records: Iterable[dict], index_path: Path):
    lookup = ClassificationLookup(index_path)
    try:
        result = []
        for record in records:
            value = dict(record)
            value["Classification"] = lookup.descriptor(record["FamilyLowId"], record["FamilyHighId"], record["Quality"], record.get("TemplateId"))
            result.append(value)
        return result
    finally:
        lookup.close()


def merge_capture_evidence(existing_path: Path | None, verified_records: list[dict], destination: Path):
    """Append a deterministic offline evidence ledger; never change generation approvals."""
    if existing_path and existing_path.resolve() == destination.resolve():
        raise ValueError("Capture reconciliation must preserve its input evidence file")
    old = read_json(existing_path) if existing_path and existing_path.exists() else {"FormatVersion": 1, "Assertions": [], "Sources": []}
    assertions = list(old.get("Assertions", []))
    old_keys = {canonical(row) for row in assertions}
    preserved = len(assertions)
    sources = list(old.get("Sources", []))
    source_keys = {canonical(row) for row in sources}
    preserved_sources = len(sources)
    for record in verified_records:
        if record.get("EvidenceStatus") not in ("VERIFIED_CONTENTS", "VERIFIED_CHEST_CONTENTS"):
            raise ValueError("Catalog integration accepts only explicitly verified chest-content records")
        reference = {key: record.get(key) for key in (
            "Session", "ChestIdentity", "WorldId", "WorldEvidence", "InventoryId", "LootId",
            "InventoryOrdinal", "Handle", "Slot", "FamilyLowId", "FamilyHighId", "TemplateId",
            "Quality", "QuantityRaw", "QuantityBasis", "PhysicalUnitConversion",
            "SourceCsvSha256", "RawPacketSha256", "Source")}
        if not reference["Session"] or not reference["ChestIdentity"] or reference["InventoryOrdinal"] is None or reference["Slot"] is None:
            raise ValueError("Verified capture evidence lacks its exact source/chest/inventory-slot locator")
        source_id = hashlib.sha256(canonical(reference).encode()).hexdigest()
        source = {"Id": source_id, "Status": "VERIFIED_CAPTURED_INVENTORY_IDENTITY_HANDLE_SLOT",
                  "Title": "Recorded mission world-container inventory descriptor",
                  "Scope": "One exact chest inventory slot; descriptor endpoints are not separate physical drops",
                  "CaptureEvidence": reference}
        source_key = canonical(source)
        if source_key not in source_keys:
            source_keys.add(source_key)
            sources.append(source)
        for role, identity in (("CapturedLowEndpoint", record["FamilyLowId"]), ("CapturedHighEndpoint", record["FamilyHighId"])):
            row = {"TemplateId": identity, "AcquisitionSources": [{
                       "Kind": "CapturedMissionWorldChestInventory", "Exclusive": False,
                       "Confidence": "PROVEN_CAPTURED_INVENTORY_IDENTITY_HANDLE_SLOT",
                       "Location": record["Session"], "SourceUrls": [],
                       "TemporalScope": "EXACT_RECORDED_CHEST_INVENTORY_OBSERVATION"}],
                   "ChestStatus": "VERIFIED_CAPTURED_DESCRIPTOR_ENDPOINT", "Confidence": "PROVEN_CAPTURED_INVENTORY_IDENTITY_HANDLE_SLOT",
                   "IdentityMatch": role, "ContentAssociations": [], "Roles": [],
                   "SourceIds": [source_id], "SourceUrls": [],
                   "Reason": "Exact captured descriptor endpoint; this does not assert two physical drops, origin, unrestricted RK eligibility or an authoritative loot probability.",
                   "CaptureEvidence": reference, "GenerationApprovalChanged": False}
            key = canonical(row)
            if key not in old_keys:
                old_keys.add(key)
                assertions.append(row)
    assertions[preserved:] = sorted(assertions[preserved:], key=canonical)
    sources[preserved_sources:] = sorted(sources[preserved_sources:], key=canonical)
    result = {**old, "Assertions": assertions, "Sources": sources,
              "CaptureEvidenceOverlay": {"PreviousAssertionsPreserved": preserved, "AppendedAssertions": len(assertions) - preserved,
                  "PreviousSourcesPreserved": preserved_sources, "AppendedSources": len(sources) - preserved_sources,
                  "GenerationApprovalChanged": False, "RuntimePolicyFilesModified": False,
                  "Scope": "OFFLINE_ACQUISITION_EVIDENCE_NOT_RUNTIME_CONTENT"}}
    write_json(destination, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    build = sub.add_parser("build-index")
    for name in ("metadata-root", "archive-root", "gamedata-root", "output"):
        build.add_argument("--" + name, type=Path, required=True)
    classify = sub.add_parser("classify")
    classify.add_argument("--index", type=Path, required=True)
    classify.add_argument("--input", type=Path, required=True)
    classify.add_argument("--output", type=Path, required=True)
    merge = sub.add_parser("merge-evidence")
    merge.add_argument("--input", type=Path, required=True)
    merge.add_argument("--existing", type=Path)
    merge.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.operation == "build-index":
        result = build_index(args.metadata_root, args.archive_root, args.gamedata_root, args.output)
        print(canonical({"Status": result["Status"], "Counts": result["Counts"], "IndexSha256": result["IndexSha256"]}))
    elif args.operation == "classify":
        records = read_records(args.input)
        result = classify_records(records, args.index)
        write_json(args.output, result)
        print(canonical({"Rows": len(result), "OutputSha256": digest(args.output)}))
    else:
        result = merge_capture_evidence(args.existing, read_records(args.input), args.output)
        print(canonical(result["CaptureEvidenceOverlay"]))


if __name__ == "__main__":
    main()
