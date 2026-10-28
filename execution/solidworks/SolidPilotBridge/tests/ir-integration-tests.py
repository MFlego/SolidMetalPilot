"""PRE-INTEGRATION tests of the IR door against LIVE SolidWorks: every sheet-metal DFM node of the Feature Graph IR (schema 0.8)
is submitted as a graph through the REAL compiler (pycompiler) and the REAL adapter port, and the geometry SolidWorks ends up with is asserted -
numbers, faces, refusals - not just COMPLETED. The graphs are the ones a planner would send; the fixtures and the expected numbers are those of the
tool-level suites (features-tests.ps1, forming-tests.ps1, flat-edit-tests.ps1, convert-tests.ps1), so a node that lowers wrongly shows up as a
number that is not the tool's own.

What it proves, test by test (I00 .. I23):
  the door as a model uses it (the MCP function, a JSON string) · a forming tool and a louver ARRAY (a pattern seeded on a typed tool's feature) ·
  a forming tool that would overhang is REFUSED and the part is untouched · a gauge-table sheet · hem · break corner · gusset · two gussets ·
  jog · mitre flange (a straight wall and a rim + lip) · closed corners · lofted bend · swept flange · tab and slot (two bodies) · move face ·
  unfold -> a radius per fillet edge -> fold · unfold -> surface extrude -> surface cut -> fold · sheet-metal settings + flat-pattern options ·
  Convert to Sheet Metal as a graph (needs SolidWorks ON SCREEN: reported SKIPPED when another window covers the graphics area) · fold from a flat DXF ·
  corner relief on a four-wall tray (circular and obround, a refused 2.9 mm circle, a three-bend relief outside the vocabulary) · surface preparation
  (delete face -> offset surface, inward and outward).

Needs SolidWorks running with the SolidPilot Bridge (shell >= 0.9, tools loaded) and NOTHING else open: every test closes all documents and
builds its own part. Run with a Python that has the adapter's requirements (mcp, httpx):
    python ir-integration-tests.py [--base http://localhost:5000] [--only "I05|I06"] [--force]
Exit code = the number of failed tests.
"""
import argparse
import json
import math
import os
import pathlib
import re
import sys
import tempfile
import time
import traceback

ap = argparse.ArgumentParser()
ap.add_argument("--base", default="http://localhost:5000")
ap.add_argument("--only", default="", help="regex on the test name")
ap.add_argument("--force", action="store_true", help="run although a document is open (the suite closes every document)")
args = ap.parse_args()
os.environ["EXECUTION_BASE_URL"] = args.base          # must be set before the adapter's config module is imported

REPO = pathlib.Path(__file__).resolve().parents[4]    # tests -> SolidPilotBridge -> solidworks -> execution -> repo root
PROBES = REPO / "execution" / "solidworks" / "SolidPilotBridge" / "probes"
sys.path.insert(0, str(REPO / "adapters" / "claude"))
sys.path.insert(0, str(REPO / "compiler" / "solidworks"))
import logging  # noqa: E402
import httpx  # noqa: E402
logging.getLogger("httpx").setLevel(logging.WARNING)       # one INFO line per request would drown the report
import server  # noqa: E402  (the real MCP adapter module)
from ir_execution_port import run_feature_graph  # noqa: E402

PI = math.pi
PIPE = r"\\.\pipe\SolidPilotBridge"


class Skip(Exception):
    pass


# ---------------------------------------------------------------------------------------------------------------------------------------------
# plumbing
# ---------------------------------------------------------------------------------------------------------------------------------------------
def pipe(cmd):
    with open(PIPE, "r+b", buffering=0) as f:
        f.write((cmd + "\n").encode("utf-8"))
        data = b""
        while not data.endswith(b"\n"):
            chunk = f.read(65536)
            if not chunk:
                break
            data += chunk
    return data.decode("utf-8", "replace").strip()


def probe(name, arg=""):
    return pipe("run_cs %s%s" % (PROBES / name, ("|" + arg) if arg else "")).replace(" ;; ", "\n")


def volume_mm3():
    out = probe("probe55_mass.cs")
    m = re.search(r"volume=([-+0-9.eE]+)", out)
    if not m:
        raise AssertionError("the mass probe answered: " + out[:200])
    return float(m.group(1)) * 1e9


def analyze(kind):
    r = server._call_raw("analyze_model", {"analysis_type": kind, "name": ""})
    assert r.get("status") == "COMPLETED", r
    return json.loads(((r.get("cadState") or {}).get("features") or [None])[0])


def faces():
    return analyze("faces")["faces"]


def edges():
    return analyze("edges")["edges"]


def extent(axis):
    v = [f["point"][axis] for f in faces() if f.get("planar") and abs(f["normal"][axis]) > 0.999999]
    return min(v), max(v)


def tool(name, **params):
    r = server._call_raw(name, params)
    return r


def result_geometry(r):
    return r.get("result_geometry") or {}


def build(graph, fresh=True):
    """Submit a graph the way the forward door does (a new part first), with every op's result kept."""
    if fresh:
        server._call_raw("close_document", {"save": False, "close_all": True})
        opened = server._call_raw("open_new_part", {})
        assert opened.get("status") == "COMPLETED", opened
    res = run_feature_graph(graph, capture=True)
    server._resync_state_version()
    return res


