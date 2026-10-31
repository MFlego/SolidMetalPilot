"""Offline tests of the sheet-metal DFM vocabulary (schema 0.8): a FAKE ExecutionPort, NO live SolidWorks.

What they prove, node by node (table driven, so a new node is a new row):
  - a valid node compiles to EXACTLY the tool call the execution layer expects (key names, the units at
    the tool boundary: radians -> degrees, anchors -> points, runtime names of earlier nodes substituted);
  - every way to get it wrong is rejected at validation, with NO execution call;
  - the typed tools' own verdict is enforced (verified=false stops the node, with the tool's problems);
  - a feature a typed tool created can seed a pattern / mirror (the compiler reads result_geometry.feature);
  - the changes to the older nodes (gauge table, extrude.merge, a radius per fillet edge);
  - COVERAGE: every tool of the execution contract is classified - reachable from a named IR node (and the
    node REALLY lowers to it) or exempt with a reason - so a tool added without an IR decision fails here.

Run two ways:
  - standalone:  python compiler/solidworks/pycompiler/tests/test_sheetmetal_nodes.py
  - pytest:      pytest compiler/solidworks/pycompiler/tests/test_sheetmetal_nodes.py
"""
import copy
import json
import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_COMPILER_ROOT = os.path.dirname(os.path.dirname(_HERE))        # compiler/solidworks/
_REPO_ROOT = os.path.dirname(os.path.dirname(_COMPILER_ROOT))
if _COMPILER_ROOT not in sys.path:
    sys.path.insert(0, _COMPILER_ROOT)

from pycompiler import coverage, ir_schema, sheetmetal_nodes          # noqa: E402
from pycompiler.compiler import compile_and_run                       # noqa: E402
from pycompiler.execution_port import ExecutionPort                   # noqa: E402

DEG = 180.0 / math.pi
_EDGES = json.dumps({"edge_count": 3, "edges": [
    {"i": 0, "start": [0, 0, 0], "end": [0.01, 0, 0], "mid": [0.005, 0.0, 0.0]},
    {"i": 1, "start": [0, 0, 0], "end": [0, 0.01, 0], "mid": [0.0, 0.005, 0.0]},
    {"i": 2, "start": [0.01, 0, 0], "end": [0.01, 0.01, 0], "mid": [0.01, 0.005, 0.0]}]})


class Port(ExecutionPort):
    """Records every call. Typed tools answer like the live layer: cadState.features EMPTY, the created
    feature in result_geometry (a LOCALISED name, so the runtime-name substitution is proven), verified true
    unless `unverified` names the tool, which answers verified=false with a problem."""

    NAMES = {"sheet_metal_hem": "Kenar", "sheet_metal_break_corner": "Koseyi", "sheet_metal_corners": "Kapali", "sheet_metal_corner_relief": "Rahat", "sheet_metal_miter_flange": "Gonye",
             "sheet_metal_gusset": "Destek", "sheet_metal_jog": "Kayma", "sheet_metal_tab_slot": "Gecme", "move_face": "Yuz", "sheet_metal_forming_tool": "sekil",
             "sheet_metal_lofted_bend": "Loft", "sheet_metal_swept_flange": "Supurme", "sheet_metal_fold": "Katla", "delete_face": "Yuzsil",
             "offset_surface": "Ofset", "surface_extrude": "Yuzey-Extrude", "surface_cut": "Yuzey-Kes", "sheet_metal_convert": "Donustur"}

    def __init__(self, unverified=(), edges=None):
        self._sv = 0
        self._unverified = set(unverified)
        self._edges = edges
        self._n = {}
        self.calls = []

    def get_state(self):
        return self._sv

    def execute(self, tool, params, state_version):
        self.calls.append((tool, copy.deepcopy(params)))
        if tool == "analyze_model":
            return {"status": "COMPLETED", "stateVersion": self._sv, "cadState": {"features": [self._edges] if self._edges else []}}
        self._sv += 1
        resp = {"status": "COMPLETED", "stateVersion": self._sv, "verified": True, "cadState": {"features": []}}
        if tool == "create_sketch":
            resp["cadState"]["activeSketch"] = "Cizim%d" % self._sv
        elif tool == "add_reference_geometry":
            resp["cadState"]["features"] = ["Duzlem%d" % self._sv]
        elif tool in self.NAMES:
            self._n[tool] = self._n.get(tool, 0) + 1
            bad = tool in self._unverified
            resp["verified"] = not bad
            resp["result_geometry"] = {"feature": "%s%d" % (self.NAMES[tool], self._n[tool]), "verified": not bad}
            if bad:
                resp["result_geometry"]["problems"] = ["the volume changed by nothing", "the feature reports rebuild error 1"]
        elif tool in ("extrude_feature", "create_pattern", "sheet_metal_feature", "add_edge_feature"):
            resp["cadState"]["features"] = ["Ozellik%d" % self._sv]
        return resp


def run(graph, **kw):
    port = Port(**kw)
    return port, compile_and_run(port, graph)


def anchor(x, y, z, hint=None):
    a = {"near": [x, y, z]}
    if hint:
        a["hint"] = hint
    return a


def sk(nid, datum="front", **extra):
    node = {"id": nid, "type": "sketch", "ref": {"datum": datum}, "profile": [{"kind": "line", "x1": 0, "y1": 0, "x2": 0.02, "y2": 0}]}
    node.update(extra)
    return node


def graph(*nodes, **extra):
    g = {"schema_version": "0.8.0-draft", "units": "meters", "nodes": list(nodes)}
    g.update(extra)
    return g


