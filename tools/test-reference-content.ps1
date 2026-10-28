param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll,
  [Parameter(Mandatory = $true)] [string] $WorkRoot
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $WorkRoot) { throw 'Content test workspace already exists.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
. (Join-Path $PSScriptRoot 'ReferenceContent.ps1')

function Write-TestPdf([string]$Name, [string[]]$Objects, [bool]$Reverse) {
  $path = Join-Path $WorkRoot ($Name + '.pdf')
  $buffer = [IO.MemoryStream]::new()
  try {
    $write = { param([string]$Text) $buffer.Write([Text.Encoding]::Latin1.GetBytes($Text)) }
    & $write "%PDF-1.4`n"
    $offsets = [long[]]::new($Objects.Length)
    $order = @(0..($Objects.Length - 1))
    if ($Reverse) { [Array]::Reverse($order) }
    foreach ($index in $order) {
      $offsets[$index] = $buffer.Position
      & $write "$($index + 1) 0 obj`n$($Objects[$index])`nendobj`n"
    }
    $xref = $buffer.Position
    & $write "xref`n0 $($Objects.Length + 1)`n0000000000 65535 f `n"
    foreach ($offset in $offsets) { & $write (('{0:D10} 00000 n ' -f $offset) + "`n") }
    & $write "trailer`n<< /Size $($Objects.Length + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n"
    [IO.File]::WriteAllBytes($path, $buffer.ToArray())
  } finally { $buffer.Dispose() }
  return $path
}

function New-TextObjects([string[]]$Texts, [string[]]$PageContents) {
  $objects = [Collections.Generic.List[string]]::new()
  $font = 3 + $PageContents.Length + $Texts.Length
  $kids = @(for ($i = 0; $i -lt $PageContents.Length; $i++) { "$($i + 3) 0 R" }) -join ' '
  $objects.Add('<< /Type /Catalog /Pages 2 0 R >>')
  $objects.Add("<< /Type /Pages /Count $($PageContents.Length) /Kids [$kids] >>")
  foreach ($page in $PageContents) {
    $references = @($page.Split(',') | ForEach-Object { "$([int]$_ + 3 + $PageContents.Length) 0 R" }) -join ' '
    $objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 $font 0 R >> >> /Contents [$references] >>")
  }
  foreach ($text in $Texts) {
    $payload = "BT /F1 12 Tf 1 0 0 1 10 10 Tm ($text) Tj ET`n"
    $objects.Add("<< /Length $($payload.Length) >>`nstream`n${payload}endstream")
  }
  $objects.Add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
  return $objects.ToArray()
}

function Assert-Rejected([scriptblock]$Action, [string]$ExpectedError, [string]$Message) {
  $rejected = $false
  try { & $Action } catch {
    if (-not $_.Exception.Message.Contains($ExpectedError, [StringComparison]::Ordinal)) { throw }
    $rejected = $true
  }
  if (-not $rejected) { throw $Message }
}

Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  $negativePairs = [Collections.Generic.List[object]]::new()
  foreach ($word in @('ordinary', 'stream', 'endstream')) {
    $left = Write-TestPdf "$word-A" (New-TextObjects @("prefix-A $word same") @('0')) $false
    $right = Write-TestPdf "$word-B" (New-TextObjects @("prefix-B $word same") @('0')) $false
    $negativePairs.Add(@($left, $right))
  }
  $ordered = Write-TestPdf 'order-AB' (New-TextObjects @('First','Second') @('0','1')) $false
  $swapped = Write-TestPdf 'order-BA' (New-TextObjects @('First','Second') @('1','0')) $false
  $negativePairs.Add(@($ordered, $swapped))
  $arrayAB = Write-TestPdf 'array-AB' (New-TextObjects @('First','Second') @('0,1')) $false
  $arrayBA = Write-TestPdf 'array-BA' (New-TextObjects @('First','Second') @('1,0')) $false
  $negativePairs.Add(@($arrayAB, $arrayBA))
  $fontB = New-TextObjects @('A') @('0')
  $fontC = [string[]]$fontB.Clone()
  $fontB[-1] = '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /B] >> >>'
  $fontC[-1] = $fontB[-1].Replace('/B]', '/C]')
  $fontLeft = Write-TestPdf 'font-B' $fontB $false
  $fontRight = Write-TestPdf 'font-C' $fontC $false
  if ((Read-ReferenceContent $QpdfExe $fontLeft).Signature -cne (Read-ReferenceContent $QpdfExe $fontRight).Signature) { throw 'Font control must have equal stream signatures.' }
  $negativePairs.Add(@($fontLeft, $fontRight))
  foreach ($pair in $negativePairs) {
    $expected = if ($pair[0] -eq $fontLeft) { 'Independent ordered page text differs' } else { 'Decoded stream bytes or logical page content differ' }
    Assert-Rejected { Assert-ReferenceContent $QpdfExe $ProbeDll $pair[0] $pair[1] } $expected "Changed content accepted: $($pair[0])"
  }
  $physical = Write-TestPdf 'physical reverse É日本' (New-TextObjects @('First','Second') @('0','1')) $true
  Assert-ReferenceContent $QpdfExe $ProbeDll $ordered $physical
  $compressed = Join-Path $WorkRoot 'compressed.pdf'
  & $QpdfExe --stream-data=compress $ordered $compressed
  if ($LASTEXITCODE -ne 0) { throw 'Compressed control generation failed.' }
  Assert-ReferenceContent $QpdfExe $ProbeDll $ordered $compressed
  $qdf = Join-Path $WorkRoot 'indirect-lengths.pdf'
  & $QpdfExe --qdf --object-streams=disable --normalize-content=n $ordered $qdf
  if ($LASTEXITCODE -ne 0) { throw 'QDF control generation failed.' }
  Assert-ReferenceContent $QpdfExe $ProbeDll $ordered $qdf
  $emptyObjects = New-TextObjects @('') @('0')
  $emptyObjects[3] = "<< /Length 0 >>`nstream`nendstream"
  $emptyFile = Write-TestPdf 'empty-stream' $emptyObjects $false
  $emptyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]::new(0)))
  if ((Read-ReferenceContent $QpdfExe $emptyFile).StreamHashes -cnotcontains $emptyHash) { throw 'Empty stream disappeared.' }
  Assert-ReferenceContent $QpdfExe $ProbeDll $emptyFile $emptyFile
  $binary = [byte[]](@(0..255) + [Text.Encoding]::Latin1.GetBytes("`nstream`nendstream`n"))
  $objects = New-TextObjects @('Binary') @('0')
  $objects[0] = '<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R >>'
  $objects += "<< /Length 7 0 R >>`nstream`n$([Text.Encoding]::Latin1.GetString($binary))endstream"
  $objects += $binary.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
  $binaryFile = Write-TestPdf 'binary-indirect' $objects $false
  $binaryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($binary))
  if ((Read-ReferenceContent $QpdfExe $binaryFile).StreamHashes -cnotcontains $binaryHash) { throw 'Binary or indirect-length payload changed.' }
  $missingFont = New-TextObjects @('Missing font') @('0')
  $missingFont[2] = $missingFont[2].Replace('/Font << /F1 5 0 R >>', '')
  $missingFontFile = Write-TestPdf 'missing-font' $missingFont $false
  Assert-Rejected { Assert-ReferenceContent $QpdfExe $ProbeDll $missingFontFile $missingFontFile } 'Independent page-text inspection failed' 'Independent reader failure accepted.'
  $malformed = Join-Path $WorkRoot 'malformed.pdf'
  [IO.File]::WriteAllText($malformed, 'not a PDF')
  Assert-Rejected { Read-ReferenceContent $QpdfExe $malformed } 'Native content inspection failed (2)' 'Malformed input accepted.'
  Assert-Rejected { Read-ReferenceContent $QpdfExe (Join-Path $WorkRoot 'absent.pdf') } 'Native content inspection failed (2)' 'Missing input accepted.'
  Write-Output 'Reference content controls passed: six changed-content pairs reject; physical order, compression, QDF and empty streams agree; binary/indirect lengths stay exact; malformed/missing inputs and independent reader failures reject.'
} finally { Complete-ReferenceProbe }
