<#
.SYNOPSIS
  Record native fixtures with public single-byte passwords for offline recovery tests.
  Randomized ciphertext is retained; regenerating requires review of hashes.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe)
$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$cases = @(
  @{ revision = 4; name = 'winansi'; user = 'café€'; owner = 'ownér€'; userHex = '636166E980'; ownerHex = '6F776EE97280' },
  @{ revision = 4; name = 'macroman'; user = 'café•'; owner = 'ownér•'; userHex = '6361668EA5'; ownerHex = '6F776E8E72A5' },
  @{ revision = 6; name = 'pdfdoc'; user = 'café•'; owner = 'ownér•'; userHex = '636166E980'; ownerHex = '6F776EE97280' },
  @{ revision = 6; name = 'winansi'; user = 'café€'; owner = 'ownér€'; userHex = '636166E980'; ownerHex = '6F776EE97280' },
  @{ revision = 6; name = 'macroman'; user = 'café•'; owner = 'ownér•'; userHex = '6361668EA5'; ownerHex = '6F776E8E72A5' },
  @{ revision = 6; name = 'long'; user = ('é' * 127); owner = ('è' * 127); userHex = ('E9' * 127); ownerHex = ('E8' * 127) }
)
foreach ($case in $cases) {
  $case.file = "security-recovery-r$($case.revision)-$($case.name).pdf"
  $output = Join-Path $fixtures $case.file
  $bits = if ($case.revision -eq 4) { '128' } else { '256' }
  [string[]] $encryptionOptions = if ($case.revision -eq 4) { @('--use-aes=y') } else { @() }
  $arguments = @('--password-mode=hex-bytes', '--encrypt', $case.userHex, $case.ownerHex, $bits) + $encryptionOptions + @(
    '--', '--object-streams=generate', (Join-Path $fixtures 'meta-1page.pdf'), $output)
  & $QpdfExe @arguments
  if ($LASTEXITCODE -ne 0) { throw 'Native recovery fixture creation failed.' }
  foreach ($role in @('user', 'owner')) {
    $hex = $case[$role + 'Hex']
    & $QpdfExe '--password-mode=hex-bytes' '--suppress-password-recovery' "--password=$hex" '--check' $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native exact-byte recovery fixture check failed.' }
    & $QpdfExe "--password=$($case[$role])" '--check' $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native text recovery fixture check failed.' }
  }
  $manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -cne $case.file }) + @([ordered]@{
    file = $case.file; sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes = (Get-Item -LiteralPath $output).Length; expectedNpages = 1
    headerVersion = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($output), 5, 3); valid = $true
    description = "Native qpdf 12.4.2 R$($case.revision) AES encryption of authored meta-1page.pdf, with $($case.name) password bytes and generated object streams. Public credentials are in security-recovery-vectors.json. Produced by tools/build-password-recovery-fixtures.ps1; native exact-byte and text-recovery controls pass for both passwords."
  })
}
[IO.File]::WriteAllText((Join-Path $fixtures 'security-recovery-vectors.json'), (($cases | ConvertTo-Json -Depth 8) + "`n"))
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