def base(nid="n1", sid="s1"):
    return [{"id": sid, "type": "sketch", "ref": {"datum": "front"}, "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]},
            {"id": nid, "type": "sheet_metal", "sketch": sid, "thickness": 0.0015, "bend_radius": 0.0015}]


# ---------------------------------------------------------------------------------------------------------
# One row per node: (node, prerequisites [nodes before it], tool, the EXACT params the tool must receive)
# ---------------------------------------------------------------------------------------------------------
P = anchor
CASES = {
    "hem": ({"id": "h", "type": "hem", "edges": [P(0, 0.03, 0.025), P(0.01, 0.03, 0.025)], "hem_type": "tear_drop", "length": 0.008, "gap": 0.001,
             "angle": math.radians(225), "radius": 0.001, "miter_gap": 0.0002, "position": "bend_outside", "reverse": True, "k_factor": 0.45},
            [], "sheet_metal_hem",
            {"points": [[0.0, 0.03, 0.025], [0.01, 0.03, 0.025]], "hem_type": "tear_drop", "length": 0.008, "gap": 0.001, "angle": 225.0, "radius": 0.001,
             "miter_gap": 0.0002, "bend_position": "bend_outside", "reverse": True, "k_factor": 0.45}),
    "break_corner": ({"id": "b", "type": "break_corner", "faces": [P(0, 0.029, 0.01)], "distance": 0.002, "corner_type": "chamfer",
                      "internal_corners_only": True, "centered_on_bend_lines": False},
                     [], "sheet_metal_break_corner",
                     {"points": [[0.0, 0.029, 0.01]], "distance": 0.002, "corner_type": "chamfer", "internal_corners_only": True, "centered_on_bend_lines": False}),
    "corner_relief": ({"id": "r", "type": "corner_relief", "relief": "obround", "width": 0.003, "length": 0.006, "tangent_to_bend": False,
                       "center_on_bend_lines": True, "add_filleted_corners": True, "fillet_diameter": 0.002},
                      [], "sheet_metal_corner_relief",
                      {"relief": "obround", "width": 0.003, "length": 0.006, "tangent_to_bend": False, "center_on_bend_lines": True,
                       "add_filleted_corners": True, "fillet_diameter": 0.002}),
    "closed_corner": ({"id": "c", "type": "closed_corner", "faces": [P(0.05, 0.02, 0.01), P(-0.05, 0.02, 0.01)], "corner_type": "underlap", "gap": 0.0001,
                       "overlap_ratio": 0.5, "open_bend_region": True},
                      [], "sheet_metal_corners",
                      {"points": [[0.05, 0.02, 0.01], [-0.05, 0.02, 0.01]], "corner_type": "underlap", "gap": 0.0001, "overlap_ratio": 0.5, "open_bend_region": True}),
    "miter_flange": ({"id": "m", "type": "miter_flange", "edges": [P(0, 0.03, 0), P(0.05, 0, 0), P(0, -0.03, 0), P(-0.05, 0, 0)],
                      "profile_points": [[0, 0.1], [0.019, 0.1], [0.019, 0.113]], "position": "bend_sharp", "bend_radius": 0.003, "rip_gap": 0.0005,
                      "trim_side_bends": False, "start_offset": 0.001, "end_offset": 0.002, "relief_ratio": 0.5, "k_factor": 0.45, "dimension_profile": False},
                     [], "sheet_metal_miter_flange",
                     {"points": [[0.0, 0.03, 0.0], [0.05, 0.0, 0.0], [0.0, -0.03, 0.0], [-0.05, 0.0, 0.0]], "profile_points": [[0, 0.1], [0.019, 0.1], [0.019, 0.113]],
                      "position": "bend_sharp", "bend_radius": 0.003, "rip_gap": 0.0005, "trim_side_bends": False, "start_offset": 0.001, "end_offset": 0.002,
                      "relief_ratio": 0.5, "k_factor": 0.45, "dimension_profile": False}),
    "gusset": ({"id": "g", "type": "gusset", "faces": [P(0, 0, 0), P(0, 0.0285, 0.012)], "position": 0.02, "indent_width": 0.01, "gusset_type": "flat",
                "dimension_scheme": "profile", "profile_length": 0.01, "profile_angle": math.radians(30), "thickness": 0.0015, "flip_sides": True,
                "draft": True, "draft_angle": math.radians(10), "fillet_edges": True, "edge_fillet_radius": 0.0005, "fillet_inner_corners": True,
                "inner_corner_fillet_radius": 0.0004, "fillet_outer_corners": True, "outer_corner_fillet_radius": 0.0003},
               [], "sheet_metal_gusset",
               {"points": [[0.0, 0.0, 0.0], [0.0, 0.0285, 0.012]], "position": 0.02, "indent_width": 0.01, "gusset_type": "flat", "dimension_scheme": "profile",
                "profile_length": 0.01, "profile_angle": 30.0, "thickness": 0.0015, "flip_sides": True, "draft": True, "draft_angle": 10.0, "fillet_edges": True,
                "edge_fillet_radius": 0.0005, "fillet_inner_corners": True, "inner_corner_fillet_radius": 0.0004, "fillet_outer_corners": True,
                "outer_corner_fillet_radius": 0.0003}),
    "jog": ({"id": "j", "type": "jog", "line": [[0, -0.03, 0], [0, 0.03, 0]], "fixed": P(-0.03, 0, 0), "offset": 0.01, "angle": math.radians(45),
             "direction": "down", "bend_radius": 0.002, "k_factor": 0.4},
            [], "sheet_metal_jog",
            {"line": [[0, -0.03, 0], [0, 0.03, 0]], "fixed_point": [-0.03, 0.0, 0.0], "offset": 0.01, "angle": 45.0, "direction": "down", "bend_radius": 0.002, "k_factor": 0.4}),
    "tab_slot": ({"id": "t", "type": "tab_slot", "edge": P(0, 0.01, 0.02), "slot_face": P(0, 0, 0.01), "tab_face": P(0, 0.02, 0.01), "tab_length": 0.01,
                  "count": 4, "start_offset": 0.005, "end_offset": 0.006, "slot_clearance": 0.0002, "tab_thickness": 0.0015, "tab_height": 0.004,
                  "edge_treatment": "fillet", "edge_treatment_size": 0.0005},
                 [], "sheet_metal_tab_slot",
                 {"edge_point": [0.0, 0.01, 0.02], "slot_face_point": [0.0, 0.0, 0.01], "tab_face_point": [0.0, 0.02, 0.01], "tab_length": 0.01, "count": 4,
                  "start_offset": 0.005, "end_offset": 0.006, "slot_clearance": 0.0002, "tab_thickness": 0.0015, "tab_height": 0.004, "edge_treatment": "fillet",
                  "edge_treatment_size": 0.0005}),
    "move_face": ({"id": "v", "type": "move_face", "faces": [P(0, 0, 0.05)], "move_type": "rotate", "reverse": True,
                   "rotation": {"axis_point": [0, 0, 0], "axis_direction": [0, 1, 0], "angle": math.radians(15)}},
                  [], "move_face",
                  {"points": [[0.0, 0.0, 0.05]], "move_type": "rotate", "reverse": True,
                   "rotation": {"axis_point": [0.0, 0.0, 0.0], "axis_direction": [0.0, 1.0, 0.0], "angle": 15.0}}),
    "forming_tool": ({"id": "f", "type": "forming_tool", "tool": "louver", "at": P(0.01, 0, 0), "rotation": math.radians(-45), "configuration": "Default"},
                     [], "sheet_metal_forming_tool",
                     {"action": "place", "tool": "louver", "point": [0.01, 0.0, 0.0], "rotation": -45.0, "configuration": "Default"}),
    "lofted_bend": ({"id": "l", "type": "lofted_bend", "profiles": ["a", "b"], "thickness": 0.0015, "thickness_side": "side2", "formed": True,
                     "bend_radius": 0.002, "facets": "segment_angle", "segment_angle": math.radians(10), "refer_to_end_points": True},
                    [sk("a", "front"), sk("b", "front")], "sheet_metal_lofted_bend",
                    {"sketches": ["Cizim1", "Cizim3"], "thickness": 0.0015, "thickness_side": "side2", "formed": True, "bend_radius": 0.002, "facets": "segment_angle",
                     "segment_angle": 10.0, "refer_to_end_points": True}),
    "swept_flange": ({"id": "w", "type": "swept_flange", "profile": "pr", "path": "pa", "thickness": 0.0015, "bend_radius": 0.0015, "reverse": True,
                      "start_offset": 0.01, "end_offset": 0.005, "trim_side_bends": True},
                     [sk("pa", "top"), sk("pr", "right")], "sheet_metal_swept_flange",
                     {"profile_sketch": "Cizim3", "path_sketch": "Cizim1", "thickness": 0.0015, "bend_radius": 0.0015, "reverse": True, "start_offset": 0.01,
                      "end_offset": 0.005, "trim_side_bends": True}),
    "from_flat": ({"id": "ff", "type": "from_flat", "dxf_path": "C:/flats/box.dxf", "thickness": 0.0015, "bend_radius": 0.003, "k_factor": 0.45,
                   "angle": math.radians(90), "units": "mm", "directions": ["up", "up", "down"], "hidden_direction": "down", "fixed_point": [0.0, 0.0],
                   "tolerance": 1e-5, "reach_tolerance": 5e-5, "allow_unfoldable": False, "keep_on_failure": True, "strategy": "auto", "rip_gap": 0.0005},
                  [], "sheet_metal_from_flat",
                  {"dxf_path": "C:/flats/box.dxf", "thickness": 0.0015, "bend_radius": 0.003, "k_factor": 0.45, "angle": 90.0, "units": "mm",
                   "directions": ["up", "up", "down"], "hidden_direction": "down", "fixed_point": [0.0, 0.0], "tolerance": 1e-5, "reach_tolerance": 5e-5,
                   "allow_unfoldable": False, "keep_on_failure": True, "strategy": "auto", "rip_gap": 0.0005}),
    "unfold": ({"id": "u", "type": "unfold", "fixed_face": P(0, 0, 0), "bends": "all"}, [], "sheet_metal_fold",
               {"action": "unfold", "fixed_face": [0.0, 0.0, 0.0], "bends": "all"}),
    "fold": ({"id": "o", "type": "fold", "fixed_face": P(0, 0, 0)}, [], "sheet_metal_fold", {"action": "fold", "fixed_face": [0.0, 0.0, 0.0]}),
    "flat_pattern": ({"id": "fp", "type": "flat_pattern", "corner_treatment": False, "simplify_bends": True, "merge_faces": False,
                      "show_slit_in_corner_relief": True, "leave_flat": True},
                     [], "flat_pattern_control",
                     {"corner_treatment": False, "simplify_bends": True, "merge_faces": False, "show_slit_in_corner_relief": True, "leave_flat": True}),
    "sheet_metal_settings": ({"id": "st", "type": "sheet_metal_settings", "k_factor": 0.42, "bend_allowance_type": "direct", "bend_allowance": 0.002,
                              "auto_relief": "obround", "relief_ratio": 0.6, "thickness": 0.0016, "bend_radius": 0.003, "reset_overrides": ["auto_relief"]},
                             [], "sheet_metal_params",
                             {"action": "set", "k_factor": 0.42, "bend_allowance_type": "direct", "bend_allowance": 0.002, "auto_relief": "obround",
                              "relief_ratio": 0.6, "thickness": 0.0016, "bend_radius": 0.003, "reset_overrides": ["auto_relief"]}),
    "delete_face": ({"id": "d", "type": "delete_face", "faces": [P(0, 0, 0.05), P(0, 0, -0.05)]}, [], "delete_face",
                    {"faces": [[0.0, 0.0, 0.05], [0.0, 0.0, -0.05]]}),
    "offset_surface": ({"id": "of", "type": "offset_surface", "body": P(0, 0, 0.02), "distance": 0.0016, "direction": "outward", "hide_original": False},
                       [], "offset_surface",
                       {"body_point": [0.0, 0.0, 0.02], "distance": 0.0016, "direction": "outward", "hide_original": False}),
    "surface_extrude": ({"id": "se", "type": "surface_extrude", "sketch": "q", "depth": 0.005, "reverse": True, "mid_plane": True},
                        [sk("q", "front")], "surface_extrude", {"sketch": "Cizim1", "depth": 0.005, "reverse": True, "mid_plane": True}),
    "surface_cut": ({"id": "sc", "type": "surface_cut", "surface": "se", "flip": True, "keep_piece": 1, "max_removed_volume": 1e-6},
                    [sk("q", "front"), {"id": "se", "type": "surface_extrude", "sketch": "q", "depth": 0.005}], "surface_cut",
                    {"surface": "Yuzey-Extrude1", "flip": True, "keep_piece": 1, "max_removed_volume": 1e-6}),
    "convert_to_sheet_metal": ({"id": "cv", "type": "convert_to_sheet_metal", "fixed_face": P(0, 0, 0), "thickness": 0.0016, "bend_radius": 0.003,
                                "steps": [{"list": "bend", "edges": [P(0.05, 0, 0), P(0, 0.03, 0)]}, {"list": "rip", "sketches": ["r1", "r2"]},
                                          {"list": "bend", "edges": [P(0.05, 0, 0.1)]}],
                                "reverse_thickness": "off", "keep_body": True, "corner": "overlap", "corner_timing": "early", "rip_gap": 0.00003,
                                "overlap_ratio": 1.0, "relief": "tear", "relief_ratio": 0.5, "k_factor": 0.5, "hide_other_bodies": False},
                               [sk("r1", "front"), sk("r2", "front")], "sheet_metal_convert",
                               {"fixed_face": [0.0, 0.0, 0.0], "thickness": 0.0016, "bend_radius": 0.003,
                                "steps": [{"list": "bend", "edges": [[0.05, 0.0, 0.0], [0.0, 0.03, 0.0]]}, {"list": "rip", "sketches": ["Cizim1", "Cizim3"]},
                                          {"list": "bend", "edges": [[0.05, 0.0, 0.1]]}],
                                "reverse_thickness": "off", "keep_body": True, "corner": "overlap", "corner_timing": "early", "rip_gap": 3e-05,
                                "overlap_ratio": 1.0, "relief": "tear", "relief_ratio": 0.5, "k_factor": 0.5, "hide_other_bodies": False}),
}


def _build(ntype):
    node, pre, tool, params = CASES[ntype]
    return graph(*(copy.deepcopy(pre) + [copy.deepcopy(node)])), tool, params


def test_every_new_node_has_a_case():
    assert set(CASES) == set(sheetmetal_nodes.NODE_TYPES), (set(CASES) ^ set(sheetmetal_nodes.NODE_TYPES))


def test_each_node_lowers_to_the_exact_tool_call():
    for ntype in sorted(CASES):
        g, tool, want = _build(ntype)
        assert ir_schema.validate(g) == [], (ntype, ir_schema.validate(g))
        port, r = run(g)
        assert r.status == "COMPLETED", (ntype, r.to_dict())
        sm_calls = [(t, p) for (t, p) in port.calls if t == tool]
        assert len(sm_calls) == 1, (ntype, [t for t, _ in port.calls])
        assert sm_calls[0][1] == want, "%s:\n got  %r\n want %r" % (ntype, sm_calls[0][1], want)
        assert sheetmetal_nodes.NODE_TOOLS[ntype] == tool, ntype
        last = r.node_log[-1]
        assert last["id"] == CASES[ntype][0]["id"] and last["status"] == "COMPLETED", (ntype, last)


def test_three_bend_corner_relief_lowers_with_its_corner_type_and_body():
    node = {"id": "r3", "type": "corner_relief", "corner_type": "three_bend", "relief": "rectangular", "length": 0.003175, "body": "Base"}
    g = graph(node)
    assert ir_schema.validate(g) == []
    port, r = run(g)
    assert r.status == "COMPLETED", r.to_dict()
    calls = [p for (t, p) in port.calls if t == "sheet_metal_corner_relief"]
    assert calls == [{"relief": "rectangular", "length": 0.003175, "corner_type": "three_bend", "body": "Base"}], calls


def test_minimal_forms_are_valid_and_send_only_what_was_given():
    minimal = {
        "hem": {"edges": [P(0, 0.03, 0.025)]}, "break_corner": {"faces": [P(0, 0, 0)], "distance": 0.001}, "closed_corner": {"faces": [P(0, 0, 0)]}, "corner_relief": {},
        "miter_flange": {"edges": [P(0, 0, 0)], "height": 0.02, "angle": math.pi / 2},
        "gusset": {"faces": [P(0, 0, 0), P(0, 1, 1)], "position": 0.01, "indent_width": 0.01, "indent_depth": 0.004},
        "jog": {"line": [[0, 0, 0], [0, 1, 0]], "fixed": P(1, 0, 0), "offset": 0.01}, "tab_slot": {"edge": P(0, 0, 0), "slot_face": P(1, 1, 1), "tab_length": 0.01},
        "move_face": {"faces": [P(0, 0, 0)], "distance": 0.002}, "forming_tool": {"tool": "louver", "at": P(0, 0, 0)},
        "unfold": {"fixed_face": P(0, 0, 0)}, "flat_pattern": {}, "sheet_metal_settings": {"k_factor": 0.4},
        "delete_face": {"faces": [P(0, 0, 0)]}, "offset_surface": {"body": P(0, 0, 0), "distance": 0.001},
    }
    expected = {"hem": {"points": [[0.0, 0.03, 0.025]]}, "unfold": {"action": "unfold", "fixed_face": [0.0, 0.0, 0.0]}, "flat_pattern": {},
                "forming_tool": {"action": "place", "tool": "louver", "point": [0.0, 0.0, 0.0]},
                "move_face": {"points": [[0.0, 0.0, 0.0]], "distance": 0.002}, "sheet_metal_settings": {"action": "set", "k_factor": 0.4}}
    for ntype, fields in minimal.items():
        node = dict(fields, id="n", type=ntype)
        errs = ir_schema.validate(graph(node))
        assert errs == [], (ntype, errs)
        port, r = run(graph(node))
        assert r.status == "COMPLETED", (ntype, r.to_dict())
        params = port.calls[-1][1]
        if ntype in expected:
            assert params == expected[ntype], (ntype, params)


# ---------------------------------------------------------------------------------------------------------
# validation: nothing is executed
# ---------------------------------------------------------------------------------------------------------
def _errors(node, pre=()):
    g = graph(*(list(copy.deepcopy(pre)) + [node]))
    errs = ir_schema.validate(g)
    port, r = run(g)
    assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", (node, r.to_dict())
    assert port.calls == [], "validation failures must execute nothing: %r" % (port.calls,)
    return " | ".join(errs)


def test_unknown_fields_are_errors_not_defaults():
    msg = _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "lenght": 0.01})
    assert "unknown field 'lenght'" in msg and "length" in msg


