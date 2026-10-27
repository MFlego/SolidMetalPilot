# Background watcher: logs every visible top-level #32770 / HwndWrapper window of SolidWorks (size, position, enabled, owner, style, child controls with captions) whenever the set changes.
# Purpose: catch the transient "skeleton" Task Dialog (text "Title / Description / Button1 / Button2 / Hyperlink Text 1 / 2", no buttons) that made the pre-flight answer DIALOG_BLOCKING,
# and tell it apart from a real modal dialog.   Usage: powershell -NoProfile -File dialog-watch.ps1 -Out dialog_watch.log [-Seconds 300] [-IntervalMs 150]
param([Parameter(Mandatory)][string]$Out, [int]$Seconds = 300, [int]$IntervalMs = 150)
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Text; using System.Runtime.InteropServices;
public static class DlgWatch {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  static string Txt(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
  public static string Snapshot(uint pid) {
    var sb = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var c = Cls(h); if (c != "#32770" && !c.StartsWith("HwndWrapper")) return true;
      RECT r; GetWindowRect(h, out r);
      var kids = new List<string>(); EnumChildWindows(h, (k, l2) => { var kc = Cls(k); var kt = Txt(k); kids.Add(kc + (kt.Length > 0 ? "='" + kt + "'" : "")); return kids.Count < 14; }, IntPtr.Zero);
      sb.Add(string.Format("{0} hwnd={1} title='{2}' size={3}x{4} at({5},{6}) enabled={7} owner={8} style=0x{9:X} children=[{10}]", c, h.ToInt64(), Txt(h), r.R - r.L, r.B - r.T, r.L, r.T, IsWindowEnabled(h), GetWindow(h, 4).ToInt64(), GetWindowLong(h, -16), string.Join(", ", kids.ToArray())));
      return true; }, IntPtr.Zero);
    sb.Sort(); return string.Join("\n", sb.ToArray());
  }
}
"@
$pid1 = (Get-Process SLDWORKS | Select-Object -First 1).Id
$end = (Get-Date).AddSeconds($Seconds); $last = $null; "watching SolidWorks pid $pid1 for $Seconds s" | Set-Content $Out
while ((Get-Date) -lt $end) {
    $s = [DlgWatch]::Snapshot([uint32]$pid1)
    if ($s -ne $last) { Add-Content $Out ("{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $(if ($s) { "`n" + $s } else { "(none)" })); $last = $s }
    Start-Sleep -Milliseconds $IntervalMs
}