def solidworks_in_front():
    """True when SolidWorks is the foreground window (the UI-driven nodes click in its graphics area)."""
    try:
        import ctypes
        u = ctypes.windll.user32
        buf = ctypes.create_unicode_buffer(300)
        u.GetWindowTextW(u.GetForegroundWindow(), buf, 300)
        return buf.value.upper().startswith("SOLIDWORKS")
    except Exception:       # noqa: BLE001
        return False


# the answers of a UI-driven tool (Convert to Sheet Metal) when the screen, not the graph, is the problem: the window is covered, the picks do not land
UI_STATE_ERRORS = ("GRAPHICS_COVERED", "NOT_PICKABLE", "WRONG_PICK", "DIALOG_BLOCKING", "PM_STUCK", "PM_DIALOG")


def ok(res, what=""):
    if res.status != "COMPLETED":
        err = res.error or {}
        if err.get("code") in UI_STATE_ERRORS and err.get("step") == "sheet_metal_convert":
            # the node got as far as the tool (the graph was valid, the parameters accepted, the picking begun): ACCESS is proven; the UI step is a matter of the screen
            raise Skip("reached sheet_metal_convert, but its UI step failed (%s%s): SolidWorks must be on screen, in front, not covered - %s"
                       % (err.get("code"), "" if solidworks_in_front() else ", SolidWorks is not the foreground window", err.get("message", "")[:110]))
        raise AssertionError("%s: %s | %s | node %s step %s" % (what or "graph", err.get("code"), err.get("message"), err.get("node_id"), err.get("step")))
    return res


def refused(res, code, node_id=None):
    assert res.status == "FAILED", "expected FAILED/%s, got %s" % (code, res.status)
    assert res.error["code"] == code, "expected %s, got %s: %s" % (code, res.error["code"], res.error["message"])
    if node_id:
        assert res.error["node_id"] == node_id, res.error
    return res.error


def node(res, nid):
    return next(n for n in res.node_log if n["id"] == nid)


def rg(res, nid, tool_name=None):
    ops = [o for o in node(res, nid)["ops"] if "result_geometry" in o and (tool_name is None or o["tool"] == tool_name)]
    assert ops, "node %s kept no result_geometry" % nid
    return ops[-1]["result_geometry"]


def near(a, b, tol, what=""):
    assert abs(a - b) <= tol, "%s: %r vs %r (tolerance %r)" % (what, a, b, tol)


def check(cond, msg):
    assert cond, msg


def params_get():
    return result_geometry(tool("sheet_metal_params", action="get"))


# ---------------------------------------------------------------------------------------------------------------------------------------------
# graph helpers
# ---------------------------------------------------------------------------------------------------------------------------------------------
def graph(*nodes, **extra):
    g = {"schema_version": "0.8.0-draft", "units": "meters", "nodes": [n for part in nodes for n in (part if isinstance(part, list) else [part])]}
    g.update(extra)
    return g


def sk(nid, datum, prims, offset=None):
    ref = {"datum": datum}
    if offset is not None:
        ref["offset"] = offset
    return {"id": nid, "type": "sketch", "ref": ref, "profile": prims}


def rect(w, h):
    return {"kind": "rectangle", "width": w, "height": h}


def line(x1, y1, x2, y2):
    return {"kind": "line", "x1": x1, "y1": y1, "x2": x2, "y2": y2}


def at(x, y, z, hint=None):
    a = {"near": [x, y, z]}
    if hint:
        a["hint"] = hint
    return a


PLATE = [sk("s1", "front", [rect(0.1, 0.06)]),
         {"id": "n2", "type": "sheet_metal", "sketch": "s1", "thickness": 0.0015, "bend_radius": 0.0015, "k_factor": 0.5}]       # 100 x 60 x 1.5, z -1.5 .. 0


def flange(nid, x, y, z=0.0, length=0.025):
    return {"id": nid, "type": "edge_flange", "edge": at(x, y, z), "length": length}


TRAY1 = PLATE + [flange("f1", 0, 0.03)]        # one 25 mm wall on the +y edge: the wall stands y 28.5 .. 30, z 0 .. 25, the bend line runs along x at y = 27


def tray4():
    """Plate + four 25 mm walls, one submission each: every flange renumbers and shortens the edges, so the next anchor is READ (as a planner would) from the live edges."""
    ok(build(graph(PLATE)), "plate")
    for k, (tx, ty) in enumerate([(0, 0.03), (0.05, 0), (0, -0.03), (-0.05, 0)], 1):
        cand = sorted((e for e in edges() if abs(e["mid"][2]) < 1e-4), key=lambda e: abs(e["mid"][0] - tx) + abs(e["mid"][1] - ty))
        check(cand and abs(cand[0]["mid"][0] - tx) + abs(cand[0]["mid"][1] - ty) < 0.006, "no top edge near (%g, %g)" % (tx, ty))
        ok(build(graph(flange("f%d" % k, *cand[0]["mid"])), fresh=False), "flange %d" % k)


# ---------------------------------------------------------------------------------------------------------------------------------------------
# the tests
# ---------------------------------------------------------------------------------------------------------------------------------------------
TESTS = []


