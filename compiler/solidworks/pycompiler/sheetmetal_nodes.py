"""Sheet-metal DFM vocabulary of the Feature Graph IR (schema 0.8.0) - table driven.

One SPEC per node type holds everything the compiler needs to know about it: the tool it lowers to,
its fields (kind, required, the tool's parameter name, the conversion at the tool boundary), the
cross-field rules, and whether it creates a named feature (a valid seed for linear_pattern /
circular_pattern / mirror). validate_node() and lower_node() are generic over the table, so a new
node is a new table entry plus its schema entry - and the contract tests keep the two in step
(test_ir_schema_contract.py diffs the closed vocabularies AND the per-node field names).

Conventions (the IR's, unchanged):
  - lengths METERS, angles RADIANS (the tool boundary takes DEGREES - lowering converts);
  - geometry is referenced by ANCHORS {near:[x,y,z], hint?} - a point ON the face / edge, from the
    geometry the node meets when it runs. The typed execution tools find the entity by geometry
    (closest within 0.1 mm) and select it as an object, so the point goes straight through; a miss
    comes back as a feature-level error (EDGE_NOT_FOUND / FACE_NOT_FOUND ...);
  - a node that needs an EARLIER sketch / feature (lofted bend profiles, swept flange path, rip
    sketches, a surface cut's surface) names it by node id - the compiler substitutes the RUNTIME
    name SolidWorks gave it (localisation / numbering proof);
  - the new nodes are STRICT: an unknown field is an error (a typo must not silently become a
    default), and the typed tools' own `verified` verdict is enforced (a COMPLETED reply with
    verified=false fails the node with UNVERIFIED and the tool's problems).

Nothing here talks to SolidWorks: lowering returns Ops, the compiler executes them.
"""
import math
from collections import namedtuple

from .errors import FeatureError
from .lowering import Op

# ---------------------------------------------------------------------------------------------
# Closed vocabularies - each is advertised by the schema's covered_subset and machine-diffed.
# ---------------------------------------------------------------------------------------------
HEM_TYPES = frozenset(("closed", "open", "tear_drop", "rolled"))          # 'double': SolidWorks refuses every parameter set
HEM_POSITIONS = frozenset(("material_inside", "bend_outside"))
CORNER_TYPES = frozenset(("overlap", "underlap", "butt"))                  # closed_corner.corner_type, convert.corner
BREAK_CORNER_TYPES = frozenset(("fillet", "chamfer"))
CORNER_RELIEF_TYPES = frozenset(("circular", "square", "tear", "obround", "rectangular"))      # the five built and measured on two-bend corners; the three-bend reliefs kill SolidWorks on them
MITER_FLANGE_POSITIONS = frozenset(("material_inside", "material_outside", "bend_outside",
                                    "bend_centerline", "bend_sharp", "bend_tangent"))
GUSSET_TYPES = frozenset(("rounded", "flat"))
GUSSET_SCHEMES = frozenset(("indent_depth", "profile"))
BEND_DIRECTIONS = frozenset(("up", "down"))                                # jog.direction, from_flat.hidden_direction
LOFTED_THICKNESS_SIDES = frozenset(("side1", "side2"))
LOFTED_FACETS = frozenset(("chord_tolerance", "number_of_bends", "segment_length", "segment_angle"))
TAB_EDGE_TREATMENTS = frozenset(("none", "fillet", "chamfer"))
MOVE_TYPES = frozenset(("offset", "translate", "rotate"))
RELIEF_TYPES = frozenset(("rectangular", "obround", "tear"))               # settings.auto_relief, convert.relief
FROM_FLAT_STRATEGIES = frozenset(("sketched_bends", "auto", "miter_flange"))
FROM_FLAT_UNITS = frozenset(("auto", "mm", "cm", "m", "in", "ft"))
CONVERT_REVERSE_THICKNESS = frozenset(("default", "on", "off"))
CONVERT_CORNER_TIMINGS = frozenset(("late", "early"))
OFFSET_DIRECTIONS = frozenset(("inward", "outward"))
BEND_ALLOWANCE_TYPES = frozenset(("direct", "deduction"))
RESET_OVERRIDES = frozenset(("bend_allowance", "auto_relief", "bend_parameters"))
FOLD_BENDS = frozenset(("all",))
STEP_LISTS = frozenset(("bend", "rip"))

