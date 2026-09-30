<#
.SYNOPSIS
  Explicit reference lane: compare pinned qpdf oracle against the independent reader.
  Fails explicitly when QPDF_EXE is missing; never silently skips.
#>
param(
  [Parameter(Mandatory = $false)]
  [string] $QpdfExe = $env:QPDF_EXE,
  [Parameter(Mandatory = $false)]
  [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($QpdfExe)) {
  throw "QPDF_EXE is not set. Supply -QpdfExe <path> or set QPDF_EXE to the pinned qpdf 12.4.2 executable. See tools/bootstrap-qpdf.ps1."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$probeProject = Join-Path $repoRoot 'tools/QpdfProbe/QpdfProbe.csproj'
$fixtureDir = Join-Path $repoRoot 'tests/Fixtures'

Write-Output "Building QpdfProbe ($Configuration)..."
dotnet build $probeProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
  throw "QpdfProbe build failed."
}

$probeDllCandidates = @(
  (Join-Path $repoRoot "tools/QpdfProbe/bin/$Configuration/net10.0/QpdfProbe.dll")
)
$probeDll = $probeDllCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $probeDll) {
  throw "QpdfProbe.dll not found after build."
}

function Invoke-Probe {
  param([string[]] $ProbeArgs)
  & dotnet $probeDll @ProbeArgs
  return $LASTEXITCODE
}

Write-Output "--- bootstrap oracle ---"
& (Join-Path $repoRoot 'tools/bootstrap-qpdf.ps1') -QpdfExe $QpdfExe

Write-Output "--- positive: minimal-1page (expected 1) ---"
$code = Invoke-Probe @('compare', '--qpdf-exe', $QpdfExe, '--input', (Join-Path $fixtureDir 'minimal-1page.pdf'), '--expected-npages', '1')
if ($code -ne 0) {
  throw "Positive compare failed for minimal-1page.pdf (exit $code)."
}

Write-Output "--- positive: minimal-2page (expected 2) ---"
$code = Invoke-Probe @('compare', '--qpdf-exe', $QpdfExe, '--input', (Join-Path $fixtureDir 'minimal-2page.pdf'), '--expected-npages', '2')
if ($code -ne 0) {
  throw "Positive compare failed for minimal-2page.pdf (exit $code)."
}

Write-Output "--- negative: deliberate mismatch (minimal-1page expected 2 must exit 2) ---"
$code = Invoke-Probe @('compare', '--qpdf-exe', $QpdfExe, '--input', (Join-Path $fixtureDir 'minimal-1page.pdf'), '--expected-npages', '2')
if ($code -ne 2) {
  throw "Deliberate mismatch did not exit 2 (got $code). Negative comparison is broken."
}

Write-Output "--- negative: truncated input must fail validation ---"
$code = Invoke-Probe @('validate', '--input', (Join-Path $fixtureDir 'truncated-1page.pdf'))
if ($code -eq 0) {
  throw "Truncated fixture unexpectedly validated successfully."
}

Write-Output "Reference lane passed: oracle and independent readers agree on valid fixtures and disagree explicitly on negative cases."
