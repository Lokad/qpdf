<#
.SYNOPSIS
  Derive a mixed AES/RC4/Identity R4 fixture and require native plaintext checks.
  Retains native RC4 page bytes; system AES encrypts authored strings and a file.+#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe)
$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$vector = Get-Content (Join-Path $fixtures 'security-r4-rc4-vectors.json') -Raw | ConvertFrom-Json | Where-Object file -eq 'security-r4-rc4-ascii.pdf'
if ($null -eq $vector) { throw 'Generate the R4 RC4 fixtures first.' }
$source = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes((Join-Path $fixtures $vector.file)))
$fileKey = [Convert]::FromHexString($vector.fileKeyHex)
function Encrypt-Aes([int] $Number, [int] $Generation, [string] $Text) {
    $material = [byte[]]::new(25)
    [Buffer]::BlockCopy($fileKey, 0, $material, 0, 16)
    for ($i = 0; $i -lt 3; $i++) { $material[16 + $i] = ($Number -shr (8 * $i)) -band 255 }
    for ($i = 0; $i -lt 2; $i++) { $material[19 + $i] = ($Generation -shr (8 * $i)) -band 255 }
    [Buffer]::BlockCopy([Text.Encoding]::ASCII.GetBytes('sAlT'), 0, $material, 21, 4)
    $aes = [Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = [Security.Cryptography.MD5]::HashData($material)
        $iv = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        return [byte[]]($iv + $aes.EncryptCbc([Text.Encoding]::ASCII.GetBytes($Text), $iv, [Security.Cryptography.PaddingMode]::PKCS7))
    } finally { $aes.Dispose() }
}
function Write-Text([IO.Stream] $Target, [string] $Text) {
    $bytes = [Text.Encoding]::Latin1.GetBytes($Text); $Target.Write($bytes, 0, $bytes.Length)
}
$objects = [Collections.Generic.List[object]]::new()
foreach ($match in [regex]::Matches($source, '(?ms)^(\d+) 0 obj\n(.*?)\nendobj')) {
    $number = [int]$match.Groups[1].Value; $body = $match.Groups[2].Value; $generation = 0
    switch ($number) {
        1 { $body = $body.Replace('/Type /Catalog', '/Type /Catalog /PrivateFile 8 0 R') }
        2 {
            $number = 2; $generation = 513
            $body = '<< /Title <' + [Convert]::ToHexString((Encrypt-Aes $number $generation 'TitleText')) + '> >>'
        }
        3 {
            $plain = '<x:xmpmeta>Meta</x:xmpmeta>'
            $body = "<< /Type /Metadata /Subtype /XML /Filter /Crypt /DecodeParms << /Type /CryptFilterDecodeParms /Name /Identity >> /Length $($plain.Length) >>`nstream`n$plain`nendstream"
        }
        6 {
            $body = $body.Replace('/Filter /FlateDecode', '/Filter [/Crypt /FlateDecode] /DecodeParms [<< /Name /StdCF >> null]')
        }
        7 {
            $body = $body.Replace('/CF <<', '/CF << /AesCF << /AuthEvent /DocOpen /CFM /AESV2 /Length 16 >>')
            $body = $body.Replace('/StrF /StdCF', '/StrF /AesCF /EFF /AesCF')
        }
    }
    $objects.Add(@{ Number=$number; Generation=$generation; Body=$body })
}
if ($objects.Count -ne 7) { throw 'Expected the seven-object native ASCII fixture.' }
$encoded = Encrypt-Aes 8 0 'embedded payload'
$objects.Add(@{ Number=8; Generation=0; Body=("<< /Type /EmbeddedFile /Filter /Crypt /DecodeParms << /Type /CryptFilterDecodeParms /Name /AesCF >> /Length $($encoded.Length) >>`nstream`n" + [Text.Encoding]::Latin1.GetString($encoded) + "`nendstream") })
$memory = [IO.MemoryStream]::new()
try {
    Write-Text $memory "%PDF-1.6`n%âãÏÓ`n"
    foreach ($obj in $objects | Sort-Object Number) {
        $obj.Offset = $memory.Position
        Write-Text $memory ("{0} {1} obj`n{2}`nendobj`n" -f $obj.Number, $obj.Generation, $obj.Body)
    }
    $xref = $memory.Position
    Write-Text $memory "xref`n0 1`n0000000000 65535 f `n"
    foreach ($obj in $objects | Sort-Object Number) {
        Write-Text $memory ("{0} 1`n{1:D10} {2:D5} n `n" -f $obj.Number, $obj.Offset, $obj.Generation)
    }
    $trailer = [regex]::Match($source, '(?s)trailer\s*(<<.*?>>)\s*startxref').Groups[1].Value
    if ($trailer.Length -eq 0) { throw 'Missing classic trailer.' }
    $trailer = $trailer.Replace('/Info 2 0 R', '/Info 2 513 R').Replace('/Size 8', '/Size 9')
    Write-Text $memory ("trailer`n{0}`nstartxref`n{1}`n%%EOF`n" -f $trailer, $xref)
    $filename = 'security-r4-rc4-mixed.pdf'; $output = Join-Path $fixtures $filename
    [IO.File]::WriteAllBytes($output, $memory.ToArray())
} finally { $memory.Dispose() }
foreach ($password in @('reader', 'fixture-owner')) {
    & $QpdfExe "--password=$password" --check $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native mixed-filter check failed.' }
    $actual = & $QpdfExe "--password=$password" '--show-object=2,513' $output
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n") -notmatch '/Title \(TitleText\)') { throw 'Native mixed-filter string mismatch.' }
    foreach ($pair in @(@('3', '<x:xmpmeta>Meta</x:xmpmeta>'), @('6', 'BT (Page) Tj ET'), @('8', 'embedded payload'))) {
        $actual = & $QpdfExe "--password=$password" "--show-object=$($pair[0])" --filtered-stream-data $output
        if ($LASTEXITCODE -ne 0 -or ($actual -join "`n") -cne $pair[1]) { throw 'Native mixed-filter stream mismatch.' }
    }
}
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$manifest.fixtures = @($manifest.fixtures | Where-Object file -cne $filename) + @([ordered]@{
    file=$filename; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=1; headerVersion='1.6'; valid=$true
    description='Authored derivative of security-r4-rc4-ascii.pdf with native RC4 page bytes, system-AES strings and embedded file, Identity metadata, explicit Crypt stages, and Info 2 generation 513. Public reader/fixture-owner passwords. tools/build-security-mixed-fixture.ps1 requires pinned native authentication and exact plaintext checks with both passwords.'
})
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