def test(name):
    def deco(fn):
        TESTS.append((name, fn))
        return fn
    return deco


@test("I00 THE DOOR AS A MODEL USES IT: submit_feature_graph with a JSON STRING builds plate + louver ('COMPLETED | IR built 3/3'), +158.248 mm3, and the adapter's state_version is in step afterwards")
def t_door():
    g = graph(PLATE, {"id": "f", "type": "forming_tool", "tool": "louver", "at": at(0.01, 0, 0)})
    out = server.submit_feature_graph(json.dumps(g))
    check(out.startswith("COMPLETED | IR built 3/3 nodes"), out)
    near(volume_mm3(), 9158.248, 0.01, "volume (mm3)")
    r = server._call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})
    check(r.get("status") == "COMPLETED", "a normal tool call right after the graph: %r" % r.get("error"))


@test("I01 FORMING TOOL + LOUVER ARRAY: a linear_pattern seeded on the forming_tool node (the compiler reads the feature from result_geometry) - 4 louvers 20 mm apart: 9000 + 4 x 158.248 mm3, 6 + 4 x 14 faces")
def t_array():
    res = ok(build(graph(PLATE, {"id": "f", "type": "forming_tool", "tool": "louver", "at": at(-0.035, 0, 0)},
                         {"id": "arr", "type": "linear_pattern", "feature": "f", "direction": "x", "spacing": 0.02, "count": 4})))
    f = rg(res, "f")
    check(f["verified"] is True and f["feature"] == "louver1" and node(res, "f")["feature"] == "louver1", f)
    near(f["volume_change"] * 1e9, 158.248, 0.01, "one louver")
    near(volume_mm3(), 9000 + 4 * 158.248, 0.01, "the array")
    check(len(faces()) == 6 + 4 * 14, "faces %d" % len(faces()))


@test("I02 A FORMING TOOL THAT WOULD OVERHANG IS REFUSED: a louver at the corner would extend the sheet - the graph stops at the node with FORMING_TOOL_OVERHANG and the part is exactly as it was")
def t_overhang():
    ok(build(graph(PLATE)), "plate")
    v0 = volume_mm3()
    res = build(graph({"id": "f", "type": "forming_tool", "tool": "louver", "at": at(0.049, 0.029, 0)}), fresh=False)
    refused(res, "FORMING_TOOL_OVERHANG", "f")
    check("EXTENDED the sheet" in res.error["message"] and "taken out again" in res.error["message"], res.error["message"])
    near(volume_mm3(), v0, 1e-6, "the part is untouched (mm3)")
    check(len(faces()) == 6, "faces %d" % len(faces()))


@test("I03 GAUGE TABLE: the base flange takes thickness 1.51892 mm, radius 1.905 mm and K 0.5 from the '16 Gauge' row of the steel sample table; the cut list says 16GA.")
def t_gauge():
    out = probe("probe84_pref.cs", "pref=61")
    m = re.search(r"plain='([^']*)'", out)
    check(m and m.group(1), "the gauge-table folder (user preference 61) is not set: " + out[:120])
    table = os.path.join(m.group(1), "sample table - steel - english units.xlsx")
    check(os.path.isfile(table), "the sample table is not there: " + table)
    ok(build(graph(sk("s1", "front", [rect(0.1, 0.06)]),
                   {"id": "n2", "type": "sheet_metal", "sketch": "s1", "gauge_table": table, "gauge": "16 Gauge"})))
    p = params_get()
    near(p["global"]["thickness"], 0.00151892, 1e-8, "thickness")
    near(p["global"]["bend_radius"], 0.001905, 1e-8, "bend radius")
    check(p["cut_list"][0]["properties"]["Sheet Metal Gauge"] == "16GA.", p["cut_list"][0]["properties"].get("Sheet Metal Gauge"))
    bad = build(graph(sk("s1", "front", [rect(0.1, 0.06)]), {"id": "n2", "type": "sheet_metal", "sketch": "s1", "gauge_table": table, "gauge": "99 Gauge"}))
    refused(bad, "GAUGE_NOT_FOUND", "n2")


@test("I04 A WALL ON THE PLATE (edge_flange, length mode): plate + one 25 mm flange = 100 x 60 x 26.5 mm, the anchor resolved to the edge by index")
def t_tray():
    ok(build(graph(TRAY1)))
    z, y, x = extent(2), extent(1), extent(0)
    near(z[0], -0.0015, 1e-9, "bottom"); near(z[1], 0.025, 1e-9, "wall top"); near(y[1], 0.03, 1e-9, "wall outer face"); near(x[1] - x[0], 0.1, 1e-9, "x")


@test("I05 HEM: a closed hem on the wall's outer top edge folds OUTWARD (+1.6 mm in y, 8 faces more, the 0.1 mm gap SolidWorks fixes), height unchanged")
def t_hem():
    res = ok(build(graph(TRAY1, {"id": "h", "type": "hem", "edges": [at(0, 0.03, 0.025)], "hem_type": "closed", "length": 0.01})))
    h = rg(res, "h")
    check(h["verified"] is True and h["feature_error"] == 0, h)
    near(h["body_after"]["size"][1], 0.0616, 5e-5, "y grew by wall + gap"); near(h["body_after"]["size"][2], 0.0265, 5e-5, "height unchanged")
    check(h["body_after"]["faces"] == h["body_before"]["faces"] + 8, h["body_after"])
    bad = build(graph(TRAY1, {"id": "h", "type": "hem", "edges": [at(5, 5, 5)], "length": 0.01}))
    refused(bad, "EDGE_NOT_FOUND", "h")


