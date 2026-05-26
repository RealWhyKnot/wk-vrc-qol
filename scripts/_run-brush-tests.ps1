[CmdletBinding()]
param(
    [string]$Project = 'D:\WhyKnot Stuff\VRChat\Avatars\Hush - Testing'
)

$ErrorActionPreference = 'Continue'

$unity   = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
$results = Join-Path $env:TEMP 'wk-vrc-qol-brush-test-results.xml'
$log     = Join-Path $env:TEMP 'wk-vrc-qol-brush-test-unity.log'

if (Test-Path $results) { Remove-Item $results -Force }
if (Test-Path $log)     { Remove-Item $log -Force }

Write-Host "unity:    $unity"
Write-Host "project:  $Project"
Write-Host "results:  $results"
Write-Host "log:      $log"

# IMPORTANT: -nographics is omitted on purpose. The brush tests dispatch
# real GPU draws (Graphics.DrawMeshNow against a RenderTexture) and need a
# functional graphics device; with -nographics those calls silently no-op
# and the tests trivially "pass" without exercising the shader.
$argList = @(
    '-projectPath', $Project,
    '-batchmode',
    '-runTests',
    '-testPlatform', 'EditMode',
    '-assemblyNames', 'dev.whyknot.wk-vrc-qol.Tests.Editor',
    '-testFilter', 'WhyKnot.AvatarQol.Tests.MaskPainterBrushTests',
    '-testResults', $results,
    '-logFile', $log
)

& $unity @argList
$exit = $LASTEXITCODE
Write-Host "unity exit code: $exit"

$waited = 0
while ($waited -lt 240) {
    if (Test-Path $results) {
        $info = Get-Item $results -ErrorAction SilentlyContinue
        if ($info -and $info.Length -gt 0) { break }
    }
    Start-Sleep -Milliseconds 250
    $waited++
}

if (Test-Path $results) {
    $info = Get-Item $results
    Write-Host ("results file present ({0} bytes)" -f $info.Length)
    $xmlPeek = Get-Content $results -TotalCount 80 -ErrorAction SilentlyContinue
    $summary = $xmlPeek | Select-String -Pattern 'total="\d+" passed="\d+" failed="\d+"' -SimpleMatch:$false | Select-Object -First 1
    if ($summary) { Write-Host ("  " + $summary.Line.Trim()) }
} else {
    Write-Host "NO RESULTS FILE -- check $log for setup errors"
}
