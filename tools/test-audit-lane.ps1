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
    }
    'SelectionOwnership' {
        $filter = 'FullyQualifiedName~SelectionJobCleanupTests&Layer=Ownership'
    }
    'DiagnosticStress' {
        $filter = 'FullyQualifiedName~PracticalMemoryTests&Layer=Stress'
    }
    'LifetimeStress' {
        $filter = 'FullyQualifiedName~PracticalMemoryTests&Layer=Stress'
    }
    'CarriersContract' {
        $filter = '(FullyQualifiedName~PdfVersionOwnershipTests|FullyQualifiedName~JsonOutputOwnershipTests|FullyQualifiedName~ExecutionResultFailureOwnershipTests|FullyQualifiedName~SelectionBackingOwnershipTests|FullyQualifiedName~TypedWarningOwnershipTests|FullyQualifiedName~CheckCollectionOwnershipTests|FullyQualifiedName~DocumentWarningSnapshotTests)&Layer=Contract'
    }
    'CarriersOwnership' {
        $filter = '(FullyQualifiedName~PdfVersionOwnershipTests|FullyQualifiedName~JsonOutputOwnershipTests|FullyQualifiedName~ExecutionResultFailureOwnershipTests|FullyQualifiedName~SelectionBackingOwnershipTests|FullyQualifiedName~TypedWarningOwnershipTests|FullyQualifiedName~CheckCollectionOwnershipTests|FullyQualifiedName~DocumentWarningSnapshotTests)&Layer=Ownership'
    }
    'WriterOwnership' {
        $filter = 'FullyQualifiedName~Lokad.Qpdf.Tests.Writing.Completed&Layer=Ownership'
    }
    'ResultsContract' {
        $filter = '(FullyQualifiedName~ExecutionResultOwnershipTests|FullyQualifiedName~TypedCounterOwnershipTests|FullyQualifiedName~TypedResultOwnershipTests)&Layer=Contract'
    }
    'ResultsOwnership' {
        # Compatibility alias: the superseded receipt/GC matrices are removed.
        $filter = '(FullyQualifiedName~ExecutionResultOwnershipTests|FullyQualifiedName~TypedCounterOwnershipTests|FullyQualifiedName~TypedResultOwnershipTests)&Layer=Contract'
    }
    'DiagnosticsContract' {
        $filter = 'FullyQualifiedName~DiagnosticFailureExportTests&Layer=Contract'
    }
    'DiagnosticsOwnership' {
        $filter = 'FullyQualifiedName~DiagnosticFailureExportTests&Layer=Ownership'
    }
}

$arguments = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--filter', $filter)
if ($List) {
    $discovery = & dotnet @arguments --list-tests 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Lane discovery failed: exit $LASTEXITCODE" }
    $tests = @($discovery | Where-Object { "$($_)".TrimStart().StartsWith('Lokad.Qpdf.Tests.', [StringComparison]::Ordinal) })
    if ($tests.Count -eq 0) { throw "$Lane selected no tests." }
    $tests | ForEach-Object { Write-Host $_ }
    Write-Host "$Lane lane discovered: $($tests.Count) cases."
    return
}

dotnet @arguments `
    --logger 'trx;LogFileName=lane.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw "$Lane lane failed: exit $LASTEXITCODE" }

[xml] $trx = Get-Content -LiteralPath (Join-Path $results 'lane.trx') -Raw
$counters = $trx.GetElementsByTagName('Counters')
if ($counters.Count -ne 1 -or [int] $counters[0].GetAttribute('total') -eq 0 `
    -or [int] $counters[0].GetAttribute('passed') -ne [int] $counters[0].GetAttribute('total')) {
    throw "$Lane must execute a nonempty selection without failures or skipped tests."
}
Write-Host "$Lane lane passed: $($counters[0].GetAttribute('passed')) cases. Evidence: $results"
