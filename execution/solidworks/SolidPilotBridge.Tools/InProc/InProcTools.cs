// Tools that exist ONLY in the in-process host. They need to live inside SolidWorks' own process - here: reaching the WPF visual tree of SolidWorks' PropertyManagers directly through
// HwndSource - so the out-of-process execution layer cannot offer them and does not compile this folder. ToolsHost tries this dispatcher first and falls back to the shared ToolDispatcher.
// The classes extend the shared SolidWorksService (same assembly), so they use the same prologue (idempotency, state_version, error mapping) as every other tool.
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public static class InProcTools
    {
        /// <summary>The reply of the in-process-only tool, or null when <paramref name="request"/> names none of them.</summary>
        public static ExecutionResponse Dispatch(SolidWorksService service, ToolRequest request)
        {
            switch (request.Tool)
            {
                case "ui_inspect": return service.UiInspect(request);
                case "sheet_metal_convert": return service.SheetMetalConvert(request);
                case "curve_pattern": return service.CurvePattern(request);
                default: return null;
            }
        }
    }
}
