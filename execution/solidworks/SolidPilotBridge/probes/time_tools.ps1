# Wall-clock latency of the REAL execution-layer analysis tools (REST /api/tool/execute, including the pre-flight), read-only modes only, N runs each (min / median / max in ms).
# 'feature_map' is excluded on purpose: it rolls the model back feature by feature, which does not belong on an open working part.
param([int]$N = 3, [string[]]$Modes = @('mass_properties', 'geometry', 'bodies', 'faces', 'edges', 'features'))
$base = 'http://localhost:5000/api/tool'
$sv = (Invoke-RestMethod "$base/state").stateVersion
foreach ($m in $Modes) {
    $ms = @(); $bytes = 0; $status = ''
    for ($i = 0; $i -lt $N; $i++) {
        $body = @{ operationId = ("timing-" + $m + "-" + [guid]::NewGuid().ToString('N')); tool = 'analyze_model'; stateVersion = $sv
                   params = @{ analysis_type = $m; name = ''; from_feature = ''; to_feature = ''; near = ''; k = 0; axis = '' } } | ConvertTo-Json -Depth 6
        $t = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-WebRequest -Uri "$base/execute" -Method Post -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 300
        $t.Stop(); $ms += $t.ElapsedMilliseconds; $bytes = $r.Content.Length
        $status = ($r.Content | ConvertFrom-Json).status
    }
    $s = $ms | Sort-Object
    "{0,-16} {1,-10} min {2,6} ms  median {3,6} ms  max {4,6} ms   response {5,7} bytes" -f $m, $status, $s[0], $s[[int][Math]::Floor($s.Count / 2)], $s[-1], $bytes
}