@test("I06 BREAK CORNER: a fillet on the wall's outer face (+2 faces), verified, definition read back")
def t_break_corner():
    res = ok(build(graph(TRAY1, {"id": "b", "type": "break_corner", "faces": [at(0, 0.03, 0.0125)], "corner_type": "fillet", "distance": 0.003})))
    b = rg(res, "b")
    check(b["verified"] is True and b["feature_error"] == 0 and b["body_after"]["faces"] == b["body_before"]["faces"] + 2, b)
    near(b["definition"]["Distance"], 0.003, 1e-9, "distance")


@test("I07 GUSSET (flat 4 x 10 mm, 20 mm from the bend's start): ~29.9 mm3 and 8 faces, centred at x = -30; the same graph with the two faces on no bend is refused")
def t_gusset():
    faces2 = [at(0, 0, 0), at(0, 0.0285, 0.012)]
    res = ok(build(graph(TRAY1, {"id": "g", "type": "gusset", "faces": faces2, "gusset_type": "flat", "indent_depth": 0.004, "indent_width": 0.01, "position": 0.02})))
    g = rg(res, "g")
    check(g["verified"] is True, g)
    dv = g["volume_added"] * 1e9
    check(29.4 < dv < 30.0, "volume added %.3f mm3" % dv)
    near(g["gusset_centre"][0], -0.03, 1e-9, "centre x")
    check(g["body_after"]["faces"] - g["body_before"]["faces"] == 8, g["body_after"])
    bad = build(graph(TRAY1, {"id": "g", "type": "gusset", "faces": [at(0, 0, 0), at(0, 0, -0.0015)], "indent_depth": 0.004, "indent_width": 0.01, "position": 0.02}))
    refused(bad, "GUSSET_FAILED", "g")


@test("I08 JOG 10 mm UP at x = 0: the far flat's top face is at +10 mm, the bend lines s = h - (2r + t) + BA = 9.034 mm apart, the blank unchanged")
def t_jog():
    res = ok(build(graph(PLATE, {"id": "j", "type": "jog", "line": [[0, -0.03, 0], [0, 0.03, 0]], "fixed": at(-0.03, 0, 0), "offset": 0.01, "angle": PI / 2, "direction": "up"})))
    j = rg(res, "j")
    check(j["verified"] is True and not j.get("problems"), j)
    near(j["line_distance"], 0.01 - 0.0045 + PI / 2 * (0.0015 + 0.5 * 0.0015), 1e-9, "line distance")
    planes = [round(p, 6) for p in j["planes_after"]]
    check(0 in planes and 0.01 in planes, planes)
    near(j["flat_deviation"], 0, 2e-4, "the flat blank is unchanged")
    refused(build(graph(PLATE, {"id": "j", "type": "jog", "line": [[0, -0.03, 0], [0, 0.03, 0]], "fixed": at(-0.03, 0, 0), "offset": 0.003})), "JOG_TOO_SMALL", "j")


@test("I09 MITRE FLANGE on a plate: ONE feature, four mitred bends, 38 faces, 20 mm high + the sheet (21.5 mm), material inside keeps x and y")
def t_miter():
    loop = [at(0, -0.03, 0), at(0.05, 0, 0), at(0, 0.03, 0), at(-0.05, 0, 0)]
    res = ok(build(graph(PLATE, {"id": "m", "type": "miter_flange", "edges": loop, "height": 0.02})))
    m = rg(res, "m")
    check(m["verified"] is True and m["feature_error"] == 0 and m["body_after"]["faces"] == 38, m)
    near(m["body_after"]["size"][2], 0.0215, 5e-5, "height"); near(m["body_after"]["size"][0], 0.1, 5e-5, "x"); near(m["body_after"]["size"][1], 0.06, 5e-5, "y")


@test("I10 MITRE RIM + LIP from profile_points: a wall 20 mm, a rim 10 mm inward, a lip 10 mm up - 12 bends in ONE feature (the cut list counts them)")
def t_miter_rim():
    loop = [at(0, -0.03, 0), at(0.05, 0, 0), at(0, 0.03, 0), at(-0.05, 0, 0)]
    res = ok(build(graph(PLATE, {"id": "m", "type": "miter_flange", "edges": loop, "profile_points": [[0, 0.02], [0.01, 0.02], [0.01, 0.03]]})))
    check(rg(res, "m")["verified"] is True, rg(res, "m"))
    check(int(params_get()["cut_list"][0]["properties"]["Bends"]) == 12, params_get()["cut_list"][0]["properties"].get("Bends"))


