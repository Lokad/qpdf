<#
.SYNOPSIS
  Derive an R4 fixture with a large object number and nonzero generation.
  Uses the recorded public fixture key, then requires native qpdf to authenticate
  and recover the known plaintext. Run after the R4 security fixture generator.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe)
$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$vectorPath = Join-Path $fixtures 'security-r4-vectors.json'
$vectors = @(Get-Content -LiteralPath $vectorPath -Raw | ConvertFrom-Json -AsHashtable)
$sourceVector = $vectors | Where-Object { $_.file -eq 'security-r4-ascii.pdf' }
if ($null -eq $sourceVector) { throw 'Generate the R4 ASCII fixture first.' }
$source = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes((Join-Path $fixtures $sourceVector.file)))
$trailer = [regex]::Match($source, '(?s)trailer\s*(<<.*?>>)\s*startxref').Groups[1].Value
$info = [regex]::Match($trailer, '/Info (\d+) 0 R')
if (-not $info.Success) { throw 'Expected a classic trailer with Info.' }
$oldInfo = [int]$info.Groups[1].Value
$newInfo = 66051 # 0x010203: exercise all three object-number bytes.
$generation = 513 # 0x0201: exercise both generation bytes.
$fileKey = [Convert]::FromHexString($sourceVector.fileKeyHex)
$material = [byte[]]::new(25)
[Buffer]::BlockCopy($fileKey, 0, $material, 0, 16)
$material[16] = 3; $material[17] = 2; $material[18] = 1
$material[19] = 1; $material[20] = 2
[Buffer]::BlockCopy([Text.Encoding]::ASCII.GetBytes('sAlT'), 0, $material, 21, 4)
$objectKey = [Security.Cryptography.MD5]::HashData($material)

function Encrypt-Text([string] $Text) {
    $aes = [Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $objectKey
        $iv = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $cipher = $aes.EncryptCbc([Text.Encoding]::ASCII.GetBytes($Text), $iv, [Security.Cryptography.PaddingMode]::PKCS7)
        return '<' + [Convert]::ToHexString([byte[]]($iv + $cipher)).ToLowerInvariant() + '>'
    }
    finally { $aes.Dispose() }
}
function Write-Text([IO.Stream] $Target, [string] $Text) {
    $bytes = [Text.Encoding]::Latin1.GetBytes($Text)
    $Target.Write($bytes, 0, $bytes.Length)
}
$objects = [Collections.Generic.List[object]]::new()
foreach ($match in [regex]::Matches($source, '(?ms)^(\d+) 0 obj\n(.*?)\nendobj')) {
    $number = [int]$match.Groups[1].Value
    $body = $match.Groups[2].Value
    $gen = 0
    if ($number -eq $oldInfo) {
        $number = $newInfo; $gen = $generation
        $body = '<< /Title ' + (Encrypt-Text 'TitleText') + ' /ModDate ' + (Encrypt-Text 'D:20240101000000Z') + ' >>'
    }
    $objects.Add(@{ Number=$number; Generation=$gen; Body=$body })
}
if ($objects.Count -ne 7) { throw 'Expected seven source objects in the authored ASCII fixture.' }
$memory = [IO.MemoryStream]::new()
try {
    Write-Text $memory "%PDF-1.6`n%âãÏÓ`n"
    foreach ($obj in $objects | Sort-Object Number) {
        $obj.Offset = $memory.Position
        Write-Text $memory ("{0} {1} obj`n{2}`nendobj`n" -f $obj.Number, $obj.Generation, $obj.Body)
    }
    $xref = $memory.Position
    $byNumber = @{}
    foreach ($obj in $objects) { $byNumber[$obj.Number] = $obj }
    Write-Text $memory ("xref`n0 {0}`n" -f ($newInfo + 1))
    for ($number = 0; $number -le $newInfo; $number++) {
        if ($byNumber.ContainsKey($number)) {
            $obj = $byNumber[$number]
            Write-Text $memory ("{0:D10} {1:D5} n `n" -f $obj.Offset, $obj.Generation)
        }
        else {
            $next = $number + 1
            while ($next -le $newInfo -and $byNumber.ContainsKey($next)) { $next++ }
            if ($next -gt $newInfo) { $next = 0 }
            $freeGeneration = if ($number -eq 0) { 65535 } else { 0 }
            Write-Text $memory ("{0:D10} {1:D5} f `n" -f $next, $freeGeneration)
        }
    }
    $trailer = [regex]::Replace($trailer, '/Info \d+ 0 R', "/Info $newInfo $generation R")
    $trailer = [regex]::Replace($trailer, '/Size \d+', "/Size $($newInfo + 1)")
    Write-Text $memory ("trailer`n{0}`nstartxref`n{1}`n%%EOF`n" -f $trailer, $xref)
    $filename = 'security-r4-generations.pdf'
    $output = Join-Path $fixtures $filename
    [IO.File]::WriteAllBytes($output, $memory.ToArray())
}
finally { $memory.Dispose() }
& $QpdfExe --password=reader --check $output | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Native qpdf rejected the generation fixture.' }
$decoded = & $QpdfExe --password=reader "--show-object=$newInfo,$generation" $output
if ($LASTEXITCODE -ne 0 -or ($decoded -join "`n") -notmatch '/Title \(TitleText\)') {
    throw 'Native qpdf did not recover the generation fixture plaintext.'
}
$derived = @{}
foreach ($entry in $sourceVector.GetEnumerator()) { $derived[$entry.Key] = $entry.Value }
$derived.file = $filename
$vectors = @($vectors | Where-Object { $_.file -ne $filename }) + @($derived)
[IO.File]::WriteAllText($vectorPath, (($vectors | ConvertTo-Json -Depth 5) + "`n"))
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -ne $filename }) + @([ordered]@{
    file=$filename; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=1; headerVersion='1.6'; valid=$true
    description='R4 native ASCII fixture with Info rewritten as object 66051 generation 513 using its public fixture key. Native qpdf 12.4.2 verifies authentication and recovers TitleText. Generated by tools/build-security-generation-fixture.ps1; tests all object-number and generation bytes in AES-128 key derivation.'
})
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