# schema covered_subset key -> frozenset (the contract test pairs them both ways)
ENUM_SETS = {
    "hem_types": HEM_TYPES, "hem_positions": HEM_POSITIONS, "corner_types": CORNER_TYPES, "corner_relief_types": CORNER_RELIEF_TYPES,
    "break_corner_types": BREAK_CORNER_TYPES, "miter_flange_positions": MITER_FLANGE_POSITIONS,
    "gusset_types": GUSSET_TYPES, "gusset_dimension_schemes": GUSSET_SCHEMES, "bend_directions": BEND_DIRECTIONS,
    "lofted_thickness_sides": LOFTED_THICKNESS_SIDES, "lofted_facet_options": LOFTED_FACETS,
    "tab_edge_treatments": TAB_EDGE_TREATMENTS, "move_types": MOVE_TYPES, "relief_types": RELIEF_TYPES,
    "from_flat_strategies": FROM_FLAT_STRATEGIES, "from_flat_units": FROM_FLAT_UNITS,
    "convert_reverse_thickness": CONVERT_REVERSE_THICKNESS, "convert_corner_timings": CONVERT_CORNER_TIMINGS,
    "offset_directions": OFFSET_DIRECTIONS, "bend_allowance_types": BEND_ALLOWANCE_TYPES,
    "reset_overrides": RESET_OVERRIDES, "fold_bends": FOLD_BENDS, "convert_step_lists": STEP_LISTS,
}

# ---------------------------------------------------------------------------------------------
# The table
# ---------------------------------------------------------------------------------------------
Field = namedtuple("Field", ["kind", "required", "param", "xform"])


def R(kind, param=None, xform=None):
    return Field(kind, True, param, xform)


def O(kind, param=None, xform=None):          # noqa: E741 - a short constructor name is the point
    return Field(kind, False, param, xform)


def _anchors(lo=1, hi=None):
    return ("anchors", lo, hi)


def _enum(values):
    return ("enum", values)


def _node(*types):
    return ("node", frozenset(types))


def _nodes(types, lo, hi):
    return ("nodes", frozenset(types), lo, hi)