def test_missing_and_malformed_fields_are_named():
    assert "'edges' is required" in _errors({"id": "h", "type": "hem"})
    assert "'distance' is required" in _errors({"id": "b", "type": "break_corner", "faces": [P(0, 0, 0)]})
    assert "'hem_type' must be one of" in _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "hem_type": "double"})
    assert "must be a list of at least 1 anchors" in _errors({"id": "h", "type": "hem", "edges": []})
    assert "'edges' must be anchors" in _errors({"id": "h", "type": "hem", "edges": [{"near": [0, 0]}]})
    assert "'length' must be a positive number" in _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "length": 0})
    assert "'angle' must be a positive number (RADIANS)" in _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "angle": -1})
    assert "exactly 2 anchors" in _errors({"id": "g", "type": "gusset", "faces": [P(0, 0, 0)], "position": 0.01, "indent_width": 0.01, "indent_depth": 0.004})
    assert "'hint' must be a string" in _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "hint": 5})
    assert "'k_factor' must be a number in (0, 1)" in _errors({"id": "j", "type": "jog", "line": [[0, 0, 0], [0, 1, 0]], "fixed": P(1, 0, 0), "offset": 0.01, "k_factor": 1.5})
    assert "two different 3D points" in _errors({"id": "j", "type": "jog", "line": [[0, 0, 0], [0, 0, 0]], "fixed": P(1, 0, 0), "offset": 0.01})


