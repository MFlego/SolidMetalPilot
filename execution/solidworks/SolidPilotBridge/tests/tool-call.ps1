# One tool call against the in-process REST facade, for ad-hoc work and probes:
#   tool-call.ps1 feature_data '{"action":"list"}'
#   tool-call.ps1 sheet_metal_params '{"action":"set","k_factor":0.42}' -Depth 6
# Reads the current state_version first (mutating tools need it), prints the reply as indented JSON (use -Compact for one line). Exit code 1 when the reply is not COMPLETED.
param([Parameter(Mandatory)][string]$Tool, [string]$Params = '{}', [string]$BaseUrl = 'http://localhost:5000', [int]$Depth = 12, [switch]$Compact, [int]$Retries = 6)
$ErrorActionPreference = 'Stop'
$p = $Params | ConvertFrom-Json
for ($i = 0; $i -le $Retries; $i++) {
    $sv = [int](Invoke-RestMethod -Uri "$BaseUrl/api/tool/state" -TimeoutSec 30).stateVersion
    $body = @{ operationId = [guid]::NewGuid().ToString('N'); tool = $Tool; stateVersion = $sv; params = $p } | ConvertTo-Json -Depth 40 -Compress
    try { $r = Invoke-RestMethod -Uri "$BaseUrl/api/tool/execute" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 180 }
    catch { $r = $null; if ($_.ErrorDetails.Message) { try { $r = $_.ErrorDetails.Message | ConvertFrom-Json } catch { } }; if (-not $r) { throw } }
    if ($r.status -eq 'FAILED' -and $r.error.code -eq 'SW_BUSY' -and $i -lt $Retries) { Start-Sleep 2; continue }      # the gate says SolidWorks is still busy (UI thread not drained yet): ask again
    break
}
if ($Compact) { $r | ConvertTo-Json -Depth $Depth -Compress } else { $r | ConvertTo-Json -Depth $Depth }
if ($r.status -ne 'COMPLETED') { exit 1 }
