<#
.SYNOPSIS
  Record native fixtures with public single-byte passwords for offline recovery tests.
  Randomized ciphertext is retained; regenerating requires review of hashes.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe, [switch] $Reverse)
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
if ($Reverse) {
  $cases = @(
    @{ revision = 6; name = 'utf8'; user = 'café•'; owner = 'ownér•'; userHex = '636166C3A9E280A2'; ownerHex = '6F776EC3A972E280A2' },
    @{ revision = 6; name = 'ligatures'; user = 'Œ'; owner = 'Š'; userHex = '96'; ownerHex = '97' },
    @{ revision = 6; name = 'tilde'; user = '˜'; owner = 'owner'; userHex = 'CB9C'; ownerHex = '6F776E6572' }
  )
}
$vectorFile = if ($Reverse) { 'security-reverse-vectors.json' } else { 'security-recovery-vectors.json' }
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
    description = "Native qpdf 12.4.2 R$($case.revision) AES encryption of authored meta-1page.pdf, with recorded public password bytes ($($case.name) case) and generated object streams. Public credentials are in $vectorFile. Produced by tools/build-password-recovery-fixtures.ps1; native exact-byte and text-recovery controls pass for both passwords."
  })
}
if ($Reverse) {
  $catalog = @{}
  foreach ($item in @((Get-Content -LiteralPath "$fixtures/security-recovery-vectors.json" -Raw | ConvertFrom-Json -AsHashtable)) +
      @((Get-Content -LiteralPath "$fixtures/security-r4-vectors.json" -Raw | ConvertFrom-Json -AsHashtable)) + $cases) { $catalog[$item.file] = $item }
  $records = foreach ($row in @(
    @('security-r4-utf8.pdf', 'win-to-utf8', '636166E9', $false, $true),
    @('security-r4-utf8.pdf', 'mac-to-utf8', '6361668E', $false, $true),
    @('security-recovery-r6-utf8.pdf', 'pdfdoc-to-utf8', '636166E980', $false, $true),
    @('security-recovery-r6-utf8.pdf', 'win-to-utf8', '636166E995', $false, $true),
    @('security-recovery-r6-utf8.pdf', 'mac-to-utf8', '6361668EA5', $false, $true),
    @('security-recovery-r6-utf8.pdf', 'owner-win-to-utf8', '6F776EE97295', $true, $true),
    @('security-recovery-r6-utf8.pdf', 'owner-mac-to-utf8', '6F776E8E72A5', $true, $true),
    @('security-recovery-r6-pdfdoc.pdf', 'win-to-pdfdoc', '636166E995', $false, $true),
    @('security-recovery-r6-pdfdoc.pdf', 'mac-to-pdfdoc', '6361668EA5', $false, $true),
    @('security-recovery-r6-winansi.pdf', 'pdfdoc-to-win', '636166E9A0', $false, $true),
    @('security-recovery-r6-macroman.pdf', 'pdfdoc-to-mac', '636166E980', $false, $true),
    @('security-recovery-r6-macroman.pdf', 'win-to-mac', '636166E995', $false, $true),
    @('security-recovery-r6-long.pdf', 'long-mac-to-pdfdoc', ('8E' * 127), $false, $true),
    @('security-recovery-r6-ligatures.pdf', 'utf16-be', 'FEFF0152', $false, $true),
    @('security-recovery-r6-ligatures.pdf', 'utf16-le', 'FFFE5201', $false, $true),
    @('security-recovery-r6-ligatures.pdf', 'owner-utf16-be', 'FEFF0160', $true, $true),
    @('security-recovery-r6-ligatures.pdf', 'owner-utf16-le', 'FFFE6001', $true, $true),
    @('security-recovery-r6-tilde.pdf', 'win-tilde-correction', '98', $false, $false)
  )) {
    $file = Join-Path $fixtures $row[0]
    & $QpdfExe @('--password-mode=hex-bytes', "--password=$($row[2])", '--requires-password', $file)
    $expected = if ($row[4]) { 3 } else { 0 }
    if ($LASTEXITCODE -ne $expected) { throw "Native recovery outcome changed for $($row[1])" }
    & $QpdfExe @('--password-mode=hex-bytes', "--password=$($row[2])", '--suppress-password-recovery', '--requires-password', $file)
    if ($LASTEXITCODE -ne 0) { throw 'Supplied recovery bytes unexpectedly matched exactly' }
    $role = if ($row[3]) { 'ownerHex' } else { 'userHex' }
    [ordered]@{ file = $row[0]; name = $row[1]; suppliedHex = $row[2]; owner = $row[3]; nativeMatched = $row[4]; exactHex = $catalog[$row[0]][$role] }
  }
  [IO.File]::WriteAllText((Join-Path $fixtures $vectorFile), (($records | ConvertTo-Json -Depth 8) + "`n"))
}
else { [IO.File]::WriteAllText((Join-Path $fixtures $vectorFile), (($cases | ConvertTo-Json -Depth 8) + "`n")) }
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
