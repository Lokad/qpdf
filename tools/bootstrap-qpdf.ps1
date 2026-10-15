<#
.SYNOPSIS
  Bootstrap the pinned qpdf 12.4.2 oracle executable for development validation.
  Fails explicitly when the executable is missing or the version does not match.
  Ordinary builds and tests do not require this executable or external/.
#>
param(
  [Parameter(Mandatory = $false)]
  [string] $QpdfExe = $env:QPDF_EXE
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($QpdfExe)) {
  throw "QPDF_EXE is not set. Supply -QpdfExe <path> or set QPDF_EXE to the pinned qpdf 12.4.2 executable. Obtain it by building external/qpdf at commit 4eba95899886e851cc41d76886483b347612f2a8 or downloading the exact 12.4.2 release binary (see docs/COMPATIBILITY.md). This is an explicit failure, not a skipped check."
}

$resolved = (Resolve-Path -LiteralPath $QpdfExe -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
  throw "qpdf executable not found: $resolved."
}

$versionOutput = & $resolved --version 2>&1 | Out-String
$versionExit = $LASTEXITCODE
if ($versionExit -ne 0) {
  throw "qpdf --version failed with exit $versionExit : $versionOutput"
}

if ($versionOutput -notmatch '12\.4\.2') {
  throw "qpdf version mismatch. Expected 12.4.2 in `qpdf --version` output, got: $($versionOutput.Trim())."
}

$sha = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "qpdf oracle: $resolved"
Write-Output "qpdf version output: $($versionOutput.Trim())"
Write-Output "qpdf exe sha256: $sha"
Write-Output "qpdf baseline: JPEG support expected; optional Zopfli support absent (see docs/COMPATIBILITY.md)."
Write-Output "reference commit: 4eba95899886e851cc41d76886483b347612f2a8"
