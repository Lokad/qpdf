<#
.SYNOPSIS
  Acquire the exact Windows qpdf 12.4.2 development oracle in a fresh directory.
  No files are installed into the runtime/package or overwritten.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $root) { throw "Oracle destination already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
$archive = Join-Path $root 'qpdf-12.4.2-msvc64.zip'
# Official release asset and reviewed digest; never resolve a latest version.
$url = 'https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2-msvc64.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
$expectedArchive = 'DB87077E683630C1217E0E8F9A20A9749D952AB676E881C3689187763A5DE25D'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedArchive) {
    throw 'Pinned qpdf archive checksum mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'unpacked')
$executable = Join-Path $root 'unpacked/qpdf-12.4.2-msvc64/bin/qpdf.exe'
$expectedExe = '1BDAF878F37EAFFEA1EA3C30591214C77636A0F2FC7432427516B6E22E36C0FF'
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $expectedExe) {
    throw 'Pinned qpdf executable checksum mismatch.'
}
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $executable | Out-Host
Write-Output $executable
