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
    '-logFile', $log,
    '-quit'
)

# Splat through the call operator: Start-Process -ArgumentList space-joins
# the array, which mangles values that themselves contain spaces (e.g. a
# project path under "D:\WhyKnot Stuff\..."). The call operator preserves
# array-element-to-argument boundaries.
& $unity @argList
$exit = $LASTEXITCODE
Write-Host "unity exit code: $exit"

if (Test-Path $results) {
    Write-Host "results file present"
} else {
    Write-Host "NO RESULTS FILE -- check $log for setup errors"
}
