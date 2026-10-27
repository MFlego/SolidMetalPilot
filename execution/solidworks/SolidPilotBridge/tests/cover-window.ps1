# Puts a small TOP-MOST window (no activation, no taskbar button, no focus) over a screen rectangle and counts the mouse clicks it receives. convert-tests.ps1 -Cover uses it to prove that the tools never
# click a window of another application that sits over SolidWorks' graphics area.
#   cover-window.ps1 -Left 241 -Top 1180 -Width 1078 -Height 893 -Out C:\temp\cover.txt [-MaxSeconds 120]
# Protocol: the file $Out holds "shown" once the window is up, and "clicks=<n>" after it has closed. The window closes when the file "$Out.stop" appears (or after -MaxSeconds).
param([int]$Left, [int]$Top, [int]$Width, [int]$Height, [Parameter(Mandatory)][string]$Out, [int]$MaxSeconds = 120)
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
public class CoverForm : System.Windows.Forms.Form
{
    public int Clicks;
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override System.Windows.Forms.CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } }      // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    protected override void OnMouseDown(System.Windows.Forms.MouseEventArgs e) { Clicks++; base.OnMouseDown(e); }
}
'@
Remove-Item $Out, "$Out.stop" -ErrorAction SilentlyContinue
$f = New-Object CoverForm
$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.TopMost = $true; $f.ShowInTaskbar = $false; $f.BackColor = [Drawing.Color]::DimGray; $f.Opacity = 0.4
$f.Bounds = New-Object Drawing.Rectangle($Left, $Top, $Width, $Height)
$started = Get-Date
$timer = New-Object Windows.Forms.Timer; $timer.Interval = 300
$timer.Add_Tick({ if ((Test-Path "$Out.stop") -or ((Get-Date) - $started).TotalSeconds -gt $MaxSeconds) { $timer.Stop(); $f.Close() } })
$f.Add_Shown({ Set-Content $Out 'shown'; $timer.Start() })
[Windows.Forms.Application]::Run($f)
Set-Content $Out "clicks=$($f.Clicks)"