SPECS = {
    # ---- sheet-metal features on an existing sheet --------------------------------------------
    "hem": {
        "tool": "sheet_metal_hem", "producer": True,
        "fields": {
            "edges": R(_anchors(), "points", "pts"),
            "hem_type": O(_enum(HEM_TYPES)), "length": O("len"), "gap": O("len"), "angle": O("ang", None, "deg"),
            "radius": O("len"), "miter_gap": O("len"), "position": O(_enum(HEM_POSITIONS), "bend_position"),
            "reverse": O("bool"), "k_factor": O("k"),
        },
        "rules": ["hem"],
    },
    "break_corner": {
        "tool": "sheet_metal_break_corner", "producer": True,
        "fields": {
            "faces": R(_anchors(), "points", "pts"), "distance": R("len"),
            "corner_type": O(_enum(BREAK_CORNER_TYPES)), "internal_corners_only": O("bool"),
            "centered_on_bend_lines": O("bool"),
        },
    },
    "corner_relief": {
        "tool": "sheet_metal_corner_relief", "producer": True,
        "fields": {
            "relief": O(_enum(CORNER_RELIEF_TYPES)), "width": O("len"), "length": O("len"),
            "tangent_to_bend": O("bool"), "center_on_bend_lines": O("bool"), "add_filleted_corners": O("bool"), "fillet_diameter": O("len"),
        },
        "rules": ["corner_relief"],
    },
    "closed_corner": {
        "tool": "sheet_metal_corners", "producer": True,
        "fields": {
            "faces": R(_anchors(), "points", "pts"), "corner_type": O(_enum(CORNER_TYPES)), "gap": O("len0"),
            "overlap_ratio": O("ratio"), "open_bend_region": O("bool"),
        },
    },
    "miter_flange": {
        "tool": "sheet_metal_miter_flange", "producer": True,
        "fields": {
            "edges": R(_anchors(), "points", "pts"),
            "height": O("len"), "profile_points": O("profile_points"), "profile_sketch": O(_node("sketch"), None, "node_name"),
            "dimension_profile": O("bool"), "angle": O("ang", None, "deg"), "position": O(_enum(MITER_FLANGE_POSITIONS)),
            "bend_radius": O("len"), "rip_gap": O("len0"), "trim_side_bends": O("bool"), "start_offset": O("len0"),
            "end_offset": O("len0"), "relief_ratio": O("pos"), "k_factor": O("k"),
        },
        "rules": ["miter_flange"],
    },
    "gusset": {
        "tool": "sheet_metal_gusset", "producer": True,
        "fields": {
            "faces": R(_anchors(2, 2), "points", "pts"), "position": R("len"), "indent_width": R("len"),
            "gusset_type": O(_enum(GUSSET_TYPES)), "dimension_scheme": O(_enum(GUSSET_SCHEMES)),
            "indent_depth": O("len"), "profile_length": O("len"), "profile_height": O("len"),
            "profile_angle": O("ang", None, "deg"), "thickness": O("len"), "flip_sides": O("bool"),
            "draft": O("bool"), "draft_angle": O("ang", None, "deg"),
            "fillet_edges": O("bool"), "edge_fillet_radius": O("len"),
            "fillet_inner_corners": O("bool"), "inner_corner_fillet_radius": O("len"),
            "fillet_outer_corners": O("bool"), "outer_corner_fillet_radius": O("len"),
        },
        "rules": ["gusset"],
    },
    "jog": {
        "tool": "sheet_metal_jog", "producer": True,
        "fields": {
            "line": R("line3"), "fixed": R("anchor", "fixed_point", "pt"), "offset": R("len"),
            "angle": O("ang", None, "deg"), "direction": O(_enum(BEND_DIRECTIONS)),
            "bend_radius": O("len"), "k_factor": O("k"),
        },
        "rules": ["jog"],
    },
    "tab_slot": {
        "tool": "sheet_metal_tab_slot", "producer": True,
        "fields": {
            "edge": R("anchor", "edge_point", "pt"), "slot_face": R("anchor", "slot_face_point", "pt"),
            "tab_face": O("anchor", "tab_face_point", "pt"), "tab_length": R("len"), "count": O("int1"),
            "start_offset": O("len0"), "end_offset": O("len0"), "slot_clearance": O("len"),
            "tab_thickness": O("len"), "tab_height": O("len"),
            "edge_treatment": O(_enum(TAB_EDGE_TREATMENTS)), "edge_treatment_size": O("len"),
        },
        "rules": ["tab_slot"],
    },
    "move_face": {
        "tool": "move_face", "producer": True,
        "fields": {
            "faces": R(_anchors(), "points", "pts"), "move_type": O(_enum(MOVE_TYPES)), "distance": O("len"),
            "reverse": O("bool"), "translation": O("point3"), "rotation": O("rotation", None, "rot"),
        },
        "rules": ["move_face"],
    },
    "forming_tool": {
        "tool": "sheet_metal_forming_tool", "producer": True, "fixed": {"action": "place"},
        "fields": {
            "tool": R("str"), "at": R("anchor", "point", "pt"), "rotation": O("num", None, "deg"),
            "configuration": O("str"),
        },
    },
    # ---- sheet-metal features that create the sheet themselves --------------------------------
    "lofted_bend": {
        "tool": "sheet_metal_lofted_bend", "producer": True, "first_feature": True,
        "fields": {
            "profiles": R(_nodes(("sketch",), 2, 2), "sketches", "node_names"), "thickness": R("len"),
            "thickness_side": O(_enum(LOFTED_THICKNESS_SIDES)), "formed": O("bool"), "bend_radius": O("len"),
            "facets": O(_enum(LOFTED_FACETS)), "chord_tolerance": O("len"), "number_of_bends": O("int1"),
            "segment_length": O("len"), "segment_angle": O("ang", None, "deg"), "refer_to_end_points": O("bool"),
        },
    },
    "swept_flange": {
        "tool": "sheet_metal_swept_flange", "producer": True, "first_feature": True,
        "fields": {
            "profile": R(_node("sketch"), "profile_sketch", "node_name"), "path": R(_node("sketch"), "path_sketch", "node_name"),
            "thickness": R("len"), "bend_radius": O("len"), "reverse": O("bool"), "start_offset": O("len0"),
            "end_offset": O("len0"), "trim_side_bends": O("bool"),
        },
        "rules": ["swept_flange"],
    },
    "from_flat": {
        "tool": "sheet_metal_from_flat", "producer": False, "first_node": True,
        "fields": {
            "dxf_path": R("str"), "thickness": R("len"), "bend_radius": R("len"), "k_factor": O("k"),
            "angle": O("ang", None, "deg"), "units": O(_enum(FROM_FLAT_UNITS)), "directions": O("directions"),
            "hidden_direction": O(_enum(BEND_DIRECTIONS)), "fixed_point": O("pair"), "tolerance": O("len"),
            "reach_tolerance": O("len"), "allow_unfoldable": O("bool"), "keep_on_failure": O("bool"),
            "strategy": O(_enum(FROM_FLAT_STRATEGIES)), "rip_gap": O("len0"),
        },
    },
    # ---- fold / unfold, flat pattern, settings ------------------------------------------------
    "unfold": {
        "tool": "sheet_metal_fold", "producer": True, "fixed": {"action": "unfold"},
        "fields": {"fixed_face": R("anchor", None, "pt"), "bends": O(_enum(FOLD_BENDS))},
    },
    "fold": {
        "tool": "sheet_metal_fold", "producer": True, "fixed": {"action": "fold"},
        "fields": {"fixed_face": R("anchor", None, "pt"), "bends": O(_enum(FOLD_BENDS))},
    },
    "flat_pattern": {
        "tool": "flat_pattern_control", "producer": False,
        "fields": {
            "corner_treatment": O("bool"), "simplify_bends": O("bool"), "merge_faces": O("bool"),
            "show_slit_in_corner_relief": O("bool"), "leave_flat": O("bool"),
        },
    },
    "sheet_metal_settings": {
        "tool": "sheet_metal_params", "producer": False, "fixed": {"action": "set"},
        "fields": {
            "k_factor": O("k"), "bend_allowance_type": O(_enum(BEND_ALLOWANCE_TYPES)), "bend_allowance": O("len0"),
            "bend_deduction": O("num"), "auto_relief": O(_enum(RELIEF_TYPES)), "relief_ratio": O("pos"),
            "thickness": O("len"), "bend_radius": O("len"), "reset_overrides": O(("list_enum", RESET_OVERRIDES)),
        },
        "rules": ["settings"],
    },
    # ---- Convert to Sheet Metal and the surface operations around it --------------------------
    "delete_face": {
        "tool": "delete_face", "producer": True,
        "fields": {"faces": R(_anchors(), None, "pts")},
    },
    "offset_surface": {
        "tool": "offset_surface", "producer": True,
        "fields": {
            "body": R("anchor", "body_point", "pt"), "distance": R("len"),
            "direction": O(_enum(OFFSET_DIRECTIONS)), "hide_original": O("bool"),
        },
    },
    "surface_extrude": {
        "tool": "surface_extrude", "producer": True,
        "fields": {
            "sketch": R(_node("sketch"), None, "node_name"), "depth": R("len"),
            "reverse": O("bool"), "mid_plane": O("bool"),
        },
    },
    "surface_cut": {
        "tool": "surface_cut", "producer": True,
        "fields": {
            "surface": R(_node("surface_extrude"), None, "node_name"), "flip": O("bool"),
            "keep_piece": O("int0"), "max_removed_volume": O("pos"),
        },
    },
    "convert_to_sheet_metal": {
        "tool": "sheet_metal_convert", "producer": True,
        "fields": {
            "fixed_face": R("anchor", None, "pt"), "thickness": R("len"), "bend_radius": R("len"), "steps": R("steps", None, "steps"),
            "reverse_thickness": O(_enum(CONVERT_REVERSE_THICKNESS)), "keep_body": O("bool"), "corner": O(_enum(CORNER_TYPES)),
            "corner_timing": O(_enum(CONVERT_CORNER_TIMINGS)), "rip_gap": O("len0"), "overlap_ratio": O("ratio"),
            "relief": O(_enum(RELIEF_TYPES)), "relief_ratio": O("pos"), "k_factor": O("k"), "hide_other_bodies": O("bool"),
        },
    },
}

