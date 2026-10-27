# Run a typed probe INSIDE SolidWorks through the bridge (run_cs) and print what it returns.
#   run-probe.ps1 probe40_sm_create.cs [argument]        (argument goes to Script.Run(sw, arg); it may contain spaces)
# The bridge pipe flattens newlines, so probes separate their output items with " ;; " - shown here one per line. A probe that throws prints the exception; a C# compile error prints the compiler messages.
param([Parameter(Mandatory)][string]$Probe, [string]$Arg = '', [int]$TimeoutMs = 120000)
$ErrorActionPreference = 'Stop'
$file = if (Test-Path $Probe) { (Resolve-Path $Probe).Path } else { (Resolve-Path (Join-Path $PSScriptRoot $Probe)).Path }
$c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut)
try { $c.Connect(5000) } catch { throw "Bridge pipe not available: is the add-in loaded in SolidWorks? ($($_.Exception.Message))" }
$w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c)
$w.WriteLine("run_cs $file" + $(if ($Arg) { "|$Arg" } else { '' }))
$t = $r.ReadLineAsync(); if (-not $t.Wait($TimeoutMs)) { $c.Dispose(); throw "no answer within $TimeoutMs ms" }
$line = $t.Result; $c.Dispose()
($line -replace ' ;; ', "`n")
