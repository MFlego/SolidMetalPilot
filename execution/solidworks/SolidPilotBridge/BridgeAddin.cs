// SolidPilot Bridge add-in entry point (ISwAddin).
// Design inspired by AngelSix's SolidDna (MIT, https://github.com/angelsix/solidworks-api): the add-in lifecycle (ConnectToSW / DisconnectFromSW) and the idea of
// marshalling work onto SolidWorks' UI thread with a control created on that thread. No SolidDna code is copied.
using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace SolidPilot.Bridge
{
    [ComVisible(true)]
    [Guid(BridgeAddin.AddinGuid)]
    [ProgId("SolidPilot.Bridge")]
    // Default class interface (AutoDispatch) on purpose: SetAddinCallbackInfo2 marshals `this` as IDispatch. With ClassInterfaceType.None the CCW has no IDispatch and the call throws
    // InvalidCastException (seen in bridge.log 2026-10-26).
    public class BridgeAddin : ISwAddin
    {
        public const string AddinGuid = "6F0B8E2A-3C41-4D6B-9A57-1E2D5C7A9B10";
        private SldWorks _sw;
        private BridgeEvents _events;
        private PipeServer _pipe;

        public bool ConnectToSW(object thisSw, int cookie)
        {
            try
            {
                _sw = (SldWorks)thisSw;
                _sw.SetAddinCallbackInfo2(0, this, cookie);
                UiThread.Init();                      // must run ON SolidWorks' UI thread: ConnectToSW does
                BridgeLog.Info("ConnectToSW on thread " + System.Threading.Thread.CurrentThread.ManagedThreadId);
                _events = new BridgeEvents(_sw); _events.Attach();
                _pipe = new PipeServer(_sw, _events); _pipe.Start();
                return true;
            }
            catch (Exception e) { BridgeLog.Error("ConnectToSW failed", e); return false; }
        }

        public bool DisconnectFromSW()
        {
            try { _pipe?.Stop(); _events?.Detach(); } catch (Exception e) { BridgeLog.Error("DisconnectFromSW", e); }
            _pipe = null; _events = null; _sw = null; BridgeLog.Info("DisconnectFromSW");
            return true;
        }
    }
}