NODE_TYPES = frozenset(SPECS)
FEATURE_PRODUCERS = frozenset(t for t, s in SPECS.items() if s.get("producer"))
# the spec's field names per node, for the schema contract test
FIELD_NAMES = {t: frozenset(s["fields"]) for t, s in SPECS.items()}
_COMMON = frozenset(("id", "type", "hint"))

# which execution tools each node lowers to, for the coverage manifest (derived, not repeated)
NODE_TOOLS = {t: s["tool"] for t, s in SPECS.items()}


# ---------------------------------------------------------------------------------------------
# validation
# ---------------------------------------------------------------------------------------------
def _num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _point3(v):
    return isinstance(v, list) and len(v) == 3 and all(_num(c) for c in v)


def _anchor_ok(v):
    return isinstance(v, dict) and _point3(v.get("near")) and (v.get("hint") is None or isinstance(v.get("hint"), str))


def _check_value(kind, v, seen):
    """Return None when v fits `kind`, else a short phrase for the error message."""
    if kind == "len":
        return None if _num(v) and v > 0 else "a positive number (meters)"
    if kind == "len0":
        return None if _num(v) and v >= 0 else "a number >= 0 (meters)"
    if kind == "ang":
        return None if _num(v) and v > 0 else "a positive number (RADIANS)"
    if kind == "num":
        return None if _num(v) else "a number"
    if kind == "pos":
        return None if _num(v) and v > 0 else "a positive number"
    if kind == "k":
        return None if _num(v) and 0 < v < 1 else "a number in (0, 1) (the K-factor)"
    if kind == "ratio":
        return None if _num(v) and 0 < v <= 1 else "a number in (0, 1]"
    if kind == "bool":
        return None if isinstance(v, bool) else "a boolean"
    if kind == "str":
        return None if isinstance(v, str) and v.strip() else "a non-empty string"
    if kind == "int1":
        return None if isinstance(v, int) and not isinstance(v, bool) and v >= 1 else "an integer >= 1"
    if kind == "int0":
        return None if isinstance(v, int) and not isinstance(v, bool) and v >= 0 else "an integer >= 0"
    if kind == "anchor":
        return None if _anchor_ok(v) else "an anchor {near:[x,y,z], hint?} - a 3D point ON the entity (meters)"
    if kind == "point3":
        return None if _point3(v) else "a 3D vector/point [x, y, z] (meters)"
    if kind == "line3":
        return None if isinstance(v, list) and len(v) == 2 and all(_point3(p) for p in v) and v[0] != v[1] \
            else "two different 3D points [[x,y,z],[x,y,z]] (meters)"
    if kind == "pair":
        return None if isinstance(v, list) and len(v) == 2 and all(_num(c) for c in v) else "a 2D point [x, y] (meters)"
    if kind == "profile_points":
        return None if isinstance(v, list) and len(v) >= 1 and all(
            isinstance(p, list) and len(p) == 2 and all(_num(c) for c in p) for p in v) \
            else "a list of [across, up] pairs (meters), at least one"
    if kind == "directions":
        if isinstance(v, list) and v and all(d in BEND_DIRECTIONS for d in v):
            return None
        if isinstance(v, dict) and v and all(isinstance(k, str) and d in BEND_DIRECTIONS for k, d in v.items()):
            return None
        return "a list of 'up' / 'down' (one per bend depth, innermost first) or an object {'<depth>': 'up'|'down'}"
    if kind == "rotation":
        if (isinstance(v, dict) and _point3(v.get("axis_point")) and _point3(v.get("axis_direction"))
                and any(c != 0 for c in v["axis_direction"]) and _num(v.get("angle")) and v["angle"] != 0
                and set(v) <= {"axis_point", "axis_direction", "angle"}):
            return None
        return "{axis_point:[x,y,z], axis_direction:[x,y,z] (not zero), angle: radians (not zero)}"
    if kind == "steps":
        return _check_steps(v, seen)
    if isinstance(kind, tuple):
        tag = kind[0]
        if tag == "enum":
            return None if isinstance(v, str) and v in kind[1] else "one of %s" % sorted(kind[1])
        if tag == "list_enum":
            return None if isinstance(v, list) and v and all(isinstance(x, str) and x in kind[1] for x in v) \
                else "a non-empty list of %s" % sorted(kind[1])
        if tag == "anchors":
            lo, hi = kind[1], kind[2]
            if not (isinstance(v, list) and len(v) >= lo and (hi is None or len(v) <= hi)):
                return "a list of %s anchors {near:[x,y,z], hint?}" % (
                    "at least %d" % lo if hi is None else ("exactly %d" % lo if lo == hi else "%d to %d" % (lo, hi)))
            bad = [i for i, a in enumerate(v) if not _anchor_ok(a)]
            return None if not bad else "anchors {near:[x,y,z], hint?} (entries %s are not)" % bad
        if tag == "node":
            return None if isinstance(v, str) and seen.get(v) in kind[1] else \
                "the id of an EARLIER %s node (got %r)" % ("/".join(sorted(kind[1])), v)
        if tag == "nodes":
            lo, hi = kind[2], kind[3]
            if not (isinstance(v, list) and lo <= len(v) <= hi):
                return "a list of %s ids of EARLIER %s nodes" % ("exactly %d" % lo if lo == hi else "%d to %d" % (lo, hi), "/".join(sorted(kind[1])))
            bad = [x for x in v if not (isinstance(x, str) and seen.get(x) in kind[1])]
            return None if not bad else "ids of EARLIER %s nodes (got %r)" % ("/".join(sorted(kind[1])), bad)
    raise ValueError("unknown field kind %r" % (kind,))


