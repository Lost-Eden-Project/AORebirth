from __future__ import annotations

import copy
import math
import sys
import unittest
from pathlib import Path


TOOLS = Path(__file__).resolve().parents[1]
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from halloween_placement_matching import match_placements


def placement(identity="p", position=(0, 0, 0), playfield=1, hash_text="FIXTURE"):
    return {"id": identity, "position": list(position), "playfield": playfield, "hash": hash_text}


def observation(identity="o", position=(0, 0, 0), playfield=1, template_id=10):
    return {"id": identity, "position": list(position), "playfield": playfield, "template_id": template_id}


class HalloweenPlacementMatchingTests(unittest.TestCase):
    def match(self, placements=None, objects=None, tolerance=0.4, mapping=None):
        return match_placements(
            [placement()] if placements is None else placements,
            [observation()] if objects is None else objects,
            {"FIXTURE": 10} if mapping is None else mapping,
            tolerance,
        )

    def test_exact_match_at_zero_tolerance_preserves_identity_and_delta(self):
        result = self.match(tolerance=0)
        self.assertEqual(1, len(result["matches"]))
        self.assertEqual("p", result["matches"][0]["placement_id"])
        self.assertEqual("o", result["matches"][0]["object_id"])
        self.assertEqual(0, result["matches"][0]["distance"])
        self.assertEqual([0, 0, 0], result["matches"][0]["delta"])
        self.assertEqual([], result["unmatched_placements"])
        self.assertEqual([], result["unmatched_objects"])

    def test_millimeter_drift_matches_using_all_three_axes(self):
        result = self.match([placement(position=(1, 2, 3))], [observation(position=(1.001, 2.002, 3.003))], 0.004)
        self.assertAlmostEqual(math.sqrt(14) / 1000, result["matches"][0]["distance"])
        for expected, actual in zip((0.001, 0.002, 0.003), result["matches"][0]["delta"]):
            self.assertAlmostEqual(expected, actual)

    def test_rounded_capture_coordinates_are_not_required_to_equal_source_floats(self):
        result = self.match([placement(position=(512.12345, 5, 241.9))], [observation(position=(512.12, 5.003, 241.9))], 0.01)
        self.assertEqual(1, len(result["matches"]))
        self.assertGreater(result["matches"][0]["distance"], 0)

    def test_tolerance_is_a_sphere_not_three_independent_axis_windows(self):
        result = self.match(objects=[observation(position=(0.3, 0.3, 0.3))])
        self.assertEqual([], result["matches"])
        row = result["unmatched_placements"][0]
        self.assertEqual("OUTSIDE_TOLERANCE", row["reason"])
        self.assertAlmostEqual(math.sqrt(0.27), row["nearest_compatible"][0]["distance"])

    def test_y_separation_prevents_false_exact_xz_match(self):
        result = self.match(objects=[observation(position=(0, 0.5, 0))])
        self.assertEqual([], result["matches"])
        self.assertEqual("OUTSIDE_TOLERANCE", result["unmatched_placements"][0]["reason"])

    def test_tolerance_boundary_is_inclusive_and_next_float_outside_is_rejected(self):
        self.assertEqual(1, len(self.match(objects=[observation(position=(0.4, 0, 0))])["matches"]))
        outside = math.nextafter(0.4, math.inf)
        self.assertEqual([], self.match(objects=[observation(position=(outside, 0, 0))])["matches"])

    def test_closest_global_edge_is_allocated_before_input_placement_order(self):
        placements = [placement("first", (0, 0, 0)), placement("closest", (0.2, 0, 0))]
        objects = [observation("shared", (0.11, 0, 0)), observation("remaining", (-0.3, 0, 0))]
        result = self.match(placements, objects, 0.5)
        pairs = {(row["placement_id"], row["object_id"]) for row in result["matches"]}
        self.assertEqual({("closest", "shared"), ("first", "remaining")}, pairs)
        reversed_result = self.match(list(reversed(placements)), list(reversed(objects)), 0.5)
        self.assertEqual(pairs, {(row["placement_id"], row["object_id"]) for row in reversed_result["matches"]})

    def test_two_placements_cannot_reuse_one_object(self):
        result = self.match([placement("near"), placement("far", (0.3, 0, 0))], [observation(position=(0.1, 0, 0))])
        self.assertEqual(1, len(result["matches"]))
        self.assertEqual("near", result["matches"][0]["placement_id"])
        self.assertEqual("ELIGIBLE_OBJECT_UNAVAILABLE", result["unmatched_placements"][0]["reason"])
        self.assertAlmostEqual(0.2, result["unmatched_placements"][0]["nearest_compatible"][0]["distance"])

    def test_one_placement_cannot_reuse_two_objects(self):
        result = self.match(objects=[observation("near", (0.1, 0, 0)), observation("far", (0.2, 0, 0))])
        self.assertEqual(1, len(result["matches"]))
        self.assertEqual("near", result["matches"][0]["object_id"])
        self.assertEqual("ELIGIBLE_PLACEMENT_UNAVAILABLE", result["unmatched_objects"][0]["reason"])

    def test_repeated_callbacks_for_the_same_exact_object_are_deduplicated(self):
        repeated = observation(position=(0.01, 0.02, 0.03))
        result = self.match(objects=[repeated, copy.deepcopy(repeated), copy.deepcopy(repeated)])
        self.assertEqual(1, len(result["matches"]))
        self.assertEqual(2, result["duplicate_object_observations"])
        self.assertEqual([], result["unmatched_objects"])

    def test_conflicting_callbacks_fail_closed_for_position_template_or_playfield(self):
        for change in ({"position": [0, 0, 0.01]}, {"template_id": 11}, {"playfield": 2}):
            with self.subTest(change=change):
                conflict = observation()
                conflict.update(change)
                with self.assertRaisesRegex(ValueError, "conflicting observations"):
                    self.match(objects=[observation(), conflict])

    def test_equidistant_objects_are_ambiguous_and_a_farther_object_does_not_break_tie(self):
        objects = [observation("left", (-0.1, 0, 0)), observation("right", (0.1, 0, 0)), observation("far", (0.2, 0, 0))]
        result = self.match(objects=objects)
        self.assertEqual([], result["matches"])
        row = result["unmatched_placements"][0]
        self.assertEqual("AMBIGUOUS_DISTANCE_TIE", row["reason"])
        self.assertEqual(2, len(row["nearest_compatible"]))
        self.assertEqual({"left", "right"}, {item["object_id"] for item in row["nearest_any"]})

    def test_equidistant_placements_are_ambiguous_but_independent_equal_distance_edges_match(self):
        placements = [placement("left", (-0.1, 0, 0)), placement("right", (0.1, 0, 0)), placement("independent", (10, 0, 0))]
        objects = [observation("shared"), observation("independent-object", (10, 0.1, 0))]
        result = self.match(placements, objects)
        self.assertEqual([("independent", "independent-object")],
                         [(row["placement_id"], row["object_id"]) for row in result["matches"]])
        self.assertTrue(all(row["reason"] == "AMBIGUOUS_DISTANCE_TIE" for row in result["unmatched_placements"]))

    def test_connected_tie_component_does_not_arbitrarily_allocate_a_perfect_matching(self):
        result = self.match([placement("a"), placement("b", (2, 0, 0))],
                            [observation("x", (1, 0, 0)), observation("y", (-1, 0, 0))], 1)
        self.assertEqual([], result["matches"])
        self.assertTrue(all(row["reason"] == "AMBIGUOUS_DISTANCE_TIE" for row in result["unmatched_placements"]))
        self.assertTrue(all(row["reason"] == "AMBIGUOUS_DISTANCE_TIE" for row in result["unmatched_objects"]))

    def test_template_mismatch_is_ineligible_even_when_it_is_closer(self):
        result = self.match(objects=[observation("wrong", template_id=11), observation("right", (0.2, 0, 0))])
        self.assertEqual("right", result["matches"][0]["object_id"])
        unmatched = self.match(objects=[observation(template_id=11)])["unmatched_placements"][0]
        self.assertEqual("NO_COMPATIBLE_OBJECT", unmatched["reason"])
        self.assertEqual([], unmatched["nearest_compatible"])
        self.assertEqual(0, unmatched["nearest_any"][0]["distance"])

    def test_playfield_mismatch_never_compares_coordinates(self):
        result = self.match(objects=[observation(playfield=2)])
        self.assertEqual([], result["matches"])
        self.assertEqual([], result["unmatched_placements"][0]["nearest_any"])
        self.assertEqual("NO_COMPATIBLE_OBJECT", result["unmatched_placements"][0]["reason"])

    def test_unknown_hash_is_not_classified_by_name_or_nearness(self):
        row = self.match([placement(hash_text="UNMAPPED")])["unmatched_placements"][0]
        self.assertEqual("NO_TEMPLATE_MAPPING", row["reason"])
        self.assertEqual([], row["nearest_compatible"])
        self.assertEqual(1, len(row["nearest_any"]))

    def test_empty_inputs_are_valid_and_inputs_remain_unchanged(self):
        self.assertEqual([], self.match([], [])["matches"])
        placements, objects, mapping = [placement()], [observation()], {"FIXTURE": 10}
        original = copy.deepcopy((placements, objects, mapping))
        self.match(placements, objects, mapping=mapping)
        self.assertEqual(original, (placements, objects, mapping))

    def test_nonfinite_negative_and_nonnumeric_tolerance_is_rejected(self):
        for value in (math.nan, math.inf, -math.inf, -0.01, True, "0.4", None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.match(tolerance=value)

    def test_nonfinite_malformed_and_nonnumeric_positions_are_rejected_on_either_side(self):
        for position in ((math.nan, 0, 0), (0, math.inf, 0), (0, 0, -math.inf), (0, True, 0), (0, "0", 0), (0, 0)):
            with self.subTest(position=position, side="placement"), self.assertRaises(ValueError):
                self.match([placement(position=position)])
            with self.subTest(position=position, side="object"), self.assertRaises(ValueError):
                self.match(objects=[observation(position=position)])

    def test_duplicate_placement_ids_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "placement ids must be unique"):
            self.match([placement(), placement(position=(0.1, 0, 0))])

    def test_invalid_template_mapping_and_missing_required_fields_are_rejected(self):
        for mapping in ({"FIXTURE": 0}, {"FIXTURE": True}, {"": 10}, {"FIXTURE": "10"}):
            with self.subTest(mapping=mapping), self.assertRaises(ValueError):
                self.match(mapping=mapping)
        for key in ("id", "playfield", "position", "template_id"):
            row = observation()
            del row[key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.match(objects=[row])


if __name__ == "__main__":
    unittest.main()
