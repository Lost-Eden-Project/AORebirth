"""Structural checks for additive offline capture evidence; no GameData or engine access."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


MODULE_PATH = Path(__file__).resolve().parents[1] / "MissionChestRKCatalog" / "reconcile_captured_loot.py"
SPEC = importlib.util.spec_from_file_location("reconcile_captured_loot", MODULE_PATH)
CATALOG = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CATALOG)


class CaptureEvidenceMergeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.existing = self.root / "historical.json"
        self.baseline = {
            "FormatVersion": 1,
            "Assertions": [{"TemplateId": 1, "Reason": "retained historical evidence"}],
            "Sources": [{"Id": "original", "Status": "UNAVAILABLE"}],
            "Gaps": ["retained evidence gap"],
        }
        self.existing.write_text(json.dumps(self.baseline), encoding="utf-8")
        self.record = {
            "EvidenceStatus": "VERIFIED_CHEST_CONTENTS", "Session": "fixture-session",
            "ChestIdentity": "container:1", "InventoryOrdinal": 25, "Slot": 2,
            "FamilyLowId": 101, "FamilyHighId": 102, "Quality": 50,
            "QuantityRaw": 1,
        }

    def tearDown(self):
        self.temporary.cleanup()

    def merge(self, rows, name="merged.json", existing=None):
        return CATALOG.merge_capture_evidence(existing or self.existing, rows, self.root / name)

    def test_history_preserved_and_endpoint_evidence_does_not_approve_generation(self):
        original_bytes = self.existing.read_bytes()
        result = self.merge([self.record])
        self.assertEqual(self.existing.read_bytes(), original_bytes)
        self.assertEqual(result["Assertions"][:1], self.baseline["Assertions"])
        self.assertEqual(result["Sources"][:1], self.baseline["Sources"])
        self.assertEqual(result["Gaps"], self.baseline["Gaps"])
        self.assertEqual(result["CaptureEvidenceOverlay"]["AppendedAssertions"], 2)
        self.assertEqual(result["CaptureEvidenceOverlay"]["AppendedSources"], 1)
        for row in result["Assertions"][1:]:
            self.assertFalse(row["GenerationApprovalChanged"])
            self.assertEqual(row["ContentAssociations"], [])
            self.assertIsInstance(row["AcquisitionSources"][0], dict)
            self.assertFalse(row["AcquisitionSources"][0]["Exclusive"])

    def test_duplicate_observation_is_deduplicated_without_losing_distinct_slots(self):
        second_slot = {**self.record, "Slot": 3}
        result = self.merge([self.record, self.record, second_slot])
        self.assertEqual(result["CaptureEvidenceOverlay"]["AppendedAssertions"], 4)
        self.assertEqual(result["CaptureEvidenceOverlay"]["AppendedSources"], 2)
        repeated = self.merge([self.record, second_slot], "second.json", self.root / "merged.json")
        self.assertEqual(repeated["CaptureEvidenceOverlay"]["AppendedAssertions"], 0)
        self.assertEqual(repeated["Assertions"], result["Assertions"])
        self.assertEqual(repeated["Sources"], result["Sources"])

    def test_record_order_does_not_change_output(self):
        another = {**self.record, "Session": "fixture-session-two", "Slot": 5}
        first = self.merge([self.record, another], "first.json")
        second = self.merge([another, self.record], "second.json")
        self.assertEqual(first, second)
        self.assertEqual((self.root / "first.json").read_bytes(), (self.root / "second.json").read_bytes())

    def test_unverified_or_unlocated_evidence_rejected(self):
        for invalid in ({**self.record, "EvidenceStatus": "UNRESOLVED"},
                        {**self.record, "Slot": None}, {**self.record, "ChestIdentity": None}):
            with self.assertRaises(ValueError):
                self.merge([invalid])
        self.assertFalse((self.root / "merged.json").exists())

    def test_existing_evidence_cannot_be_overwritten(self):
        with self.assertRaises(ValueError):
            CATALOG.merge_capture_evidence(self.existing, [self.record], self.existing)


if __name__ == "__main__":
    unittest.main()