def _check_steps(v, seen):
    """convert_to_sheet_metal.steps: the ordered recipe - bend lists (edge anchors) and rip lists (sketch ids)."""
    if not isinstance(v, list) or not v:
        return "a non-empty list of steps [{list:'bend', edges:[anchors]} | {list:'rip', sketches:[sketch node ids]}]"
    for i, st in enumerate(v):
        if not isinstance(st, dict) or st.get("list") not in STEP_LISTS:
            return "steps whose 'list' is one of %s (step %d is not)" % (sorted(STEP_LISTS), i)
        if st["list"] == "bend":
            if set(st) - {"list", "edges"}:
                return "bend steps with only 'list' and 'edges' (step %d)" % i
            err = _check_value(_anchors(), st.get("edges"), seen)
            if err:
                return "step %d (bend): edges must be %s" % (i, err)
        else:
            if set(st) - {"list", "sketches"}:
                return "rip steps with only 'list' and 'sketches' (step %d)" % i
            err = _check_value(_nodes(("sketch",), 1, 10 ** 6), st.get("sketches"), seen)
            if err:
                return "step %d (rip): sketches must be %s" % (i, err)
    return None


def _rule_hem(node, label, errors):
    ang = node.get("angle")
    kind = node.get("hem_type", "closed")
    if _num(ang):
        if kind == "tear_drop" and not (math.pi < ang < 1.5 * math.pi):
            errors.append("%s: a tear_drop hem needs 180 < angle < 270 degrees (3.1416 < angle < 4.7124 rad)." % label)
        if kind == "rolled" and not (0 < ang < 2 * math.pi):
            errors.append("%s: a rolled hem needs 0 < angle < 360 degrees." % label)


