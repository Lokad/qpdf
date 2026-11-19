<#
.SYNOPSIS
  Acquire the pinned Windows qpdf development oracle in a fresh directory.
  No files are installed into the runtime/package or overwritten.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reference-pin.json') -Raw | ConvertFrom-Json
$release = $pin.windowsRelease
$root = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $root) { throw "Oracle destination already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
$archive = Join-Path $root $release.archive
# Official release asset and reviewed digest; never resolve a latest version.
$url = "$($pin.repository)/releases/download/v$($pin.version)/$($release.archive)"
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $release.archiveSha256) {
    throw 'Pinned qpdf archive checksum mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'unpacked')
$executable = Join-Path (Join-Path $root 'unpacked') $release.executable
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $release.executableSha256) {
    throw 'Pinned qpdf executable checksum mismatch.'
}
Write-Output $executable
