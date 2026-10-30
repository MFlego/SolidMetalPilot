"""IR coverage manifest: how every tool of the execution contract is reached from a Feature Graph.

The IR is the planned ONLY door for building (the low-level tools collapse beneath it), so a tool nobody
can reach through a graph is a capability the planner does not have. This module states, for each of the
execution layer's tools, either

  REACHABLE[tool] = the IR node types (or graph-level doors) whose lowering calls it, or
  EXEMPT[tool]    = the reason it is deliberately outside the build IR (reads, document management,
                    editing an existing part, drawings, outputs, the IR doors themselves).

tests/test_sheetmetal_nodes.py holds the manifest to the contract: every tool of
execution/solidworks/contracts/tool-schemas.json must be classified exactly once, every reachable node
must be a registered IR node (the table-driven sheet-metal nodes are also compiled and shown to call the
tool). Adding a tool to the execution layer without deciding its IR status fails the build.

Pure data - nothing here imports the adapter or talks to SolidWorks.
"""

# Doors that are not nodes: the adapter opens the document (submit_feature_graph fresh_document) and the
# graph-level `material` is applied after the last node.
NON_NODE_DOORS = frozenset(("fresh_document", "graph.material"))

REACHABLE = {
    # --- the part-modelling core (IR v0.5 - v0.7) ----------------------------------------------------------------
    "open_new_part": ("fresh_document",),
    "open_new_assembly": ("fresh_document",),
    "create_sketch": ("sketch", "box", "hole", "edge_flange"),
    "add_sketch_entity": ("sketch", "box", "hole", "edge_flange"),
    "sketch_fully_define": ("sketch", "hole"),
    "sketch_convert_entities": ("sketch",),
    "add_reference_geometry": ("sketch", "circular_pattern"),
    "extrude_feature": ("box", "extrude", "revolve", "sweep", "loft", "hole"),
    "create_rib": ("rib",),
    "add_edge_feature": ("fillet", "chamfer"),
    "create_pattern": ("linear_pattern", "circular_pattern", "mirror"),
    "set_part_material": ("graph.material",),
    "sheet_metal_feature": ("sheet_metal", "sketched_bend", "edge_flange"),
    "insert_component": ("component",),
    "add_mate": ("mate",),
    # --- the sheet-metal DFM vocabulary (IR v0.8) ----------------------------------------------------------------
    "sheet_metal_hem": ("hem",),
    "sheet_metal_break_corner": ("break_corner",),
    "sheet_metal_corners": ("closed_corner",),
    "sheet_metal_corner_relief": ("corner_relief",),
    "sheet_metal_miter_flange": ("miter_flange",),
    "sheet_metal_gusset": ("gusset",),
    "sheet_metal_jog": ("jog",),
    "sheet_metal_tab_slot": ("tab_slot",),
    "move_face": ("move_face",),
    "sheet_metal_forming_tool": ("forming_tool",),
    "sheet_metal_lofted_bend": ("lofted_bend",),
    "sheet_metal_swept_flange": ("swept_flange",),
    "sheet_metal_from_flat": ("from_flat",),
    "sheet_metal_fold": ("unfold", "fold"),
    "flat_pattern_control": ("flat_pattern",),
    "sheet_metal_params": ("sheet_metal_settings",),
    "sheet_metal_convert": ("convert_to_sheet_metal",),
    "delete_face": ("delete_face",),
    "offset_surface": ("offset_surface",),
    "surface_extrude": ("surface_extrude",),
    "surface_cut": ("surface_cut",),
}

_EDIT = "edits an EXISTING part (the IR builds from scratch; variants are made with the edit tools after a build)"
_READ = "read-only analysis - the compiler's resolver calls the readers itself, a graph never needs to"
_DOC = "document management, not building"
_DRAW = "drawing documents are outside the part / assembly build IR (analyze_drawing is the drawing -> part front end)"
_OUT = "an OUTPUT of a finished part (written after the build, from a saved document)"
_DOOR = "an IR door or verifier itself, not a thing a graph builds"
_FROZEN = "the IR deliberately carries no constraints or dimensions: sketches close by shared endpoints (frozen-coordinate discipline)"
_PARAM = ("parametric layer (named dimensions, equations, document units): applied with the tools after the build - the IR carries VALUES; "
          "its `_params` side table is the lifted parameter map (recorded gap: no equations in the graph yet)")

EXEMPT = {
    "open_document": _DOC, "close_document": _DOC, "save_document": _DOC, "activate_document": _DOC, "ensure_ready": "session readiness check, not building",
    "verify_state": _READ, "analyze_model": _READ, "analyze_drawing": _READ + " (and its drawing build mode is a separate door)", "analyze_slddrw_test": _READ,
    "get_selection": _READ, "analyze_assembly": _READ,
    "add_sketch_entities": "the batch form of add_sketch_entity; the compiler issues one entity per profile primitive",
    "add_sketch_constraint": _FROZEN, "add_dimension": _FROZEN,
    "edit_sketch": _EDIT, "edit_feature": _EDIT, "feature_data": _EDIT + " - typed read/write of a feature definition", "modify_dimension": _EDIT,
    "create_drawing": _DRAW, "add_drawing_view": _DRAW, "add_flat_pattern_view": _DRAW, "add_drawing_dimension": _DRAW, "auto_dimension_drawing": _DRAW,
    "auto_center_marks": _DRAW, "add_hole_callout": _DRAW, "add_section_view": _DRAW,
    "export_document": _OUT, "batch_export": _OUT, "save_body_as_part": _OUT,
    "document_units": _PARAM, "equations": _PARAM, "sketch_center_rectangle": _PARAM + "; a plain rectangle profile is the sketch node",
    "get_recipe": _DOOR, "save_analysis": _DOOR, "rebuild_from_ir": _DOOR, "submit_feature_graph": _DOOR, "compare_parts": _DOOR, "compare_assemblies": _DOOR,
}