def test_cross_field_rules():
    assert "tear_drop hem needs 180 < angle < 270" in _errors({"id": "h", "type": "hem", "edges": [P(0, 0, 0)], "hem_type": "tear_drop", "angle": math.radians(90)})
    one = lambda **kw: dict({"id": "m", "type": "miter_flange", "edges": [P(0, 0, 0)]}, **kw)         # noqa: E731
    assert "exactly ONE of height" in _errors(one())
    assert "exactly ONE of height" in _errors(one(height=0.02, profile_points=[[0, 0.02]]))
    assert "'angle' belongs with 'height'" in _errors(one(profile_points=[[0, 0.02]], angle=1.0))
    assert "'profile_points' must be a list of [across, up] pairs" in _errors(one(profile_points=[[0, 0.02, 3]]))
    assert "needs 'indent_depth'" in _errors({"id": "g", "type": "gusset", "faces": [P(0, 0, 0), P(0, 1, 1)], "position": 0.01, "indent_width": 0.01})
    assert "needs 'profile_length'" in _errors({"id": "g", "type": "gusset", "faces": [P(0, 0, 0), P(0, 1, 1)], "position": 0.01, "indent_width": 0.01, "dimension_scheme": "profile"})
    assert "at most 90 degrees" in _errors({"id": "j", "type": "jog", "line": [[0, 0, 0], [0, 1, 0]], "fixed": P(1, 0, 0), "offset": 0.01, "angle": 2.0})
    assert "needs 'edge_treatment_size'" in _errors({"id": "t", "type": "tab_slot", "edge": P(0, 0, 0), "slot_face": P(1, 1, 1), "tab_length": 0.01, "edge_treatment": "chamfer"})
    assert "a square relief needs 'length'" in _errors({"id": "r", "type": "corner_relief", "relief": "square"})
    assert "an obround relief needs 'width' and 'length'" in _errors({"id": "r", "type": "corner_relief", "relief": "obround", "length": 0.006})
    assert "add_filleted_corners needs 'fillet_diameter'" in _errors({"id": "r", "type": "corner_relief", "add_filleted_corners": True})
    assert "a three_bend corner takes the 'rectangular' relief only" in _errors({"id": "r", "type": "corner_relief", "corner_type": "three_bend", "relief": "obround", "length": 0.006, "width": 0.003})
    assert "a three_bend corner needs 'length'" in _errors({"id": "r", "type": "corner_relief", "corner_type": "three_bend", "relief": "rectangular"})
    assert "'corner_type' must be one of" in _errors({"id": "r", "type": "corner_relief", "corner_type": "four_bend"})
    assert "'relief' must be one of" in _errors({"id": "r", "type": "corner_relief", "relief": "bend_waist"})          # a THREE-bend relief: it killed SolidWorks on two-bend corners, so the vocabulary does not have it
    assert "needs 'distance'" in _errors({"id": "v", "type": "move_face", "faces": [P(0, 0, 0)]})
    assert "needs 'translation'" in _errors({"id": "v", "type": "move_face", "faces": [P(0, 0, 0)], "move_type": "translate"})
    assert "'rotation' must be {axis_point" in _errors({"id": "v", "type": "move_face", "faces": [P(0, 0, 0)], "move_type": "rotate", "rotation": {"angle": 0.3}})
    assert "nothing to set" in _errors({"id": "st", "type": "sheet_metal_settings"})
    assert "'direct' needs 'bend_allowance'" in _errors({"id": "st", "type": "sheet_metal_settings", "bend_allowance_type": "direct"})
    assert "'tool' must be a non-empty string" in _errors({"id": "f", "type": "forming_tool", "tool": " ", "at": P(0, 0, 0)})