@test("I11 CLOSED CORNERS: a four-wall tray built flange by flange (anchors READ from the live edges), then the four wall end faces named in ONE closed_corner node - four 'Closed Corner' features, configured as asked")
def t_closed_corner():
    tray4()
    ends = [f for f in faces() if f.get("planar") and abs(f["area"] - 3.5e-5) < 1e-6
            and ((abs(f["normal"][0]) > 0.5 and abs(f["point"][0]) < 0.049) or (abs(f["normal"][1]) > 0.5 and abs(f["point"][1]) < 0.029))]
    check(len(ends) == 4, "expected 4 inner end faces, found %d" % len(ends))
    res = ok(build(graph({"id": "c", "type": "closed_corner", "faces": [at(*f["point"]) for f in ends], "corner_type": "overlap", "gap": 0.0000762, "overlap_ratio": 1.0}), fresh=False))
    c = rg(res, "c")
    check(c["verified"] is True and len(c["created"]) == 4 and c["closed_corners_after"] == 4, c)
    check(all(k["configured"] for k in c["created"]), c["created"])


@test("I12 LOFTED BEND: two half circles (r 20 -> 10 mm, 30 mm apart) become a half-cone transition - 3 bends, one body, 1.5 mm sheet, 30 mm high; the profiles are named by node id")
def t_lofted():
    arc = lambda r: [{"kind": "arc", "cx": 0, "cy": 0, "x1": -r, "y1": 0, "x2": r, "y2": 0, "dir": 1}]      # noqa: E731
    res = ok(build(graph(sk("a", "front", arc(0.02)), sk("b", "front", arc(0.01), offset=0.03),
                         {"id": "l", "type": "lofted_bend", "profiles": ["a", "b"], "thickness": 0.0015, "facets": "number_of_bends", "number_of_bends": 4})))
    r = rg(res, "l")
    check(r["verified"] is True and r["bends"] == 3 and r["bodies"] == 1, r)
    near(r["sheet_thickness"], 0.0015, 1e-9, "sheet thickness")
    slant = math.sqrt(0.03 ** 2 + 0.01 ** 2)
    want = PI * (0.02 + 0.01) * slant / 2 * 0.0015
    check(abs(r["volume"] - want) / want < 0.05, "volume %.1f mm3, half cone shell %.1f mm3" % (r["volume"] * 1e9, want * 1e9))
    check(0.0304 < r["body_after"]["size"][2] < 0.0306, r["body_after"]["size"])


@test("I13 SWEPT FLANGE: an L profile (15 + 20 mm) swept along a 100 mm line is the first feature of the part - 4880.144 mm3, 1 bend, blank (29 + pi/2 (r + K t)) x 100 mm")
def t_swept():
    res = ok(build(graph(sk("pa", "top", [line(0, 0, 0.1, 0)]), sk("pr", "right", [line(0.015, 0, 0, 0), line(0, 0, 0, 0.02)]),
                         {"id": "w", "type": "swept_flange", "profile": "pr", "path": "pa", "thickness": 0.0015})))
    w = rg(res, "w")
    check(w["verified"] is True and not w.get("problems") and w["bends"] == 1, w)
    near(w["volume_added"] * 1e9, 4880.144, 0.01, "volume (mm3)")
    near(w["blank"][0], 0.1, 2e-5, "blank length"); near(w["blank"][1], 0.029 + PI / 2 * (0.0015 + w["k_factor"] * 0.0015), 2e-5, "blank width")


@test("I14 TAB AND SLOT on two bodies: a plate and a wall (extrude merge:false) - 3 tabs of 10 mm, 5 mm from each end: the wall gains 67.5 mm3, the plate loses 79.56 mm3, 6 -> 18 faces each")
def t_tab_slot():
    res = ok(build(graph(sk("s1", "front", [rect(0.1, 0.06)]), {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.0015, "reversed": True},
                         sk("s2", "front", [rect(0.08, 0.0015)]), {"id": "e2", "type": "extrude", "sketch": "s2", "operation": "boss", "depth": 0.03, "merge": False},
                         {"id": "t", "type": "tab_slot", "edge": at(0, 0.00075, 0), "slot_face": at(0.02, 0.02, 0), "tab_length": 0.01, "count": 3,
                          "start_offset": 0.005, "end_offset": 0.005, "slot_clearance": 0.0002})))
    t = rg(res, "t")
    check(t["verified"] is True and not t.get("problems") and len(t["features"]) == 2, t)
    near(t["tab_volume_added"] * 1e9, 67.5, 1e-3, "wall gains"); near(t["slot_volume_removed"] * 1e9, 79.56, 1e-3, "plate loses")
    check(t["before"]["tab_body"]["faces"] == 6 and t["after"]["tab_body"]["faces"] == 18 and t["after"]["slot_body"]["faces"] == 18, t["after"])


@test("I15 MOVE FACE: the top face of a 20 x 20 x 10 mm block moves +3 mm (13 mm high, +1200 mm3); the distance is the dimension D1@Move Face1")
def t_move_face():
    res = ok(build(graph(sk("s1", "front", [rect(0.02, 0.02)]), {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.01},
                         {"id": "v", "type": "move_face", "faces": [at(0, 0, 0.01)], "distance": 0.003})))
    v = rg(res, "v")
    check(v["verified"] is True and v["dimensions"] == ["D1@Move Face1"], v)
    near(v["body_after"]["size"][2], 0.013, 1e-9, "height"); near(v["volume_change"] * 1e9, 1200, 1e-3, "volume change (mm3)")