def _rule_miter_flange(node, label, errors):
    given = [k for k in ("height", "profile_points", "profile_sketch") if node.get(k) is not None]
    if len(given) != 1:
        errors.append("%s: give exactly ONE of height (a straight wall), profile_points (a polyline) or "
                      "profile_sketch (your own sketch) - got %s." % (label, given or "none"))
    if node.get("angle") is not None and node.get("height") is None:
        errors.append("%s: 'angle' belongs with 'height' (it is the angle of that one straight wall)." % label)


def _rule_gusset(node, label, errors):
    scheme = node.get("dimension_scheme", "indent_depth")
    if scheme == "indent_depth":
        if node.get("indent_depth") is None:
            errors.append("%s: dimension_scheme 'indent_depth' (the default) needs 'indent_depth'." % label)
    elif node.get("profile_length") is None or (node.get("profile_height") is None and node.get("profile_angle") is None):
        errors.append("%s: dimension_scheme 'profile' needs 'profile_length' and 'profile_height' (or 'profile_angle')." % label)


def _rule_jog(node, label, errors):
    ang = node.get("angle")
    if _num(ang) and ang > math.pi / 2 + 1e-9:
        errors.append("%s: a jog bend angle is at most 90 degrees (1.5708 rad)." % label)


def _rule_tab_slot(node, label, errors):
    if node.get("edge_treatment", "none") != "none" and node.get("edge_treatment_size") is None:
        errors.append("%s: an edge_treatment of 'fillet' or 'chamfer' needs 'edge_treatment_size'." % label)


