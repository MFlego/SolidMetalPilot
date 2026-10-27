# Maps the SolidWorks API surface that matters for sheet metal straight from the interop assemblies (no SolidWorks needed):
#   1. swFeatureNameID_e members (what CreateDefinition(...) can be asked for)
#   2. methods of IFeatureManager / IModelDoc2 / IPartDoc / IModelDocExtension / ISldWorks that mention sheet-metal terms
#   3. every *FeatureData interface for those features, with all members and signatures
#   4. the sheet-metal enums
# Usage: powershell -NoProfile -File sheetmetal_api_map.ps1 [-Out map.txt]
param([string]$Out = "")
. "$PSScriptRoot\_common.ps1"
$redist = Import-SwInterops
$swd = [Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
$swc = [Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll")
$words = 'SheetMetal|Flange|Bend|Hem\b|Hem[A-Z]|Jog|Rip|Corner|Miter|Mitre|Fold|Unfold|Flat|Convert|FormTool|Forming|Gusset|Tab|Slot|MoveFace|Louver|Lance|Beads|Dome|Weld|Gauge|KFactor|Relief|Crossbreak|CrossBreak|Cross'
$sb = New-Object Text.StringBuilder
function Out-Line($s) { [void]$sb.AppendLine($s) }

Out-Line "SOLIDWORKS sheet-metal API map  (generated from $($swd.GetName().Name) $($swd.GetName().Version))"
Out-Line ""
Out-Line "=== 1. swFeatureNameID_e (CreateDefinition / feature type ids) matching sheet-metal terms"
$e = $swc.GetType('SolidWorks.Interop.swconst.swFeatureNameID_e')
foreach ($n in [Enum]::GetNames($e) | Where-Object { $_ -match $words } | Sort-Object) { Out-Line ("   {0,-44} = {1}" -f $n, [int][Enum]::Parse($e, $n)) }

Out-Line ""
Out-Line "=== 2. methods that mention sheet-metal terms"
foreach ($tn in 'IFeatureManager', 'IModelDoc2', 'IPartDoc', 'IModelDocExtension', 'ISldWorks', 'IFeature', 'ISelectionMgr') {
    $t = $swd.GetType("SolidWorks.Interop.sldworks.$tn"); if (-not $t) { continue }
    Out-Line "   --- $tn"
    foreach ($m in $t.GetMethods() | Where-Object { $_.Name -match $words -and -not $_.IsSpecialName } | Sort-Object Name) {
        $ps = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
        Out-Line ("   {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
}

Out-Line ""
Out-Line "=== 3. feature-data interfaces for those features (all members)"
$ifaces = $swd.GetTypes() | Where-Object { $_.IsInterface -and $_.Name -match '^I.*(FeatureData|Data\d*)$' -and $_.Name -match $words -and $_.Name -notmatch 'Event|Handler|Notify' } | Sort-Object Name
foreach ($i in $ifaces) {
    Out-Line ""
    Out-Line "   $($i.Name)"
    $members = $i.GetProperties() | Sort-Object Name | ForEach-Object { $acc = if ($_.CanWrite) { 'get/set' } else { 'get' }; "      prop {0,-9}{1} {2}" -f $acc, $_.PropertyType.Name, $_.Name }
    $methods = $i.GetMethods() | Where-Object { -not $_.IsSpecialName } | Sort-Object Name | ForEach-Object { $ps = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '; "      call {0} {1}({2})" -f $_.ReturnType.Name, $_.Name, $ps }
    $members; $methods | ForEach-Object { Out-Line $_ }
    $members | ForEach-Object { Out-Line $_ }
}

Out-Line ""
Out-Line "=== 4. enums (swconst) with sheet-metal terms"
foreach ($t in $swc.GetTypes() | Where-Object { $_.IsEnum -and $_.Name -match $words -and $_.Name -notmatch 'swFeatureNameID_e' } | Sort-Object Name) {
    Out-Line ("   {0}: {1}" -f $t.Name, (([Enum]::GetNames($t) | ForEach-Object { "$_=$([int][Enum]::Parse($t, $_))" }) -join ', '))
}
if ($Out) { $sb.ToString() | Set-Content -Path $Out -Encoding UTF8; "written: $Out  ($($sb.Length) chars)" } else { $sb.ToString() }
