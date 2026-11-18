<#
.SYNOPSIS
  Explicit reference lane: compare pinned qpdf oracle against the independent reader.
  Fails explicitly when QPDF_EXE is missing; never silently skips.
#>
param(
  [Parameter(Mandatory = $false)]
  [string] $QpdfExe = $env:QPDF_EXE,
  [Parameter(Mandatory = $false)]
  [string] $Configuration = 'Release',
  [Parameter(Mandatory = $false)]
  [string] $WorkRoot,
  [string] $ProbeDll
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($QpdfExe)) {
  throw "QPDF_EXE is not set. Supply -QpdfExe <path> or set QPDF_EXE to the pinned qpdf 12.4.2 executable. See tools/bootstrap-qpdf.ps1."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixtureDir = Join-Path $repoRoot 'tests/Fixtures'
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')

$root = if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
  Join-Path $repoRoot ('artifacts/reference-checks/' + [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkRoot, (Get-Location).ProviderPath) }
if (Test-Path -LiteralPath $root) { throw "Reference workspace already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
Write-Output "Reference artifacts: $root"
$results = [ordered]@{
  Status = 'running'; Oracle = $QpdfExe; Probe = $null; Stages = [ordered]@{}
}

function Save-ReferenceResults {
  $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
}

function Invoke-ReferenceStage([string] $Name, [scriptblock] $Action) {
  $stage = [ordered]@{ Status = 'running'; Log = "$Name.log" }
  $results.Stages[$Name] = $stage
  Save-ReferenceResults
  $log = Join-Path $root $stage.Log
  Write-Output "--- $Name ---"
  try {
    & $Action *>&1 | Tee-Object -FilePath $log
    $stage.Status = 'passed'
  } catch {
    $stage.Status = 'failed'
    $stage.Error = $_.Exception.Message
    $_ | Out-String | Add-Content -LiteralPath $log -Encoding utf8NoBOM
    throw
  } finally { Save-ReferenceResults }
}

try {
  Invoke-ReferenceStage 'bootstrap' {
    $results.Oracle = (Resolve-Path -LiteralPath $QpdfExe).Path
    & (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $results.Oracle
  }
  $buildProbe = [string]::IsNullOrWhiteSpace($ProbeDll)
  if ($buildProbe) { $ProbeDll = Join-Path $repoRoot "tools/QpdfProbe/bin/$Configuration/net11.0/QpdfProbe.dll" }
  Invoke-ReferenceStage 'probe' {
    if ($buildProbe) {
      Write-Output "Building QpdfProbe ($Configuration)..."
      dotnet build (Join-Path $PSScriptRoot 'QpdfProbe/QpdfProbe.csproj') -c $Configuration `
        -f net11.0 --nologo -v q -p:RestoreLockedMode=true
      if ($LASTEXITCODE -ne 0) { throw "QpdfProbe build failed (exit $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath $ProbeDll -PathType Leaf)) { throw "QpdfProbe.dll not found: $ProbeDll" }
    $results.Probe = (Resolve-Path -LiteralPath $ProbeDll).Path
    Write-Output "Reference probe: $($results.Probe)"
  }
  Invoke-ReferenceStage 'reader-checks' {
    $directory = Join-Path $root 'reader-checks'
    New-Item -ItemType Directory -Path $directory | Out-Null
    Initialize-ReferenceProbe -ProbeDll $results.Probe -WorkRoot $directory
    try {
      foreach ($count in 1, 2) {
        $fixture = "minimal-${count}page.pdf"
        $path = Join-Path $fixtureDir $fixture
        $probe = Invoke-ReferenceProbeRequest -WorkingDirectory $directory -Arguments @(
          'compare', '--qpdf-exe', $results.Oracle, '--input', $path, '--expected-npages', "$count")
        Assert-ReferenceProbeOutcome -Result $probe -Case $fixture -ExpectedExit 0 -ExpectedOutput @(
          "compare: expected-npages=$count", "compare: oracle-npages=$count", "compare: independent-npages=$count", 'compare: match') -ExpectedError ''
        Write-Output "Reader agreement passed for $fixture ($count pages)."
      }
      $probe = Invoke-ReferenceProbeRequest -WorkingDirectory $directory -Arguments @(
        'compare', '--qpdf-exe', $results.Oracle, '--input', (Join-Path $fixtureDir 'minimal-1page.pdf'), '--expected-npages', '2')
      Assert-ReferenceProbeOutcome -Result $probe -Case 'Deliberate page-count mismatch' -ExpectedExit 2 -ExpectedOutput @(
        'compare: expected-npages=2', 'compare: oracle-npages=1', 'compare: independent-npages=1') `
        -ExpectedError 'qpdfprobe: page-count mismatch (expected=2, oracle=1, independent=1).'
      Write-Output 'Deliberate page-count mismatch returned the expected counts and diagnostic.'

      $path = Join-Path $fixtureDir 'truncated-1page.pdf'
      $probe = Invoke-ReferenceProbeRequest -WorkingDirectory $directory -Arguments @('validate', '--input', $path)
      Assert-ReferenceProbeOutcome -Result $probe -Case 'Truncated PDF' -ExpectedExit 1 -ExpectedError (Get-TruncatedProbeDiagnostic $path)
      Write-Output 'Truncated PDF returned the expected independent-reader diagnostic.'
    } finally { Complete-ReferenceProbe }
  }
  Invoke-ReferenceStage 'content-controls' {
    & (Join-Path $PSScriptRoot 'test-reference-content.ps1') -QpdfExe $results.Oracle -ProbeDll $results.Probe -WorkRoot (Join-Path $root 'content-controls')
  }
  Invoke-ReferenceStage 'differential' {
    & (Join-Path $PSScriptRoot 'run-differential-checks.ps1') -QpdfExe $results.Oracle -ProbeDll $results.Probe -WorkRoot (Join-Path $root 'differential')
  }
  Invoke-ReferenceStage 'parser' {
    & (Join-Path $PSScriptRoot 'run-parser-checks.ps1') -QpdfExe $results.Oracle -ProbeDll $results.Probe -WorkRoot (Join-Path $root 'parser')
  }
  $results.Status = 'passed'
} catch {
  $results.Status = 'failed'
  throw
} finally { Save-ReferenceResults }
