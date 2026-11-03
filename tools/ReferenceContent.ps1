# Development-only content evidence from the pinned native parser.
function Read-ReferenceContent {
  param([string]$QpdfExe, [string]$File)
  $ErrorActionPreference = 'Stop'
  $jsonFile = $File + '.reference.json'
  $errorFile = $File + '.reference-error.txt'
  $PSNativeCommandUseErrorActionPreference = $false
  & $QpdfExe --json --json-key=pages --json-key=qpdf --json-stream-data=inline --decode-level=generalized $File 1> $jsonFile 2> $errorFile
  $code = $LASTEXITCODE
  if ($code -notin @(0,3)) { throw "Native content inspection failed ($code): $File; see $errorFile" }
  $json = Get-Content -LiteralPath $jsonFile -Raw | ConvertFrom-Json -AsHashtable
  if ($json.version -ne 2 -or $json.qpdf.Count -ne 2 -or -not $json.ContainsKey('pages')) {
    throw "Incomplete native content inspection: $File"
  }
  $streams = [Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::Ordinal)
  $hashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
  $allText = [Collections.Generic.List[string]]::new()
  $allBytes = [IO.MemoryStream]::new()
  try {
    foreach ($entry in $json.qpdf[1].GetEnumerator()) {
      if (-not $entry.Value.ContainsKey('stream')) { continue }
      $stream = $entry.Value.stream
      if (-not $stream.ContainsKey('data')) { throw "Missing native stream data: $File $($entry.Key)" }
      $reference = $entry.Key.Substring(4)
      $bytes = [Convert]::FromBase64String($stream.data)
      $streams.Add($reference, $bytes)
      $hashes.Add($reference, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))
      $allBytes.Write($bytes)
      $allText.Add([Text.Encoding]::Latin1.GetString($bytes))
    }
    $pageHashes = [Collections.Generic.List[object]]::new()
    $pageText = [Collections.Generic.List[string]]::new()
    foreach ($page in $json.pages) {
      $contentHashes = [Collections.Generic.List[string]]::new()
      foreach ($reference in $page.contents) {
        if (-not $streams.ContainsKey($reference)) { throw "Missing native page-content stream: $File $reference" }
        $contentHashes.Add($hashes[$reference])
        $pageText.Add([Text.Encoding]::Latin1.GetString($streams[$reference]))
      }
      $pageHashes.Add([string[]]$contentHashes.ToArray())
    }
    [string[]] $streamHashes = @($hashes.Values | Sort-Object)
    $signature = [ordered]@{ pages = $pageHashes.ToArray(); streams = $streamHashes } | ConvertTo-Json -Depth 5 -Compress
    $bytes = $allBytes.ToArray()
    return [pscustomobject]@{
      Signature = $signature; StreamHashes = $streamHashes; PageText = $pageText -join "`n"
      AllBytes = $bytes; AllText = $allText -join "`n"; OracleExitCode = $code
    }
  } finally { $allBytes.Dispose() }
}

function Test-ReferenceTextPosition {
  param([string]$Text, [double]$X, [double]$Y)
  # Retain the original five-decimal placement acceptance while allowing the
  # engine's round-trip decimal spelling. These fixtures emit Td on its own line.
  $positions = [regex]::Matches($Text, '(?m)^[ \t]*([+-]?[0-9.]+)[ \t]+([+-]?[0-9.]+)[ \t]+Td[ \t]*\r?$')
  foreach ($position in $positions) {
    [double]$xCoordinate = 0
    [double]$yCoordinate = 0
    $style = [Globalization.NumberStyles]::AllowLeadingSign -bor [Globalization.NumberStyles]::AllowDecimalPoint
    if ([double]::TryParse($position.Groups[1].Value, $style, [Globalization.CultureInfo]::InvariantCulture, [ref]$xCoordinate) -and
        [double]::TryParse($position.Groups[2].Value, $style, [Globalization.CultureInfo]::InvariantCulture, [ref]$yCoordinate) -and
        [double]::IsFinite($xCoordinate) -and [double]::IsFinite($yCoordinate) -and
        [Math]::Round($xCoordinate, 5, [MidpointRounding]::AwayFromZero) -eq $X -and
        [Math]::Round($yCoordinate, 5, [MidpointRounding]::AwayFromZero) -eq $Y) { return $true }
  }
  return $false
}

function Assert-ReferenceContent {
  param([string]$QpdfExe, [string]$ProbeDll, [string]$Left, [string]$Right)
  $leftContent = Read-ReferenceContent $QpdfExe $Left
  $rightContent = Read-ReferenceContent $QpdfExe $Right
  if ($leftContent.Signature -cne $rightContent.Signature) {
    throw "Decoded stream bytes or logical page content differ: $Left / $Right"
  }
  $leftText = Invoke-ReferenceProbe -Arguments @($ProbeDll, 'page-text', '--input', $Left) 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "Independent page-text inspection failed: $Left; $leftText" }
  $rightText = Invoke-ReferenceProbe -Arguments @($ProbeDll, 'page-text', '--input', $Right) 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "Independent page-text inspection failed: $Right; $rightText" }
  $leftPages = ConvertTo-Json -InputObject ($leftText | ConvertFrom-Json -NoEnumerate) -Compress
  $rightPages = ConvertTo-Json -InputObject ($rightText | ConvertFrom-Json -NoEnumerate) -Compress
  if ($leftPages -cne $rightPages) { throw "Independent ordered page text differs: $Left / $Right" }
}