def test_node_references_must_point_at_earlier_nodes_of_the_right_type():
    assert "EARLIER sketch node" in _errors({"id": "l", "type": "lofted_bend", "profiles": ["a", "nope"], "thickness": 0.001}, [sk("a")])
    assert "exactly 2" in _errors({"id": "l", "type": "lofted_bend", "profiles": ["a"], "thickness": 0.001}, [sk("a")])
    assert "'profile' and 'path' must be two DIFFERENT sketch nodes" in _errors({"id": "w", "type": "swept_flange", "profile": "a", "path": "a", "thickness": 0.001}, [sk("a")])
    assert "EARLIER surface_extrude node" in _errors({"id": "c", "type": "surface_cut", "surface": "a"}, [sk("a")])
    assert "EARLIER sketch node" in _errors({"id": "se", "type": "surface_extrude", "sketch": "x", "depth": 0.01})
    assert "EARLIER sketch node" in _errors({"id": "m", "type": "miter_flange", "edges": [P(0, 0, 0)], "profile_sketch": "x"})
    assert "rip steps" in _errors({"id": "cv", "type": "convert_to_sheet_metal", "fixed_face": P(0, 0, 0), "thickness": 0.0016, "bend_radius": 0.003,
                                   "steps": [{"list": "rip", "sketches": ["ghost"], "edges": []}]}, [sk("a")])


