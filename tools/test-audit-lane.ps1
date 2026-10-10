param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('SelectionContract', 'SelectionOwnership', 'ResultsContract', 'ResultsOwnership',
        'DiagnosticsContract', 'DiagnosticsOwnership', 'DiagnosticStress',
        'CarriersContract', 'CarriersOwnership', 'WriterOwnership', 'LifetimeStress')]
    [string] $Lane,
    [switch] $List
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Lokad.Qpdf.Tests/Lokad.Qpdf.Tests.csproj'
$results = Join-Path $repoRoot ('artifacts/test-lanes/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

switch ($Lane) {
    'SelectionContract' {
        $filter = 'FullyQualifiedName~SelectionJobCleanupTests&Layer=Contract'
        $expected = 1530
    }
    'SelectionOwnership' {
        $filter = 'FullyQualifiedName~SelectionJobCleanupTests&Layer=Ownership'
        $expected = 74
    }
    'DiagnosticStress' {
        $filter = 'FullyQualifiedName~OwnershipStressTests&Layer=Stress'
        $expected = 1
    }
    'LifetimeStress' {
        $filter = 'FullyQualifiedName~OwnershipStressTests&Layer=Stress'
        $expected = 1
    }
    'CarriersContract' {
        $filter = '(FullyQualifiedName~PdfVersionOwnershipTests|FullyQualifiedName~JsonOutputOwnershipTests|FullyQualifiedName~ExecutionResultFailureOwnershipTests|FullyQualifiedName~SelectionBackingOwnershipTests|FullyQualifiedName~TypedWarningOwnershipTests|FullyQualifiedName~CheckCollectionOwnershipTests|FullyQualifiedName~DocumentWarningSnapshotTests)&Layer=Contract'
        $expected = 1660
    }
    'CarriersOwnership' {
        $filter = '(FullyQualifiedName~PdfVersionOwnershipTests|FullyQualifiedName~JsonOutputOwnershipTests|FullyQualifiedName~ExecutionResultFailureOwnershipTests|FullyQualifiedName~SelectionBackingOwnershipTests|FullyQualifiedName~TypedWarningOwnershipTests|FullyQualifiedName~CheckCollectionOwnershipTests|FullyQualifiedName~DocumentWarningSnapshotTests)&Layer=Ownership'
        $expected = 32
    }
    'WriterOwnership' {
        $filter = 'FullyQualifiedName~Lokad.Qpdf.Tests.Writing.Completed&Layer=Ownership'
        $expected = 10
    }
    'ResultsContract' {
        $filter = '(FullyQualifiedName~ExecutionResultOwnershipTests|FullyQualifiedName~TypedCounterOwnershipTests|FullyQualifiedName~TypedResultOwnershipTests)&Layer=Contract'
        $expected = 1587
    }
    'ResultsOwnership' {
        $filter = '(FullyQualifiedName~ExecutionResultOwnershipTests|FullyQualifiedName~TypedCounterOwnershipTests|FullyQualifiedName~TypedResultOwnershipTests)&Layer=Ownership'
        $expected = 131
    }
    'DiagnosticsContract' {
        $filter = 'FullyQualifiedName~DiagnosticFailureExportTests&Layer=Contract'
        $expected = 450
    }
    'DiagnosticsOwnership' {
        $filter = 'FullyQualifiedName~DiagnosticFailureExportTests&Layer=Ownership'
        $expected = 4
    }
}

$arguments = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--filter', $filter)
if ($List) {
    $discovery = & dotnet @arguments --list-tests 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Lane discovery failed: exit $LASTEXITCODE" }
    $tests = @($discovery | Where-Object { "$($_)".TrimStart().StartsWith('Lokad.Qpdf.Tests.', [StringComparison]::Ordinal) })
    if ($tests.Count -ne $expected) { throw "$Lane must discover exactly $expected cases; found $($tests.Count)." }
    $tests | ForEach-Object { Write-Host $_ }
    Write-Host "$Lane lane discovered: $expected cases."
    return
}

dotnet @arguments `
    --logger 'trx;LogFileName=lane.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw "$Lane lane failed: exit $LASTEXITCODE" }

[xml] $trx = Get-Content -LiteralPath (Join-Path $results 'lane.trx') -Raw
$counters = $trx.GetElementsByTagName('Counters')
if ($counters.Count -ne 1 -or [int] $counters[0].GetAttribute('total') -ne $expected `
    -or [int] $counters[0].GetAttribute('passed') -ne $expected) {
    throw "$Lane must pass exactly $expected cases; empty or partial discovery cannot pass."
}
Write-Host "$Lane lane passed: $expected cases. Evidence: $results"
