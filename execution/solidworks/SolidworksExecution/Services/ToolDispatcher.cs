using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // Routes a tool request to its SolidWorksService method. Shared by BOTH hosts - the out-of-process OWIN controller (ToolController) and the in-process SolidPilot Bridge add-in
    // (SolidPilotBridge.Tools) - so the tool surface cannot drift between them. Returns null for an unknown tool so the caller can emit its own "unknown tool" response.
    public static class ToolDispatcher
    {
        public static ExecutionResponse Dispatch(SolidWorksService service, ToolRequest request)
        {
            switch (request.Tool)
            {
                case "open_new_part":          return service.OpenNewPart(request);
                case "open_document":          return service.OpenDocument(request);
                case "create_sketch":          return service.CreateSketch(request);
                case "add_sketch_entity":      return service.AddSketchEntity(request);
                case "add_sketch_entities":    return service.AddSketchEntities(request);
                case "add_dimension":          return service.AddDimension(request);
                case "add_sketch_constraint":  return service.AddSketchConstraint(request);
                case "add_edge_feature":       return service.AddEdgeFeature(request);
                case "create_rib":             return service.CreateRib(request);
                case "create_drawing":         return service.CreateDrawing(request);
                case "add_drawing_view":       return service.AddDrawingView(request);
                case "add_flat_pattern_view":  return service.AddFlatPatternView(request);
                case "add_drawing_dimension":  return service.AddDrawingDimension(request);
                case "auto_dimension_drawing": return service.AutoDimensionDrawing(request);
                case "auto_center_marks":      return service.AutoCenterMarks(request);
                case "add_hole_callout":       return service.AddHoleCallout(request);
                case "add_section_view":       return service.AddSectionView(request);
                case "save_document":          return service.SaveDocument(request);
                case "export_document":        return service.ExportDocument(request);
                case "batch_export":           return service.BatchExport(request);
                case "extrude_feature":        return service.ExtrudeFeature(request);
                case "verify_state":           return service.VerifyState(request);
                case "close_document":         return service.CloseDocument(request);
                case "analyze_model":          return service.AnalyzeModel(request);
                case "analyze_slddrw_test":    return service.AnalyzeDrawing(request);
                case "get_selection":          return service.GetSelection(request);
                case "edit_sketch":            return service.EditSketch(request);
                case "add_reference_geometry": return service.AddReferenceGeometry(request);
                case "create_pattern":         return service.CreatePattern(request);
                case "sketch_pattern":         return service.SketchPattern(request);
                case "set_part_material":      return service.SetPartMaterial(request);
                case "sheet_metal_feature":    return service.SheetMetalFeature(request);
                case "modify_dimension":       return service.ModifyDimension(request);
                case "edit_feature":           return service.EditFeature(request);
                case "activate_document":      return service.ActivateDocument(request);
                case "analyze_assembly":       return service.AnalyzeAssembly(request);
                case "open_new_assembly":      return service.OpenNewAssembly(request);
                case "insert_component":       return service.InsertComponent(request);
                case "add_mate":               return service.AddMateTool(request);
                case "save_body_as_part":      return service.SaveBodyAsPart(request);
                case "feature_data":           return service.FeatureData(request);
                case "sheet_metal_params":     return service.SheetMetalParams(request);
                case "sheet_metal_corners":    return service.SheetMetalCorners(request);
                case "flat_pattern_control":   return service.FlatPatternControl(request);
                case "sheet_metal_hem":        return service.SheetMetalHem(request);
                case "sheet_metal_break_corner": return service.SheetMetalBreakCorner(request);
                case "sheet_metal_miter_flange": return service.SheetMetalMiterFlange(request);
                case "document_units":         return service.DocumentUnits(request);
                case "equations":              return service.Equations(request);
                case "sketch_center_rectangle": return service.SketchCenterRectangle(request);
                case "sketch_fully_define":    return service.SketchFullyDefine(request);
                case "sketch_convert_entities": return service.SketchConvertEntities(request);
                case "delete_face":            return service.DeleteFace(request);
                case "offset_surface":         return service.OffsetSurface(request);
                case "sheet_metal_fold":       return service.SheetMetalFold(request);
                case "surface_extrude":        return service.SurfaceExtrude(request);
                case "surface_cut":            return service.SurfaceCut(request);
                case "sheet_metal_from_flat":  return service.SheetMetalFromFlat(request);
                case "sheet_metal_gusset":     return service.SheetMetalGusset(request);
                case "sheet_metal_jog":        return service.SheetMetalJog(request);
                case "sheet_metal_lofted_bend": return service.SheetMetalLoftedBend(request);
                case "sheet_metal_tab_slot":   return service.SheetMetalTabSlot(request);
                case "move_face":              return service.MoveFace(request);
                case "sheet_metal_swept_flange": return service.SheetMetalSweptFlange(request);
                case "sheet_metal_forming_tool": return service.SheetMetalFormingTool(request);
                case "sheet_metal_corner_relief": return service.SheetMetalCornerRelief(request);
                default:                       return null;
            }
        }
    }
}