def _rule_corner_relief(node, label, errors):
    rel = node.get("relief", "circular")
    if rel in ("square", "rectangular", "obround") and node.get("length") is None:
        errors.append("%s: a %s relief needs 'length' (its side / slot length)." % (label, rel))
    if rel == "obround" and node.get("width") is None:
        errors.append("%s: an obround relief needs 'width' and 'length' (a slot about twice as long as it is wide)." % label)
    if node.get("add_filleted_corners") is True and node.get("fillet_diameter") is None:
        errors.append("%s: add_filleted_corners needs 'fillet_diameter'." % label)


def _rule_move_face(node, label, errors):
    kind = node.get("move_type", "offset")
    need = {"offset": "distance", "translate": "translation", "rotate": "rotation"}[kind] if kind in MOVE_TYPES else None
    if need and node.get(need) is None:
        errors.append("%s: move_type '%s' needs '%s'." % (label, kind, need))


def _rule_swept_flange(node, label, errors):
    if node.get("profile") is not None and node.get("profile") == node.get("path"):
        errors.append("%s: 'profile' and 'path' must be two DIFFERENT sketch nodes." % label)


def _rule_settings(node, label, errors):
    if not any(node.get(k) is not None for k in ("k_factor", "bend_allowance_type", "bend_allowance", "bend_deduction",
                                                  "auto_relief", "relief_ratio", "thickness", "bend_radius", "reset_overrides")):
        errors.append("%s: nothing to set - give at least one field." % label)
    kind = node.get("bend_allowance_type")
    if kind == "direct" and node.get("bend_allowance") is None:
        errors.append("%s: bend_allowance_type 'direct' needs 'bend_allowance' (meters)." % label)
    if kind == "deduction" and node.get("bend_deduction") is None:
        errors.append("%s: bend_allowance_type 'deduction' needs 'bend_deduction' (meters)." % label)