@test("I16 UNFOLD -> A RADIUS PER FILLET EDGE -> FOLD: the tray's blank is flattened (thickness = the sheet's), its four corners get radii 5 / 5 / 10 / 10 mm in ONE multiple-radius fillet (volume = closed form), and the fold returns the folded size")
def t_unfold_fillet_fold():
    ok(build(graph(TRAY1)), "tray")
    size0 = (extent(0), extent(1), extent(2))
    ok(build(graph({"id": "u", "type": "unfold", "fixed_face": at(0, 0, 0)}), fresh=False), "unfold")
    v_flat = volume_mm3()
    vertical = [e for e in edges() if abs(e["start"][0] - e["end"][0]) < 1e-7 and abs(e["start"][1] - e["end"][1]) < 1e-7 and abs(abs(e["start"][2] - e["end"][2]) - 0.0015) < 1e-7]
    ys = [e["mid"][1] for e in vertical]       # the unfolded blank also has a vertical edge where each bend line met the outline: the corners are the ones at the extreme ends
    corners = sorted((e for e in vertical if abs(e["mid"][1] - min(ys)) < 1e-7 or abs(e["mid"][1] - max(ys)) < 1e-7), key=lambda e: (e["mid"][0], e["mid"][1]))
    check(len(corners) == 4, "the flat blank has %d corner edges (of %d vertical ones)" % (len(corners), len(vertical)))
    radii = [0.005, 0.005, 0.01, 0.01]
    res = ok(build(graph({"id": "fi", "type": "fillet", "edges": [dict(at(*c["mid"]), radius=r) for c, r in zip(corners, radii)]},
                         {"id": "fo", "type": "fold", "fixed_face": at(0, 0, 0)}), fresh=False), "fillet + fold")
    fi = rg(res, "fi")
    check(fi["multi_radius"] is True and fi["edges_in_feature"] == 4 and fi["verified"] is True, fi)
    fo = rg(res, "fo")
    check(fo["verified"] is True, fo)
    removed = sum((1 - PI / 4) * r * r for r in radii) * 0.0015 * 1e9
    near(v_flat - volume_mm3(), removed, 0.05, "the four rounded corners remove (1 - pi/4) r2 t each (mm3; K = 0.5 keeps the volume through the fold)")
    size1 = (extent(0), extent(1), extent(2))
    for a, b in zip(size0, size1):
        near(a[0], b[0], 1e-9, "min back"); near(a[1], b[1], 1e-9, "max back")


@test("I17 UNFOLD -> SURFACE EXTRUDE -> SURFACE CUT -> FOLD: a corner triangle (5 mm legs) is trimmed off the flat blank - removes 12.5 x t = 18.75 mm3 and the fold carries the cut")
def t_trim():
    ok(build(graph(TRAY1)), "tray")
    v_folded = volume_mm3()
    ok(build(graph({"id": "u0", "type": "unfold", "fixed_face": at(0, 0, 0)}), fresh=False), "unfold")
    corner = sorted((e for e in edges() if abs(e["start"][0] - e["end"][0]) < 1e-7 and abs(e["start"][1] - e["end"][1]) < 1e-7 and abs(abs(e["start"][2] - e["end"][2]) - 0.0015) < 1e-7
                     and e["mid"][0] > 0 and e["mid"][1] > 0), key=lambda e: -(e["mid"][0] + e["mid"][1]))[0]
    cx, cy = corner["mid"][0], corner["mid"][1]
    x1, y1, x2, y2 = cx - 0.008, cy + 0.003, cx + 0.003, cy - 0.008        # the line x + y = cx + cy - 5 mm, 3 mm beyond the outline at both ends

    def trim(flip):
        return graph(TRAY1, {"id": "u", "type": "unfold", "fixed_face": at(0, 0, 0)}, sk("q", "front", [line(x1, y1, x2, y2)]),
                     {"id": "se", "type": "surface_extrude", "sketch": "q", "depth": 0.005, "reverse": True},
                     {"id": "sc", "type": "surface_cut", "surface": "se", "flip": flip, "max_removed_volume": 1e-7},
                     {"id": "fo", "type": "fold", "fixed_face": at(0, 0, 0)})
    res = build(trim(False))
    if res.status == "FAILED" and res.error["code"] == "CUT_REMOVED_TOO_MUCH":
        res = build(trim(True))                 # the planner's recovery: flip the side once and resubmit
    ok(res, "trim")
    sc = rg(res, "sc")
    near(sc["removed_volume"] * 1e9, 12.5 * 1.5, 0.01, "the corner triangle (mm3)")
    check(rg(res, "fo")["verified"] is True, rg(res, "fo"))
    near(volume_mm3(), v_folded - 12.5 * 1.5, 0.02, "folded volume = original - the triangle")


@test("I18 SETTINGS + FLAT-PATTERN OPTIONS: sheet_metal_settings sets the global K-factor (0.42) and flat_pattern splits the flat faces (merge_faces false), both read back")
def t_settings():
    res = ok(build(graph(PLATE, {"id": "st", "type": "sheet_metal_settings", "k_factor": 0.42},
                         {"id": "fp", "type": "flat_pattern", "merge_faces": False})))
    check(rg(res, "st")["verified"] is True, rg(res, "st"))
    near(params_get()["global"]["bend_allowance"]["k_factor"], 0.42, 1e-9, "global K-factor")
    check(rg(res, "fp")["options"]["merge_faces"] is False, rg(res, "fp"))


