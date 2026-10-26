# Talk to the SolidPilot Bridge add-in over its named pipe.   Usage: powershell -NoProfile -File bridge-ping.ps1 [-Command "ping"]
# Commands: ping | activeDoc | events [skip] | log | msgbox <text>
param([string]$Command = 'ping', [int]$TimeoutMs = 5000)
$c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut)
try { $c.Connect($TimeoutMs) } catch { throw "Bridge pipe not available: is the add-in loaded in SolidWorks? ($($_.Exception.Message))" }
$w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c)
$w.WriteLine($Command); $r.ReadLine(); $c.Dispose()
