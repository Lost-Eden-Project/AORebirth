"""Offline spatial candidates only; a match does not prove a runtime identity binding."""

from __future__ import annotations

import math
from collections import Counter
from itertools import groupby
from typing import Any


def _number(value: Any, description: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(description + " must be a finite number")
    try:
        result = float(value)
    except (OverflowError, ValueError) as error:
        raise ValueError(description + " must be a finite number") from error
    if not math.isfinite(result):
        raise ValueError(description + " must be a finite number")
    return result


def _positive_integer(value: Any, description: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError(description + " must be a positive integer")
    return value


def _record(source: dict[str, Any], kind: str) -> dict[str, Any]:
    if not isinstance(source, dict):
        raise ValueError(kind + " must be a dictionary")
    identity = source.get("id")
    if isinstance(identity, bool) or not isinstance(identity, (str, int)) or identity == "":
        raise ValueError(kind + " id must be a nonempty string or integer")
    position = source.get("position")
    if not isinstance(position, (list, tuple)) or len(position) != 3:
        raise ValueError(kind + " position must contain exactly three finite coordinates")
    row = {
        "id": identity,
        "playfield": _positive_integer(source.get("playfield"), kind + " playfield"),
        "position": tuple(_number(value, kind + " position") for value in position),
    }
    if kind == "placement":
        hash_text = source.get("hash")
        if not isinstance(hash_text, str) or not hash_text.strip():
            raise ValueError("placement hash must be a nonempty string")
        row["hash"] = hash_text
    else:
        row["template_id"] = _positive_integer(source.get("template_id"), "object template_id")
    return row


def match_placements(
    placements: list[dict[str, Any]],
    objects: list[dict[str, Any]],
    template_by_hash: dict[str, int],
    tolerance: float,
) -> dict[str, Any]:
    """Return one-to-one, globally closest spatial candidates within XYZ tolerance.

    A compatible edge requires the same playfield and the template supplied by
    ``template_by_hash``. The closest still-available edge wins. Competing edges
    at exactly the same distance are ambiguous: their connected component is
    excluded from allocation rather than resolved by input order. Independent
    edges at that distance are safe to allocate.

    Exact repeated object observations are deduplicated by id. Conflicting
    observations of an id, duplicate placement ids, and invalid numbers raise
    ValueError before any result is returned. Inputs are never modified.

    ``nearest_compatible`` and ``nearest_any`` are lists retaining every exact
    nearest-distance tie, even outside tolerance. ``nearest_any`` considers the
    same playfield only; coordinates in different playfields are not comparable.
    Deltas are captured-object XYZ minus placement XYZ. These results establish
    positional candidates, not native hash mappings or runtime identity proof.
    """
    limit = _number(tolerance, "tolerance")
    if limit < 0:
        raise ValueError("tolerance must not be negative")
    if not isinstance(template_by_hash, dict):
        raise ValueError("template_by_hash must be a dictionary")
    for hash_text, template_id in template_by_hash.items():
        if not isinstance(hash_text, str) or not hash_text.strip():
            raise ValueError("template_by_hash keys must be nonempty strings")
        _positive_integer(template_id, "template_by_hash template_id")

    points = [_record(source, "placement") for source in placements]
    if len({row["id"] for row in points}) != len(points):
        raise ValueError("placement ids must be unique")
    observations: list[dict[str, Any]] = []
    by_object_id: dict[str | int, dict[str, Any]] = {}
    duplicate_count = 0
    for source in objects:
        row = _record(source, "object")
        previous = by_object_id.get(row["id"])
        if previous is not None:
            if previous != row:
                raise ValueError("conflicting observations for object id " + str(row["id"]))
            duplicate_count += 1
            continue
        by_object_id[row["id"]] = row
        observations.append(row)

    all_edges: list[dict[str, Any]] = []
    eligible: list[dict[str, Any]] = []
    for p, placement in enumerate(points):
        for o, observation in enumerate(observations):
            if placement["playfield"] != observation["playfield"]:
                continue
            delta = tuple(b - a for a, b in zip(placement["position"], observation["position"]))
            distance = math.dist(placement["position"], observation["position"])
            if not math.isfinite(distance):
                raise ValueError("computed distance is not finite")
            edge = {
                "p": p, "o": o, "distance": distance, "delta": list(delta),
                "compatible": template_by_hash.get(placement["hash"]) == observation["template_id"],
            }
            all_edges.append(edge)
            if edge["compatible"] and distance <= limit:
                eligible.append(edge)

    used_points: set[int] = set()
    used_objects: set[int] = set()
    ambiguous_points: set[int] = set()
    ambiguous_objects: set[int] = set()
    selected: list[dict[str, Any]] = []
    for _, distance_group in groupby(sorted(eligible, key=lambda edge: edge["distance"]), key=lambda edge: edge["distance"]):
        available = [edge for edge in distance_group
                     if edge["p"] not in used_points and edge["p"] not in ambiguous_points
                     and edge["o"] not in used_objects and edge["o"] not in ambiguous_objects]
        point_degree = Counter(edge["p"] for edge in available)
        object_degree = Counter(edge["o"] for edge in available)
        tied_points = {p for p, count in point_degree.items() if count > 1}
        tied_objects = {o for o, count in object_degree.items() if count > 1}
        changed = True
        while changed:
            changed = False
            for edge in available:
                if edge["p"] in tied_points or edge["o"] in tied_objects:
                    if edge["p"] not in tied_points or edge["o"] not in tied_objects:
                        changed = True
                    tied_points.add(edge["p"])
                    tied_objects.add(edge["o"])
        ambiguous_points.update(tied_points)
        ambiguous_objects.update(tied_objects)
        for edge in available:
            if edge["p"] not in tied_points and edge["o"] not in tied_objects:
                selected.append(edge)
                used_points.add(edge["p"])
                used_objects.add(edge["o"])

    def describe(edge: dict[str, Any]) -> dict[str, Any]:
        placement = points[edge["p"]]
        observation = observations[edge["o"]]
        return {
            "placement_id": placement["id"], "object_id": observation["id"],
            "playfield": placement["playfield"], "hash": placement["hash"],
            "template_id": observation["template_id"], "distance": edge["distance"],
            "delta": edge["delta"],
        }

    def nearest(edges: list[dict[str, Any]]) -> list[dict[str, Any]]:
        if not edges:
            return []
        distance = min(edge["distance"] for edge in edges)
        return [describe(edge) for edge in edges if edge["distance"] == distance]

    def unmatched(index: int, side: str) -> dict[str, Any]:
        is_point = side == "p"
        row = points[index] if is_point else observations[index]
        edges = [edge for edge in all_edges if edge[side] == index]
        compatible = [edge for edge in edges if edge["compatible"]]
        within = [edge for edge in compatible if edge["distance"] <= limit]
        if index in (ambiguous_points if is_point else ambiguous_objects):
            reason = "AMBIGUOUS_DISTANCE_TIE"
        elif is_point and row["hash"] not in template_by_hash:
            reason = "NO_TEMPLATE_MAPPING"
        elif not compatible:
            reason = "NO_COMPATIBLE_OBJECT" if is_point else "NO_COMPATIBLE_PLACEMENT"
        elif not within:
            reason = "OUTSIDE_TOLERANCE"
        else:
            reason = "ELIGIBLE_OBJECT_UNAVAILABLE" if is_point else "ELIGIBLE_PLACEMENT_UNAVAILABLE"
        return {
            "placement_id" if is_point else "object_id": row["id"],
            "playfield": row["playfield"], "reason": reason,
            "nearest_compatible": nearest(compatible), "nearest_any": nearest(edges),
        }

    return {
        "tolerance": limit,
        "matches": [describe(edge) for edge in sorted(selected, key=lambda edge: edge["p"])],
        "unmatched_placements": [unmatched(p, "p") for p in range(len(points)) if p not in used_points],
        "unmatched_objects": [unmatched(o, "o") for o in range(len(observations)) if o not in used_objects],
        "duplicate_object_observations": duplicate_count,
    }
