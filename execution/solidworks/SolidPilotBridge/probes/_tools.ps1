# Call SolidPilot tools over the REST facade from PowerShell (dot-source). Base URL: $env:EXECUTION_BASE_URL or http://localhost:5000.
#   Invoke-Tool <tool> [-Params @{...}]  -> the parsed reply (status, cadState, result_geometry, error ...) with .http added; fetches the current state_version itself and
#                                           retries once when the gate reports the UI thread busy.
#   Get-Edges / Get-Faces                -> parsed analyze_model(edges|faces) payloads
$script:ToolBase = if ($env:EXECUTION_BASE_URL) { $env:EXECUTION_BASE_URL } else { 'http://localhost:5000' }
function Invoke-Tool([string]$Tool, $Params = @{}) {
    for ($try = 0; $try -lt 3; $try++) {
        $sv = [int](Invoke-RestMethod "$script:ToolBase/api/tool/state" -TimeoutSec 30).stateVersion
        $body = @{ operationId = [guid]::NewGuid().ToString('N'); tool = $Tool; stateVersion = $sv; params = $Params } | ConvertTo-Json -Depth 40 -Compress
        try { $r = Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) "$script:ToolBase/api/tool/execute" -TimeoutSec 300; $o = $r.Content | ConvertFrom-Json; $o | Add-Member http ([int]$r.StatusCode) -Force }
        catch [System.Net.WebException] { $resp = $_.Exception.Response; $txt = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd(); $o = $txt | ConvertFrom-Json; $o | Add-Member http ([int]$resp.StatusCode) -Force }
        if ($o.status -eq 'FAILED' -and $o.error.code -eq 'SW_BUSY') { Start-Sleep 2; continue }
        return $o
    }
    return $o
}
function Get-Analysis([string]$Type, $Extra = @{}) {
    $p = @{ analysis_type = $Type; name = ''; from_feature = ''; to_feature = ''; near = ''; k = 0; axis = '' }; foreach ($k in $Extra.Keys) { $p[$k] = $Extra[$k] }
    $o = Invoke-Tool 'analyze_model' $p; if ($o.status -ne 'COMPLETED') { throw "analyze_model($Type) failed: $($o.error.code) $($o.error.message)" }
    $f = $o.cadState.features; if ($f -is [array]) { $f = $f[0] }; if ($f -is [string] -and $f.TrimStart().StartsWith('{')) { return $f | ConvertFrom-Json } else { return $o.cadState.features }
}
function Get-Edges { Get-Analysis 'edges' }
function Get-Faces { Get-Analysis 'faces' }
