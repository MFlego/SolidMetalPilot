"""End-to-end test of the in-process tools through the REAL Python MCP adapter and compiler (no mocks).

Imports adapters/claude/server.py (the module behind the MCP tools) with EXECUTION_BASE_URL pointed at the in-process REST façade, then runs the full round trip:
  ensure_ready -> submit_feature_graph (IR: box + through-hole) -> analyze_model -> save_document -> save_analysis -> rebuild_from_ir -> compare_parts -> close.
Run it with a Python that has the adapter's requirements and the pycompiler installed, on a SolidWorks session with NOTHING else open (it closes the documents it creates):
    python e2e_adapter.py [--base http://localhost:5000]
Exit code 0 = every step reported success and the rebuilt part matches the original.
"""
import argparse
import glob
import json
import os
import pathlib
import sys
import tempfile
import time

ap = argparse.ArgumentParser()
ap.add_argument("--base", default="http://localhost:5000")
args = ap.parse_args()
os.environ["EXECUTION_BASE_URL"] = args.base          # must be set before the adapter's config module is imported

REPO = pathlib.Path(__file__).resolve().parents[4]    # tests -> SolidPilotBridge -> solidworks -> execution -> repo root
sys.path.insert(0, str(REPO / "adapters" / "claude"))
import httpx  # noqa: E402
import server  # noqa: E402  (the real MCP adapter module)

EXAMPLE = {
    "schema_version": "0.0.1-draft-ir-exp",
    "units": "meters",
    "nodes": [
        {"id": "n1", "type": "box", "ref": {"datum": "top"}, "width": 0.05, "depth": 0.05, "height": 0.05},
        {"id": "n2", "type": "hole", "ref": {"node_face": {"node": "n1", "selector": "top"}, "position": "center"},
         "diameter": 0.01, "depth": "through_all"},
    ],
}

failures = []


def step(name, fn):
    t = time.time()
    try:
        out = fn()
    except Exception as ex:                                   # noqa: BLE001 - a test driver reports everything
        out = f"EXCEPTION {type(ex).__name__}: {ex}"
    ms = int((time.time() - t) * 1000)
    text = out if isinstance(out, str) else json.dumps(out)
    bad = text.startswith("FAILED") or text.startswith("EXCEPTION") or " FAILED " in text[:80]
    print(f"[{'FAIL' if bad else ' ok '}] {name:<28} {ms:>6} ms  {text[:330].replace(chr(10), ' ')}")
    if bad:
        failures.append(name)
    return out


def active_document():
    return httpx.get(args.base + "/health", timeout=30).json().get("activeDocument")


tmp = pathlib.Path(tempfile.mkdtemp(prefix="solidpilot_e2e_"))
part_path = tmp / "e2e_box.SLDPRT"
print(f"base {args.base}   work folder {tmp}")

try:
    step("ensure_ready", server.ensure_ready)
    step("submit_feature_graph", lambda: server.submit_feature_graph(json.dumps(EXAMPLE)))
    if failures:
        print("-- the build failed: nothing after it is meaningful, going straight to cleanup")
    else:
        step("analyze mass_properties", lambda: server.analyze_model("mass_properties"))
        step("analyze geometry", lambda: server.analyze_model("geometry"))
        step("analyze features (recipe)", lambda: server.analyze_model("features"))
        step("save_document", lambda: server.save_document(str(part_path)))
        step("save_analysis", lambda: server.save_analysis(str(part_path)))

        artifacts = glob.glob(str(tmp / ".solidpilot" / "*.analysis.json"))
        if not artifacts:
            failures.append("artifact missing")
            print("[FAIL] no analysis artifact was written")
        else:
            art_path = artifacts[0]
            with open(art_path, "r", encoding="utf-8-sig") as fh:
                art = json.load(fh)
            art["ir"] = {"graph": EXAMPLE}                # the role the AI plays in the real flow: the IR that reproduces this part
            with open(art_path, "w", encoding="utf-8") as fh:
                json.dump(art, fh)
            step("rebuild_from_ir", lambda: server.rebuild_from_ir(art_path))
            rebuilt_title = active_document()
            out = step("compare_parts", lambda: server.compare_parts(str(part_path), rebuilt_title))
            if "verified" in str(out).lower() and "false" in str(out).lower().split("verified")[1][:12]:
                failures.append("compare_parts verdict")
finally:
    # always leave the session as found: this test owns every document that is open (see the docstring)
    step("close all documents", lambda: server.close_document(False, True))

print("---")
print("RESULT:", "ALL STEPS OK" if not failures else "FAILED STEPS: " + ", ".join(failures))
sys.exit(1 if failures else 0)