def test_first_feature_and_first_node_rules():
    two = [sk("a"), sk("b")]
    base_nodes = base()
    assert "must be the FIRST feature" in _errors({"id": "l", "type": "lofted_bend", "profiles": ["a", "b"], "thickness": 0.001}, base_nodes + two)
    assert "must be the FIRST feature" in _errors({"id": "w", "type": "swept_flange", "profile": "a", "path": "b", "thickness": 0.001}, base_nodes + two)
    assert "must be the FIRST node" in _errors({"id": "ff", "type": "from_flat", "dxf_path": "x.dxf", "thickness": 0.001, "bend_radius": 0.001}, [sk("a")])
    # sketches before them are fine
    for node in ({"id": "l", "type": "lofted_bend", "profiles": ["a", "b"], "thickness": 0.001}, {"id": "w", "type": "swept_flange", "profile": "a", "path": "b", "thickness": 0.001}):
        assert ir_schema.validate(graph(*(copy.deepcopy(two) + [node]))) == []


def test_part_and_assembly_vocabularies_still_never_mix():
    g = graph({"id": "h", "type": "hem", "edges": [P(0, 0, 0)]},
              {"id": "c", "type": "component", "source": {"path": "p.sldprt"}, "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]})
    assert any("mixes part-vocabulary and assembly-vocabulary" in e for e in ir_schema.validate(g))


# ---------------------------------------------------------------------------------------------------------
# the typed tools' own verdict, runtime names, patterns
# ---------------------------------------------------------------------------------------------------------
def test_unverified_result_stops_the_node_with_the_tools_problems():
    g, tool, _ = _build("hem")
    port, r = run(g, unverified=[tool])
    assert r.status == "FAILED" and r.error["code"] == "UNVERIFIED", r.to_dict()
    assert r.error["node_id"] == "h" and r.error["step"] == "sheet_metal_hem"
    assert "the volume changed by nothing" in r.error["message"] and "rebuild error 1" in r.error["message"]
    assert r.nodes_completed == 0


def test_an_older_tool_answering_verified_false_is_still_accepted():
    class Lenient(Port):
        def execute(self, tool, params, sv):
            resp = Port.execute(self, tool, params, sv)
            if tool == "extrude_feature":
                resp["verified"] = False
            return resp
    port = Lenient()
    r = compile_and_run(port, graph(sk("s"), {"id": "e", "type": "extrude", "sketch": "s", "operation": "boss", "depth": 0.01}))
    assert r.status == "COMPLETED", r.to_dict()


def test_a_tool_failure_is_a_feature_level_error_with_the_tools_code():
    class Failing(Port):
        def execute(self, tool, params, sv):
            if tool == "sheet_metal_forming_tool":
                return {"status": "FAILED", "stateVersion": self._sv, "error": {"code": "FORMING_TOOL_OVERHANG", "message": "the tool does not fit"}}
            return Port.execute(self, tool, params, sv)
    g, _, _ = _build("forming_tool")
    r = compile_and_run(Failing(), g)
    assert r.status == "FAILED" and r.error["code"] == "FORMING_TOOL_OVERHANG" and r.error["node_id"] == "f"


def test_a_forming_tool_seeds_a_louver_array():
    g = graph(*base(), {"id": "f", "type": "forming_tool", "tool": "louver", "at": P(-0.035, 0, 0)},
              {"id": "arr", "type": "linear_pattern", "feature": "f", "direction": "x", "spacing": 0.02, "count": 4})
    assert ir_schema.validate(g) == []
    port, r = run(g)
    assert r.status == "COMPLETED", r.to_dict()
    pat = [p for (t, p) in port.calls if t == "create_pattern"][0]
    assert pat["feature_name"] == "sekil1" and pat["count"] == 4 and pat["direction"] == "X"      # the runtime (localised) name the tool answered
    assert [n for n in r.node_log if n["id"] == "f"][0]["feature"] == "sekil1"


def test_a_surface_cut_can_be_mirrored_and_a_pattern_can_seed_on_any_new_producer():
    g = graph(sk("q"), {"id": "se", "type": "surface_extrude", "sketch": "q", "depth": 0.005},
              {"id": "sc", "type": "surface_cut", "surface": "se"}, {"id": "mi", "type": "mirror", "plane": {"datum": "right"}, "features": ["sc"]})
    port, r = run(g)
    assert r.status == "COMPLETED", r.to_dict()
    mir = [p for (t, p) in port.calls if t == "create_pattern"][0]
    assert json.loads(mir["features_json"]) == ["Yuzey-Kes1"]
    for ntype in sorted(sheetmetal_nodes.FEATURE_PRODUCERS):
        assert ntype in ir_schema.FEATURE_PRODUCERS, ntype
    for ntype in ("from_flat", "flat_pattern", "sheet_metal_settings"):
        assert ntype not in ir_schema.FEATURE_PRODUCERS, ntype


def test_runtime_sketch_names_come_from_the_nodes_that_made_them():
    g, _, want = _build("lofted_bend")
    port, r = run(g)
    sketches = [p["sketches"] for (t, p) in port.calls if t == "sheet_metal_lofted_bend"][0]
    created = [i for i, (t, _) in enumerate(port.calls) if t == "create_sketch"]
    assert sketches == ["Cizim%d" % 1, "Cizim%d" % 3] and len(created) == 2                       # the localised names create_sketch answered
    g2 = graph(sk("p0", "top"), sk("p1", "top"), {"id": "w", "type": "swept_flange", "profile": "p1", "path": "p0", "thickness": 0.0015})
    port2, r2 = run(g2)
    p = [p for (t, p) in port2.calls if t == "sheet_metal_swept_flange"][0]
    assert p["path_sketch"] == "Cizim1" and p["profile_sketch"] == "Cizim3"


# ---------------------------------------------------------------------------------------------------------
# the older nodes that grew in 0.8
# ---------------------------------------------------------------------------------------------------------
def test_sheet_metal_gauge_table():
    node = {"id": "n", "type": "sheet_metal", "sketch": "s", "gauge_table": "C:/tables/steel.xlsx", "gauge": "16 Gauge", "gauge_radius": 0.00254}
    g = graph({"id": "s", "type": "sketch", "ref": {"datum": "front"}, "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]}, node)
    assert ir_schema.validate(g) == []
    port, r = run(g)
    p = [p for (t, p) in port.calls if t == "sheet_metal_feature"][0]
    assert p == {"feature_type": "base_flange", "gauge_table": "C:/tables/steel.xlsx", "gauge": "16 Gauge", "gauge_radius": 0.00254}, p
    bad = copy.deepcopy(g)
    del bad["nodes"][1]["gauge"]
    assert any("needs 'gauge'" in e for e in ir_schema.validate(bad))
    bad = copy.deepcopy(g)
    del bad["nodes"][1]["gauge_table"]
    assert any("needs 'gauge_table'" in e for e in ir_schema.validate(bad))
    bad = copy.deepcopy(g)
    del bad["nodes"][1]["gauge_table"], bad["nodes"][1]["gauge"], bad["nodes"][1]["gauge_radius"]
    assert any("'thickness' must be a positive number" in e for e in ir_schema.validate(bad))        # without a table the thickness is still required


def test_extrude_merge_false_makes_a_separate_body():
    g = graph(sk("s"), {"id": "e", "type": "extrude", "sketch": "s", "operation": "boss", "depth": 0.01, "merge": False})
    port, r = run(g)
    assert [p for (t, p) in port.calls if t == "extrude_feature"][0] == {"feature_type": "boss", "depth": 0.01, "merge": False}
    g["nodes"][1]["merge"] = "no"
    assert any("'merge' must be a boolean" in e for e in ir_schema.validate(g))


def test_fillet_with_a_radius_per_edge_is_one_multiple_radius_feature():
    g = graph({"id": "f", "type": "fillet", "edges": [{"near": [0.01, 0.005, 0.0], "radius": 0.015}, {"near": [0.005, 0.0, 0.0], "radius": 0.005},
                                                       {"near": [0.0, 0.005, 0.0], "radius": 0.01}]})
    assert ir_schema.validate(g) == []
    port, r = run(g, edges=_EDGES)
    assert r.status == "COMPLETED", r.to_dict()
    p = [p for (t, p) in port.calls if t == "add_edge_feature"][0]
    # the anchors resolve to edges 2, 0, 1; the indices go out sorted and each radius stays with its edge
    assert p["edge_indices"] == "[0,1,2]" and p["radii"] == [0.005, 0.01, 0.015] and p["radius_or_distance"] == 0.005, p
    # the node's radius is the default for anchors without one; without any radius at all it is an error
    g2 = graph({"id": "f", "type": "fillet", "radius": 0.002, "edges": [{"near": [0.01, 0.005, 0.0], "radius": 0.015}, {"near": [0.005, 0.0, 0.0]}]})
    port2, r2 = run(g2, edges=_EDGES)
    p2 = [p for (t, p) in port2.calls if t == "add_edge_feature"][0]
    assert p2["radii"] == [0.002, 0.015] and p2["edge_indices"] == "[0,2]", p2
    g3 = graph({"id": "f", "type": "fillet", "edges": [{"near": [0.01, 0.005, 0.0], "radius": 0.015}, {"near": [0.005, 0.0, 0.0]}]})
    assert any("'radius' must be a positive number" in e for e in ir_schema.validate(g3))
    # the plain single-radius form is unchanged
    g4 = graph({"id": "f", "type": "fillet", "radius": 0.002, "edges": [{"near": [0.01, 0.005, 0.0]}]})
    port4, r4 = run(g4, edges=_EDGES)
    assert [p for (t, p) in port4.calls if t == "add_edge_feature"][0] == {"feature_type": "fillet", "radius_or_distance": 0.002, "edge_indices": "[2]"}


def test_capture_keeps_the_result_geometry_of_each_op():
    g, tool, _ = _build("forming_tool")
    port = Port()
    r = compile_and_run(port, g, capture=True)
    ops = r.node_log[-1]["ops"]
    assert ops[0]["result_geometry"]["feature"] == "sekil1"
    r2 = compile_and_run(Port(), g)
    assert "result_geometry" not in r2.node_log[-1]["ops"][0]


# ---------------------------------------------------------------------------------------------------------
# coverage: every execution tool is reachable from the IR (and really is) or exempt for a stated reason
# ---------------------------------------------------------------------------------------------------------
def _contract_tools():
    with open(os.path.join(_REPO_ROOT, "execution", "solidworks", "contracts", "tool-schemas.json"), encoding="utf-8") as fh:
        return set(json.load(fh))


def test_every_execution_tool_is_classified():
    tools = _contract_tools()
    classified = set(coverage.REACHABLE) | set(coverage.EXEMPT)
    assert not (tools - classified), "tools with NO IR decision (add them to pycompiler/coverage.py): %s" % sorted(tools - classified)
    assert not (classified - tools), "classified but not in the execution contract: %s" % sorted(classified - tools)
    assert not (set(coverage.REACHABLE) & set(coverage.EXEMPT)), sorted(set(coverage.REACHABLE) & set(coverage.EXEMPT))
    for tool, reason in coverage.EXEMPT.items():
        assert isinstance(reason, str) and len(reason) > 12, "an exemption needs a real reason: %s" % tool


def test_every_reachable_tool_is_reached_by_a_registered_node():
    for tool, nodes in coverage.REACHABLE.items():
        assert nodes, tool
        for n in nodes:
            assert n in ir_schema.NODE_TYPES or n in coverage.NON_NODE_DOORS, "%s -> unknown IR node %r" % (tool, n)
    # the new nodes are proven by compiling them: each node's sample must call the tool the manifest says
    for ntype in sorted(sheetmetal_nodes.NODE_TYPES):
        g, tool, _ = _build(ntype)
        port, r = run(g)
        assert r.status == "COMPLETED" and tool in [t for t, _ in port.calls], (ntype, tool)
        assert ntype in coverage.REACHABLE[tool], "node %s lowers to %s but the manifest does not list it" % (ntype, tool)


if __name__ == "__main__":
    failed = 0
    names = sorted(n for n in globals() if n.startswith("test_") and callable(globals()[n]))
    for name in names:
        try:
            globals()[name]()
            print("ok   - " + name)
        except Exception as ex:        # noqa: BLE001 - a test runner reports everything
            failed += 1
            print("FAIL - %s: %s" % (name, ex))
    print("\n%d/%d passed" % (len(names) - failed, len(names)))
    sys.exit(1 if failed else 0)
