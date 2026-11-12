<#
.SYNOPSIS
  Generate offline encryption fixtures from authored input using pinned qpdf.
  Passwords and keys are public test data. Native randomness is retained: a
  regeneration intentionally changes ciphertext, hashes and recorded vectors.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe, [ValidateSet(2, 3, 4, 5, 6)] [int] $Revision, [switch] $Attachments, [switch] $Rc4)
$ErrorActionPreference = 'Stop'
if ($Revision -eq 0) { $Revision = 6 }
if ($Rc4 -and $Revision -ne 4) { throw 'RC4 fixture generation requires revision 4.' }
$prefix = if ($Rc4) { 'security-r4-rc4' } else { "security-r$Revision" }
$cipher = if ($Rc4 -or $Revision -lt 4) { 'RC4' } else { 'AES' }
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.generation -notmatch 'Security fixtures') {
    $manifest.generation += ' Security fixtures record randomized native encryption of authored inputs; their individual descriptions identify generation and public test credentials.'
}
$vectors = [Collections.Generic.List[object]]::new()
if ($Attachments) {
    # Derive only these composition fixtures; leave existing vectors untouched.
    $scratch = Join-Path $repo 'artifacts/security-attachment-fixtures'
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $payload = Join-Path $scratch 'private.bin'
    [IO.File]::WriteAllBytes($payload, [Text.Encoding]::UTF8.GetBytes("Private attachment payload.`n"))
    $seed = if ($Revision -le 4) { "$prefix-pdfdoc.pdf" } else { "$prefix-empty.pdf" }
    $password = if ($Revision -le 4) { '636166e980' } else { '' }
    $filename = "$prefix-attachment.pdf"
    $output = Join-Path $fixtures $filename
    $protection = if ($Rc4) {
        @('--allow-weak-crypto', '--encrypt', $password, '6F776E6572E991', '128', '--use-aes=n', '--force-V4', '--modify=none', '--print=low', '--extract=n', '--')
    } else { @() }
    & $QpdfExe '--password-mode=hex-bytes' "--password=$password" @protection (Join-Path $fixtures $seed) `
        '--add-attachment' $payload '--key=private.bin' '--filename=private.bin' `
        '--mimetype=application/octet-stream' '--description=Public synthetic attachment.' '--' `
        '--object-streams=generate' $output
    if ($LASTEXITCODE -ne 0) { throw 'Native attachment fixture creation failed.' }
    & $QpdfExe '--password-mode=hex-bytes' "--password=$password" '--check' $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native attachment fixture validation failed.' }
    $manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -cne $filename }) + @([ordered]@{
        file=$filename; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=1; headerVersion=[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($output), 5, 3); valid=$true
        description="Native qpdf 12.4.2 attachment derivative of $seed, retaining its public credentials and metadata policy. Embedded private.bin contains UTF-8 Private attachment payload. followed by LF. Object streams are generated. Produced by tools/build-security-fixtures.ps1 -Attachments; randomized ciphertext is recorded for offline composition tests."
    })
    [IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
    return
}
$cases = @(
    @{ Name='ascii'; User='reader'; Owner='fixture-owner'; Metadata=$true },
    @{ Name='empty'; User=''; Owner='fixture-owner'; Metadata=$false },
    @{ Name='utf8'; User='café-π-密碼'; Owner='propriétaire-Δ'; Metadata=$true },
    @{ Name='truncated'; User=('x' * 126 + 'é-ending'); Owner=('owner-' * 25); Metadata=$false }
)
if ($Revision -le 4) {
    $cases = @(
        @{ Name='ascii'; User='reader'; Owner='fixture-owner'; Metadata=$true; Objects='disable' },
        @{ Name='empty'; User=''; Owner='fixture-owner'; Metadata=$false; Objects='generate' },
        @{ Name='pdfdoc'; UserHex='636166E980'; OwnerHex='6F776E6572E991'; Metadata=$true; Objects='generate' },
        @{ Name='utf8'; User='café'; Owner='propriétaire'; Metadata=$true; Objects='disable' },
        @{ Name='truncated'; User=('user-' * 10); Owner=('owner-' * 10); Metadata=$false; Objects='disable' }
    )
}
$bits = if ($Revision -ge 5) { 256 } elseif ($Revision -eq 2) { 40 } else { 128 }
foreach ($case in $cases) {
    if ($Revision -lt 4) { $case.Metadata = $true }
    $filename = $prefix + '-' + $case.Name + '.pdf'
    $output = Join-Path $fixtures $filename
    $userHex = if ($case.ContainsKey('UserHex')) { $case.UserHex } else { [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($case.User)) }
    $ownerHex = if ($case.ContainsKey('OwnerHex')) { $case.OwnerHex } else { [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($case.Owner)) }
    # Apply the revision's byte limit before native creation. Keep the
    # longer original inputs in the vectors to test reader-side truncation.
    $maxHex = if ($Revision -ge 5) { 254 } else { 64 }
    $creationUser = $userHex.Substring(0, [Math]::Min($userHex.Length, $maxHex))
    $creationOwner = $ownerHex.Substring(0, [Math]::Min($ownerHex.Length, $maxHex))
    $arguments = @('--password-mode=hex-bytes', '--encrypt', $creationUser, $creationOwner, "$bits")
    $arguments += $(if ($Revision -eq 2) { @('--print=n', '--modify=n', '--extract=n', '--annotate=n') } else { @('--modify=none', '--print=low', '--extract=n') })
    if ($Revision -eq 4) { $arguments += $(if ($Rc4) { @('--use-aes=n', '--force-V4') } else { '--use-aes=y' }) }
    if ($Revision -eq 3) { $arguments += '--use-aes=n' }
    if ($Rc4 -or $Revision -lt 4) { $arguments = @('--allow-weak-crypto') + $arguments }
    if ($Revision -eq 5) { $arguments += '--force-R5' }
    if (-not $case.Metadata) { $arguments += '--cleartext-metadata' }
    $objectMode = if ($case.ContainsKey('Objects')) { $case.Objects } else { 'generate' }
    $arguments += @('--', "--object-streams=$objectMode", (Join-Path $fixtures 'meta-1page.pdf'), $output)
    & $QpdfExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Encryption fixture generation failed: $filename" }
    & $QpdfExe --password-mode=hex-bytes "--password=$ownerHex" --check $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Encryption fixture check failed: $filename" }
    $inspection = & $QpdfExe --password-mode=hex-bytes "--password=$ownerHex" --json --json-key=qpdf --json-stream-data=inline --decode-level=none $output
    if ($LASTEXITCODE -ne 0) { throw 'Encryption dictionary inspection failed.' }
    $objects = ($inspection -join "`n" | ConvertFrom-Json -AsHashtable).qpdf[1]
    $reference = $objects.trailer.value['/Encrypt']
    $number = ($reference -replace ' R$', '') -replace ' ', ','
    $dictionary = & $QpdfExe --password-mode=hex-bytes "--password=$ownerHex" "--show-object=$number" $output
    if ($LASTEXITCODE -ne 0) { throw 'Encryption dictionary display failed.' }
    $keyDisplay = & $QpdfExe --password-mode=hex-bytes "--password=$ownerHex" --show-encryption --show-encryption-key $output
    if ($LASTEXITCODE -ne 0) { throw 'Fixture key inspection failed.' }
    $key = [regex]::Match(($keyDisplay -join "`n"), '(?im)^encryption key = ([0-9a-f]+)$')
    if (-not $key.Success -or $key.Groups[1].Value.Length -ne $bits / 4) { throw "Expected a $bits-bit native fixture key." }
    $streams = @($objects.GetEnumerator() | Where-Object {
        $_.Value.ContainsKey('stream') -and $_.Value.stream.dict['/Type'] -ne '/XRef' -and
        ($case.Metadata -or $_.Value.stream.dict['/Type'] -ne '/Metadata')
    } | Sort-Object Name | ForEach-Object {
        [ordered]@{ number=[int](($_.Key -replace '^obj:', '') -split ' ')[0]; plainBase64=$_.Value.stream.data }
    })
    $vector = [ordered]@{ file=$filename; userHex=$userHex; ownerHex=$ownerHex; fileKeyHex=$key.Groups[1].Value; dictionary=($dictionary -join "`n"); streams=$streams }
    if ($Revision -le 4) {
        $identifier = $objects.trailer.value['/ID'][0]
        if (-not $identifier.StartsWith('b:')) { throw 'Expected a binary document identifier.' }
        $vector.idHex = $identifier.Substring(2)
    }
    $vectors.Add($vector)
    $manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -cne $filename }) + @([ordered]@{
        file=$filename; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=1; headerVersion=[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($output), 5, 3); valid=$true
        description="Native qpdf 12.4.2 $cipher-$bits R$Revision encryption of authored meta-1page.pdf, object-stream mode $objectMode. Public credentials/key in $prefix-vectors.json. Generated by tools/build-security-fixtures.ps1; randomized ciphertext is recorded for offline tests."
    })
}
[IO.File]::WriteAllText((Join-Path $fixtures "$prefix-vectors.json"), (($vectors.ToArray() | ConvertTo-Json -Depth 5) + "`n"))
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
if ($Revision -eq 4 -and -not $Rc4) { & (Join-Path $PSScriptRoot 'build-security-generation-fixture.ps1') -QpdfExe $QpdfExe }