_RULES = {"corner_relief": _rule_corner_relief, "hem": _rule_hem, "miter_flange": _rule_miter_flange, "gusset": _rule_gusset, "jog": _rule_jog,
          "tab_slot": _rule_tab_slot, "move_face": _rule_move_face, "swept_flange": _rule_swept_flange,
          "settings": _rule_settings}


def validate_node(node, ntype, label, pos, seen, errors):
    """Append the structural errors of one sheet-metal node to `errors` (nothing is executed here)."""
    spec = SPECS[ntype]
    allowed = set(spec["fields"]) | _COMMON
    for k in sorted(set(node) - allowed):
        errors.append("%s: unknown field '%s' (supported: %s)." % (label, k, ", ".join(sorted(spec["fields"]))))
    if node.get("hint") is not None and not isinstance(node.get("hint"), str):
        errors.append("%s: 'hint' must be a string." % label)
    for fname, f in spec["fields"].items():
        v = node.get(fname)
        if v is None:
            if f.required:
                errors.append("%s: '%s' is required." % (label, fname))
            continue
        why = _check_value(f.kind, v, seen)
        if why:
            errors.append("%s: '%s' must be %s." % (label, fname, why))
    if spec.get("first_node") and pos != 0:
        errors.append("%s: a %s node must be the FIRST node (it builds in the empty part)." % (label, ntype))
    if spec.get("first_feature"):
        earlier = sorted(set(t for t in seen.values() if t != "sketch"))
        if earlier:
            errors.append("%s: a %s creates the sheet-metal definition itself, so it must be the FIRST feature "
                          "(found earlier %s node(s))." % (label, ntype, "/".join(earlier)))
    for rname in spec.get("rules", ()):
        _RULES[rname](node, label, errors)


# ---------------------------------------------------------------------------------------------
# lowering
# ---------------------------------------------------------------------------------------------
def _runtime(names, node_id, what, nid):
    name = names.get(node_id)
    if not name:
        raise FeatureError("%s '%s' has no recorded runtime name (it must be an earlier node that ran)" % (what, node_id),
                           code="NODE_FEATURE_UNAVAILABLE", node_id=nid)
    return name


def _xform(how, value, names, nid):
    if how is None:
        return value
    if how == "deg":
        return round(math.degrees(float(value)), 6)
    if how == "pts":
        return [[float(c) for c in a["near"]] for a in value]
    if how == "pt":
        return [float(c) for c in value["near"]]
    if how == "node_name":
        return _runtime(names, value, "node", nid)
    if how == "node_names":
        return [_runtime(names, x, "node", nid) for x in value]
    if how == "rot":
        return {"axis_point": [float(c) for c in value["axis_point"]],
                "axis_direction": [float(c) for c in value["axis_direction"]],
                "angle": round(math.degrees(float(value["angle"])), 6)}
    if how == "steps":
        out = []
        for st in value:
            if st["list"] == "bend":
                out.append({"list": "bend", "edges": [[float(c) for c in a["near"]] for a in st["edges"]]})
            else:
                out.append({"list": "rip", "sketches": [_runtime(names, s, "rip sketch node", nid) for s in st["sketches"]]})
        return out
    raise ValueError("unknown xform %r" % (how,))


def lower_node(node, names):
    """One sheet-metal node -> [Op]. `names` maps node id -> the runtime name recorded when it ran."""
    ntype = node["type"]
    spec = SPECS[ntype]
    params = dict(spec.get("fixed", {}))
    nid = node.get("id")
    for fname, f in spec["fields"].items():
        if node.get(fname) is None:
            continue
        params[f.param or fname] = _xform(f.xform, node[fname], names, nid)
    return [Op(spec["tool"], params, "%s -> %s" % (ntype, spec["tool"]), True)]
