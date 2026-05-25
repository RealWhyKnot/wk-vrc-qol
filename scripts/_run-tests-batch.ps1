[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$unity   = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
$project = 'D:\WhyKnot Stuff\VRChat\Avatars\Ume'
$results = Join-Path $env:TEMP 'wk-vrc-qol-test-results.xml'
$log     = Join-Path $env:TEMP 'wk-vrc-qol-test-unity.log'

if (Test-Path $results) { Remove-Item $results -Force }
if (Test-Path $log)     { Remove-Item $log -Force }

Write-Host "unity:    $unity"
Write-Host "project:  $project"
Write-Host "results:  $results"
Write-Host "log:      $log"

$argList = @(
    '-projectPath', $project,
    '-batchmode',
    '-nographics',
    '-runTests',
    '-testPlatform', 'EditMode',
    '-assemblyNames', 'dev.whyknot.wk-vrc-qol.Tests.Editor;dev.whyknot.core.Tests.Editor',
    '-testResults', $results,
    '-logFile', $log
    # -quit is intentionally omitted: -runTests already exits the Editor
    # automatically after the test results are written. Adding -quit races
    # the test runner's deferred startup and the Editor sometimes shuts
    # down before any test executes, leaving an empty (or missing) NUnit
    # results XML.
)

# Splat through the call operator: Start-Process -ArgumentList space-joins
# the array, which mangles values that themselves contain spaces (e.g. a
# project path under "D:\WhyKnot Stuff\..."). The call operator preserves
# array-element-to-argument boundaries.
& $unity @argList
$exit = $LASTEXITCODE
Write-Host "unity exit code: $exit"

# Unity 2022.3 batch mode sometimes returns before the OS finishes
# flushing the NUnit results XML to disk -- a Test-Path check in the
# next line then reports "no file" even though the file appears moments
# later. Poll briefly so the script reports the real outcome.
$waited = 0
while (-not (Test-Path $results) -and $waited -lt 30) {
    Start-Sleep -Milliseconds 250
    $waited++
}

if (Test-Path $results) {
    $info = Get-Item $results
    Write-Host ("results file present ({0} bytes)" -f $info.Length)
    # Try to summarise pass/fail without depending on external XML tools.
    $xmlPeek = Get-Content $results -TotalCount 50 -ErrorAction SilentlyContinue
    $summary = $xmlPeek | Select-String -Pattern 'total="\d+" passed="\d+" failed="\d+"' -SimpleMatch:$false | Select-Object -First 1
    if ($summary) { Write-Host ("  " + $summary.Line.Trim()) }
} else {
    Write-Host "NO RESULTS FILE -- check $log for setup errors"
}
