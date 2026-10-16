<#
.SYNOPSIS
  Negative integration cases for fresh-artifact consumer verification.
.DESCRIPTION
  Requires a packed unsigned Lokad.qpdf artifact, PowerShell and the pinned SDK.
  Changes only copied consumer locks under artifacts, never the tracked lock.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$testRoot = Join-Path $repoRoot ('artifacts/consumer-tests/' + [guid]::NewGuid().ToString('N'))
$consumerDirectory = Join-Path $testRoot 'tests/Lokad.Qpdf.PackageConsumer'
$toolDirectory = Join-Path $testRoot 'tools'
New-Item -ItemType Directory -Path $consumerDirectory, $toolDirectory | Out-Null
foreach ($name in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $testRoot
}
foreach ($name in @('Lokad.Qpdf.PackageConsumer.csproj', 'Program.cs', 'RecordingMemoryBudget.cs')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('tests/Lokad.Qpdf.PackageConsumer/' + $name)) -Destination $consumerDirectory
}
$seedPath = Join-Path $repoRoot 'tests/Lokad.Qpdf.PackageConsumer/packages.lock.json'
$seedText = Get-Content -LiteralPath $seedPath -Raw
$seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
$testLockPath = Join-Path $consumerDirectory 'packages.lock.json'
$verifier = Join-Path $toolDirectory 'verify-package-consumer.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-package-consumer.ps1') -Destination $verifier

function Assert-Rejection {
    param([string] $Name, [string] $ExpectedPattern)
    $output = & pwsh -NoProfile -File $verifier -PackagePath $resolvedPackage 2>&1 | Out-String
    $code = $LASTEXITCODE
    [System.IO.File]::WriteAllText((Join-Path $testRoot ($Name + '.log')), $output)
    if ($code -eq 0 -or $output -notmatch $ExpectedPattern -or
        $output.Contains('CONSUMER SMOKE PASSED')) {
        throw "$Name did not reject as expected (exit $code). See its saved log."
    }
    Write-Output "PASS $Name (rejected before consumer execution)"
}

# A stale version must not be made acceptable by changing the local hash.
$lock = $seedText | ConvertFrom-Json -AsHashtable
$lock.dependencies['net10.0']['Lokad.qpdf'].resolved = '0.0.0-invalid'
[System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
Assert-Rejection 'wrong-artifact-version' 'Artifact identity/version must match'

# A changed dependency graph must not silently introduce a codec package.
$lock = $seedText | ConvertFrom-Json -AsHashtable
$lock.dependencies['net10.0']['Unexpected.Codec'] = @{ type = 'Transitive'; resolved = '1.0.0'; contentHash = 'invalid' }
[System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
Assert-Rejection 'unexpected-consumer-dependency' 'Consumer must depend only on'

if ((Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash -cne $seedHash) {
    throw 'Negative verification changed the tracked consumer lock.'
}
Write-Output "Consumer verification negative cases passed; evidence: $testRoot"
# Expected child failures leave LASTEXITCODE nonzero in a calling pwsh step.
exit 0