@test("I19 CONVERT TO SHEET METAL AS A GRAPH (needs SolidWorks on screen): box -> delete the top face -> convert (4 bends) -> folded 103.048 x 63.048 x 31.524 mm; SKIPPED when another window covers the graphics area")
def t_convert():
    edges4 = [at(0.05, 0, 0), at(0, 0.03, 0), at(-0.05, 0, 0), at(0, -0.03, 0)]
    res = ok(build(graph(sk("s1", "front", [rect(0.1, 0.06)]), {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.03},
                         {"id": "d", "type": "delete_face", "faces": [at(0, 0, 0.03)]},
                         {"id": "cv", "type": "convert_to_sheet_metal", "fixed_face": at(0, 0, 0), "thickness": 0.001524, "bend_radius": 0.004572,
                          "reverse_thickness": "on", "steps": [{"list": "bend", "edges": edges4}]})), "convert")
    cv = rg(res, "cv")
    check(cv["verified"] is True and cv["accepted"] is True, cv)
    check(int(params_get()["cut_list"][0]["properties"]["Bends"]) == 4, "bends")
    x, y, z = extent(0), extent(1), extent(2)
    near((x[1] - x[0]) * 1000, 103.048, 1e-3, "folded X"); near((y[1] - y[0]) * 1000, 63.048, 1e-3, "folded Y"); near((z[1] - z[0]) * 1000, 31.524, 1e-3, "folded Z")


@test("I20 FOLD FROM A FLAT DXF: the tray's own flat DXF (exported from a saved part) builds a part with the DXF's blank and one bend - from_flat as the first node")
def t_from_flat():
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="solidpilot_ir_"))
    ok(build(graph(TRAY1)), "tray")
    saved = server._call_raw("save_document", {"file_path": str(tmp / "flat_source.SLDPRT")})
    check(saved.get("status") == "COMPLETED", saved.get("error"))
    dxf = tmp / "flat_source.dxf"
    r = tool("flat_pattern_control", dxf_path=str(dxf), dxf_flags=5)
    check(r.get("status") == "COMPLETED" and dxf.exists(), r.get("error"))
    res = ok(build(graph({"id": "ff", "type": "from_flat", "dxf_path": str(dxf), "thickness": 0.0015, "bend_radius": 0.0015, "k_factor": 0.5})), "from_flat")
    ff = rg(res, "ff")
    check(ff["verified"] is True and ff["bends"] == 1 and ff["bends_expected"] == 1, ff)
    near(ff["flat_deviation"], 0, 2e-4, "the part's own flat pattern has the DXF's size")
    bad = build(graph(PLATE, {"id": "ff", "type": "from_flat", "dxf_path": str(dxf), "thickness": 0.0015, "bend_radius": 0.0015}))
    check(bad.status == "FAILED" and bad.error["code"] == "VALIDATION_FAILED" and "FIRST node" in bad.error["message"], bad.error)


@test("I21 THE PLANNER'S RECOVERY LOOP: a graph with a typo'd field is rejected BEFORE anything is built (no document change), the corrected graph builds")
def t_recovery():
    ok(build(graph(PLATE)), "plate")
    v0, sv0 = volume_mm3(), server._resync_state_version()
    res = build(graph({"id": "f", "type": "forming_tool", "tool": "louver", "at": at(0.01, 0, 0), "rotaton": 0.5}), fresh=False)
    err = refused(res, "VALIDATION_FAILED")
    check("unknown field 'rotaton'" in err["message"], err["message"])
    near(volume_mm3(), v0, 1e-6, "nothing was built")
    check(server._resync_state_version() == sv0, "a rejected graph executes nothing")
    ok(build(graph({"id": "f", "type": "forming_tool", "tool": "louver", "at": at(0.01, 0, 0), "rotation": 0.5}), fresh=False), "corrected")
    near(volume_mm3(), v0 + 158.248, 0.01, "the corrected graph built")


