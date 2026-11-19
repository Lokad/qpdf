<#
.SYNOPSIS
  Inspect the supplied qpdf oracle executable for development validation.
  Fails explicitly when the executable is missing or the version does not match.
  Ordinary builds and tests do not require this executable or external/.
#>
param(
  [Parameter(Mandatory = $false)]
  [string] $QpdfExe = $env:QPDF_EXE
)

$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reference-pin.json') -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($QpdfExe)) {
  throw "QPDF_EXE is not set. Supply -QpdfExe <path> or set QPDF_EXE. Use the pinned qpdf $($pin.version) release or build commit $($pin.commit). See docs/COMPATIBILITY.md."
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

$versionLine = ($versionOutput -split '[\r\n]', 2)[0].Trim()
$expectedBanner = "qpdf version $($pin.version)"
if ($versionLine -cne $expectedBanner) {
  throw "qpdf version mismatch. Expected '$expectedBanner', got: $($versionOutput.Trim())."
}

$sha = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -eq $pin.windowsRelease.executableSha256) {
  $identity = 'Windows release executable hash matched'
} else {
  $identity = 'version matched, executable hash recorded'
}
Write-Output "qpdf oracle: $resolved"
Write-Output "qpdf version output: $($versionOutput.Trim())"
Write-Output "qpdf exe sha256: $sha"
Write-Output "qpdf identity: $identity"
Write-Output "qpdf target source commit: $($pin.commit)"
