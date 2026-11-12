<#
.SYNOPSIS
  Record native AES-encrypted linearized two-page documents for offline tests.
  Credentials are public fixture data; regeneration changes random ciphertext.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe)
$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($revision in @(4, 6)) {
    $bits = if ($revision -eq 4) { 128 } else { 256 }
    $name = "security-r$revision-linearized.pdf"
    $output = Join-Path $fixtures $name
    $arguments = @('--encrypt', 'reader', 'fixture-owner', "$bits", '--modify=none', '--print=low', '--extract=n')
    if ($revision -eq 4) { $arguments += '--use-aes=y' }
    $arguments += @('--', '--linearize', '--object-streams=generate', (Join-Path $fixtures 'minimal-2page.pdf'), $output)
    & $QpdfExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not generate $name" }
    foreach ($password in @('reader', 'fixture-owner')) {
        & $QpdfExe "--password=$password" --check-linearization $output | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Native linearization check failed for $name" }
        & $QpdfExe "--password=$password" --check $output | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Native structural check failed for $name" }
    }
    $manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -cne $name }) + @([ordered]@{
        file=$name; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=2
        headerVersion=$(if ($revision -eq 4) { '1.6' } else { '1.7' }); valid=$true
        description="Native qpdf 12.4.2 AES-$bits R$revision linearization of authored minimal-2page.pdf with generated object streams. Public user/owner credentials are reader/fixture-owner. Generated and checked by tools/build-security-linearized-fixtures.ps1; randomized ciphertext is retained for offline tests."
    })
}
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