@test("I22 CORNER RELIEF: on a four-wall tray one corner_relief node relieves the four two-bend corners in ONE feature (circular 3 mm: 42 -> 46 faces, 20103.843 -> 19974.975 mm3; obround 6 x 3: 70 faces, 20011.248 mm3); a 2.9 mm circle builds nothing (the part is untouched), a three-bend relief is not in the vocabulary")
def t_corner_relief():
    tray4()
    check(len(faces()) == 42, "the IR-built tray has %d faces (the fixture's has 42)" % len(faces()))
    v0 = volume_mm3()
    near(v0, 20103.843, 0.01, "the IR-built tray is the fixture's tray (mm3)")
    err = refused(build(graph({"id": "cr", "type": "corner_relief", "relief": "circular", "width": 0.0029}), fresh=False), "CORNER_RELIEF_FAILED", "cr")
    check("at least 2 x the thickness" in err["message"], err["message"])
    near(volume_mm3(), v0, 1e-6, "a relief SolidWorks would not build leaves the part as it was (mm3)")
    sv0 = server._resync_state_version()
    err = refused(build(graph({"id": "cr", "type": "corner_relief", "relief": "bend_waist", "width": 0.003}), fresh=False), "VALIDATION_FAILED")
    check("'relief' must be one of" in err["message"], err["message"])
    check(server._resync_state_version() == sv0, "a graph rejected by validation executes nothing")
    res = ok(build(graph({"id": "cr", "type": "corner_relief", "relief": "circular", "width": 0.003}), fresh=False), "corner relief")
    r = rg(res, "cr")
    check(r["verified"] is True and not r.get("problems") and r["corners_collected"] == 4 and r["feature_type"] == "CornerRelief", r)
    check(r["body_before"]["faces"] == 42 and r["body_after"]["faces"] == 46, r["body_after"])
    check(node(res, "cr")["feature"] == r["feature"] and r["feature"].startswith("Corner Relief"), (node(res, "cr")["feature"], r["feature"]))
    near(volume_mm3(), 19974.975, 0.01, "volume after the circular relief (mm3)")
    check(r["definition"]["corner_count"] == 4 and all(k["relief_type"] == 0 for k in r["definition"]["corners"]), r["definition"])
    tray4()
    res = ok(build(graph({"id": "cr", "type": "corner_relief", "relief": "obround", "width": 0.003, "length": 0.006}), fresh=False), "obround relief")
    r = rg(res, "cr")
    check(r["verified"] is True and r["body_after"]["faces"] == 70, r)
    near(volume_mm3(), 20011.248, 0.01, "volume after the obround relief (mm3)")


@test("I23 SURFACE PREPARATION AS A GRAPH: a 100 x 60 x 30 mm box, delete_face on its top (a surface body), offset_surface inward by 1.5 mm - x and y shrink by 3 mm, z by 1.5 mm (the top is open), the original is hidden; outward the same amounts grow; a point off the body is refused")
def t_surface_prep():
    box = [sk("s1", "front", [rect(0.1, 0.06)]), {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.03},
           {"id": "d", "type": "delete_face", "faces": [at(0, 0, 0.03)]}]
    res = ok(build(graph(box, {"id": "o", "type": "offset_surface", "body": at(0, 0, 0), "distance": 0.0015, "direction": "inward"})), "offset inward")
    o = rg(res, "o")
    check(o["verified"] is True and o["hidden_original"] is True, o)
    near(o["size_change"][0], -0.003, 1e-7, "x shrinks by 2 d"); near(o["size_change"][1], -0.003, 1e-7, "y shrinks by 2 d"); near(o["size_change"][2], -0.0015, 1e-7, "z shrinks by d (the top is open)")
    res = ok(build(graph(box, {"id": "o", "type": "offset_surface", "body": at(0, 0, 0), "distance": 0.0015, "direction": "outward"})), "offset outward")
    o = rg(res, "o")
    near(o["size_change"][0], 0.003, 1e-7, "x grows by 2 d"); near(o["size_change"][2], 0.0015, 1e-7, "z grows by d")
    refused(build(graph(box, {"id": "o", "type": "offset_surface", "body": at(9, 9, 9), "distance": 0.001})), "FACE_NOT_FOUND", "o")
    refused(build(graph(box, {"id": "o", "type": "offset_surface", "body": at(0, 0, 0), "distance": 0})), "VALIDATION_FAILED")


# ---------------------------------------------------------------------------------------------------------------------------------------------
# runner
# ---------------------------------------------------------------------------------------------------------------------------------------------
def main():
    try:
        ad = pipe("activeDoc")
    except Exception as ex:     # noqa: BLE001
        print("The SolidPilot Bridge does not answer on its pipe (%s): is SolidWorks running with the add-in loaded?" % ex)
        return 99
    if not args.force and not ad.endswith("none"):
        print("A document is open in SolidWorks (activeDoc = %s): the suite closes every document - close them first or pass --force." % ad)
        return 98
    h = httpx.get(args.base + "/health", timeout=30).json()
    print("== IR integration tests (schema 0.8)  %s  | SolidWorks %s" % (args.base, h.get("swVersion") or h.get("activeDocument")))
    done = {"PASS": 0, "FAIL": 0, "SKIP": 0}
    for name, fn in TESTS:
        if args.only and not re.search(args.only, name):
            continue
        t0 = time.time()
        status, why = "PASS", ""
        try:
            fn()
        except Skip as s:
            status, why = "SKIP", str(s)
        except Exception as ex:     # noqa: BLE001 - a test runner reports everything
            status, why = "FAIL", "%s: %s" % (type(ex).__name__, str(ex)[:600])
            if os.environ.get("IR_TEST_TRACE"):
                traceback.print_exc()
        done[status] += 1
        print("%s  %-190s %6d ms  %s" % (status, name[:190], int((time.time() - t0) * 1000), why))
    try:
        server._call_raw("close_document", {"save": False, "close_all": True})
    except Exception:           # noqa: BLE001
        pass
    print("\n%d tests, %d passed, %d failed, %d skipped" % (sum(done.values()), done["PASS"], done["FAIL"], done["SKIP"]))
    return done["FAIL"]


if __name__ == "__main__":
    sys.exit(main())
