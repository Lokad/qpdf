<#
.SYNOPSIS
  Derive encrypted incremental and hybrid fixtures from the native ASCII cases.
  Public fixture keys are used only to author updated strings. Native qpdf must
  authenticate each result and recover the expected newest Info dictionary.
#>
param([Parameter(Mandatory = $true)] [string] $QpdfExe)
$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtures = Join-Path $repo 'tests/Fixtures'
$manifestPath = Join-Path $fixtures 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable

function Encrypt-Title([byte[]] $Key) {
    $aes = [Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $Key
        $iv = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $cipher = $aes.EncryptCbc([Text.Encoding]::ASCII.GetBytes('UpdatedTitle'), $iv, [Security.Cryptography.PaddingMode]::PKCS7)
        return '<' + [Convert]::ToHexString([byte[]]($iv + $cipher)).ToLowerInvariant() + '>'
    }
    finally { $aes.Dispose() }
}

foreach ($case in @(
    @{ Revision=4; Kind='incremental' },
    @{ Revision=6; Kind='incremental' },
    @{ Revision=6; Kind='hybrid' },
    @{ Revision=6; Kind='hybrid-incremental' }
)) {
    $revision = $case.Revision
    $sourceName = "security-r$revision-ascii.pdf"
    $sourcePath = Join-Path $fixtures $sourceName
    $source = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($sourcePath))
    $previous = [int][regex]::Match($source, 'startxref\s+(\d+)\s+%%EOF\s*$').Groups[1].Value
    $rawTrailer = & $QpdfExe --password=reader --show-object=trailer $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not read source trailer.' }
    $root = [regex]::Match($rawTrailer, '/Root (\d+ 0 R)').Groups[1].Value
    $info = [int][regex]::Match($rawTrailer, '/Info (\d+) 0 R').Groups[1].Value
    $encrypt = [regex]::Match($rawTrailer, '/Encrypt (\d+ 0 R)').Groups[1].Value
    $size = [int][regex]::Match($rawTrailer, '/Size (\d+)').Groups[1].Value
    $ids = [regex]::Match($rawTrailer, '/ID (\[.*?\])').Groups[1].Value
    if (-not $root -or -not $encrypt -or -not $ids -or $info -le 0 -or $size -le $info) { throw 'Unexpected source trailer.' }
    $trailer = "/Root $root /Info $info 0 R /Encrypt $encrypt /Size $size /ID $ids"

    if ($case.Kind.StartsWith('hybrid')) {
        $offsets = @{}
        foreach ($match in [regex]::Matches($source, '(?m)^(\d+) 0 obj\n')) {
            $offsets[[int]$match.Groups[1].Value] = $match.Index
        }
        # The supplemental stream supplies the compressed entries; the classic
        # table supplies physical entries. No entry falsely deletes a member.
        $xref = $source.Length
        $source += "xref`n0 1`n0000000000 65535 f `n"
        for ($number = 1; $number -lt $size; $number++) {
            if ($offsets.ContainsKey($number)) { $source += "$number 1`n{0:D10} 00000 n `n" -f $offsets[$number] }
        }
        $source += "trailer`n<< $trailer /XRefStm $previous >>`nstartxref`n$xref`n%%EOF`n"
        $previous = $xref
    }

    $expected = 'TitleText'
    if ($case.Kind.Contains('incremental')) {
        $vectors = Get-Content -LiteralPath (Join-Path $fixtures "security-r$revision-vectors.json") -Raw | ConvertFrom-Json -AsHashtable
        $vector = $vectors | Where-Object { $_.file -eq $sourceName }
        $key = [Convert]::FromHexString($vector.fileKeyHex)
        if ($revision -eq 4) {
            $material = [byte[]]::new(25)
            [Buffer]::BlockCopy($key, 0, $material, 0, 16)
            $material[16] = $info -band 255; $material[17] = ($info -shr 8) -band 255; $material[18] = ($info -shr 16) -band 255
            [Buffer]::BlockCopy([Text.Encoding]::ASCII.GetBytes('sAlT'), 0, $material, 21, 4)
            $key = [Security.Cryptography.MD5]::HashData($material)
        }
        $offset = $source.Length
        $source += "$info 0 obj`n<< /Title $(Encrypt-Title $key) /Revision 2 >>`nendobj`n"
        $xref = $source.Length
        $source += "xref`n$info 1`n{0:D10} 00000 n `n" -f $offset
        $source += "trailer`n<< $trailer /Prev $previous >>`nstartxref`n$xref`n%%EOF`n"
        $expected = 'UpdatedTitle'
    }

    $filename = "security-r$revision-$($case.Kind).pdf"
    $output = Join-Path $fixtures $filename
    [IO.File]::WriteAllBytes($output, [Text.Encoding]::Latin1.GetBytes($source))
    & $QpdfExe --password=reader --check $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Native qpdf rejected $filename." }
    $decoded = & $QpdfExe --password=reader "--show-object=$info" $output
    if ($LASTEXITCODE -ne 0 -or ($decoded -join "`n") -notmatch "/Title \($expected\)") { throw "Incorrect newest Info in $filename." }
    $manifest.fixtures = @($manifest.fixtures | Where-Object { $_.file -ne $filename }) + @([ordered]@{
        file=$filename; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes=(Get-Item -LiteralPath $output).Length; expectedNpages=1; headerVersion=$(if ($revision -eq 6) { '1.7' } else { '1.6' }); valid=$true
        description="R$revision $($case.Kind) derivative of $sourceName, with unchanged public credentials/key. Incremental cases replace Info with encrypted UpdatedTitle; hybrid cases retain native object streams. Generated by tools/build-security-xref-fixtures.ps1 and checked with qpdf 12.4.2."
    })
}
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 20) + "`n"))
