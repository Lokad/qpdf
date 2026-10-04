<#
.SYNOPSIS
  Explicit parser differential lane over authored PDFs. Requires a freshly
  built development probe and the pinned qpdf oracle; ordinary tests never
  invoke this script. Each run has its own inputs, outputs and observations.
#>
param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll
)

$ErrorActionPreference = 'Stop'
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runRoot = Join-Path $repoRoot ('external/parser-checks/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $runRoot 'inputs') -Force | Out-Null
$observations = [Collections.Generic.List[object]]::new()

function Write-Input {
  param([string] $Name, [string] $Body, [bool] $BrokenXref)
  $objects = [string[]]@(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Audit 5 0 R >>',
    "<< /Length 0 >>`nstream`n`nendstream",
    $Body
  )
  $bytes = [IO.MemoryStream]::new()
  try {
    $bytes.Write([Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n"))
    $offsets = [Collections.Generic.List[long]]::new()
    for ($i = 0; $i -lt $objects.Length; $i++) {
      $offsets.Add($bytes.Position)
      $bytes.Write([Text.Encoding]::ASCII.GetBytes("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n"))
    }
    $xref = $bytes.Position
    $bytes.Write([Text.Encoding]::ASCII.GetBytes("xref`n0 6`n0000000000 65535 f `n"))
    foreach ($offset in $offsets) {
      $bytes.Write([Text.Encoding]::ASCII.GetBytes($offset.ToString('D10', [Globalization.CultureInfo]::InvariantCulture) + " 00000 n `n"))
    }
    if ($BrokenXref) { $xref = 0 }
    $bytes.Write([Text.Encoding]::ASCII.GetBytes("trailer`n<< /Root 1 0 R /Size 6 >>`nstartxref`n$xref`n%%EOF`n"))
    [IO.File]::WriteAllBytes((Join-Path $runRoot "inputs/$Name.pdf"), $bytes.ToArray())
  }
  finally { $bytes.Dispose() }
}

function Invoke-Managed {
  param([string] $Tag, [string[]] $Arguments, [string[]] $Mappings)
  $probeArgs = @($ProbeDll, 'emit', '--workdir', $runRoot)
  foreach ($mapping in $Mappings) { $probeArgs += @('--out', $mapping) }
  $probeArgs += @('--') + $Arguments
  $lines = @(& dotnet @probeArgs 2>&1)
  $processExit = $LASTEXITCODE
  $lines | Set-Content -LiteralPath (Join-Path $runRoot "$Tag-managed.log") -Encoding utf8NoBOM
  $status = [regex]::Match(($lines -join "`n"), 'emit: status=(\w+) exit=(\d+)')
  if ($processExit -ne 0 -or -not $status.Success -or $status.Groups[1].Value -ne 'Completed') {
    throw "Parser probe failed ($Tag, process exit $processExit). See $runRoot."
  }
  $listing = @($lines | Where-Object { $_ -match '^emit: diagnostic ObjectListing Info ' } |
    ForEach-Object { $_ -replace '^emit: diagnostic ObjectListing Info ', '' }) -join "`n"
  return @{ Exit = [int]$status.Groups[2].Value; Value = $listing.Trim() }
}

$specs = @(
  @{ Name = 'word'; Body = '[ 1 @ 2 ]' },
  @{ Name = 'four-words'; Body = '[ @ @ @ @ ]' },
  @{ Name = 'key'; Body = '<< 5 /A >>' },
  @{ Name = 'missing-value'; Body = '<< /A >>' },
  @{ Name = 'duplicate'; Body = '<< /A 1 /A 2 >>' },
  @{ Name = 'cluster'; Body = '[ @ @ @ @ @ @ ]' },
  @{ Name = 'separated'; Body = '[ @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 ]' },
  @{ Name = 'closer'; Body = '[ 1 >> 2 ]' },
  @{ Name = 'missing-close'; Body = '<< /A 1' },
  @{ Name = 'collision'; Body = '<< 5 /QPDFFake1 3 /A 1 >>' },
  @{ Name = 'top-word'; Body = '@' },
  @{ Name = 'endobj-word'; Body = '[ 1 endobj 2 ]' },
  @{ Name = 'number-word'; Body = '[ 1x 2 ]' },
  @{ Name = 'bad-hex'; Body = '[ <ABCG> 1 ]' },
  @{ Name = 'brace'; Body = '[ { } ]' },
  @{ Name = 'lone-closer'; Body = '[ 1 > 2 ]' },
  @{ Name = 'broken-word'; Body = '[ 1 @ 2 ]'; Broken = $true }
)

foreach ($spec in $specs) {
  Write-Input $spec.Name $spec.Body ([bool]$spec.Broken)
  foreach ($budget in @(0, 1, 2, 3, 6, 15)) {
    $tag = "$($spec.Name)-$budget"
    $logical = "inputs/$($spec.Name).pdf"
    $managed = Invoke-Managed $tag @('--global', "--parser-max-errors=$budget", '--', $logical, '--show-object=5') @()
    $oracleArgs = @('--global', "--parser-max-errors=$budget", '--', (Join-Path $runRoot $logical), '--show-object=5')
    $oracleLines = @(& $QpdfExe @oracleArgs 2> (Join-Path $runRoot "$tag-oracle-stderr.log"))
    $oracleExit = $LASTEXITCODE
    $oracleValue = ($oracleLines -join "`n").Trim()
    $oracleLines | Set-Content -LiteralPath (Join-Path $runRoot "$tag-oracle-stdout.log") -Encoding utf8NoBOM
    $observations.Add(@{ Case = $spec.Name; Budget = $budget; Managed = $managed; OracleExit = $oracleExit; OracleValue = $oracleValue })
    if ($managed.Exit -ne 3 -or $oracleExit -ne 3 -or $managed.Value -cne $oracleValue) {
      throw "Parser values or warning exits differ ($tag): managed='$($managed.Value)', oracle='$oracleValue'. See $runRoot."
    }
  }
  Write-Output "[parser/$($spec.Name)] six budget values and warning exits agree"
}

foreach ($inputName in @('word', 'broken-word')) {
  foreach ($budget in @(0, 1, 2)) {
    $tag = "rewrite-$inputName-$budget"
    $managedFile = Join-Path $runRoot "$tag-managed.pdf"
    $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
    $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", "--parser-max-errors=$budget",
      '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
    & $QpdfExe '--global' "--parser-max-errors=$budget" '--' (Join-Path $runRoot "inputs/$inputName.pdf") '--pages' '.' '1' '--' $oracleFile 2> (Join-Path $runRoot "$tag-oracle-stderr.log")
    if ($LASTEXITCODE -ne 3 -or $managed.Exit -ne 3) { throw "Parser rewrite exits differ ($tag)." }
    foreach ($file in @($managedFile, $oracleFile)) {
      & $QpdfExe '--check' $file *> (Join-Path $runRoot "$tag-$([IO.Path]::GetFileName($file))-check.log")
      if ($LASTEXITCODE -ne 0) { throw "Repaired output fails oracle check ($file)." }
      $validation = @(& dotnet $ProbeDll 'validate' '--input' $file 2>&1)
      if ($LASTEXITCODE -ne 0 -or -not (($validation -join "`n") -match 'independent: npages=1')) {
        throw "Independent reader rejected repaired output ($file)."
      }
      $json = @(& $QpdfExe '--json' '--json-key=qpdf' $file 2> (Join-Path $runRoot "$tag-json-stderr.log")) -join "`n"
      if ($LASTEXITCODE -ne 0) { throw "Repaired output JSON fails ($file)." }
      if ($json.Contains('u:@') -ne ($budget -ne 1 -and $inputName -eq 'word')) { throw "Repaired/null object value lost during rewrite ($file)." }
    }
    Write-Output "[parser/$tag] repaired values survive rewrite; oracle check and independent page counts pass"
  }
}

@{
  OracleSHA256 = (Get-FileHash -LiteralPath $QpdfExe -Algorithm SHA256).Hash
  ProbeSHA256 = (Get-FileHash -LiteralPath $ProbeDll -Algorithm SHA256).Hash
  LibrarySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $ProbeDll) 'Lokad.Qpdf.dll') -Algorithm SHA256).Hash
  Values = $observations
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8NoBOM
Write-Output "Parser lane passed: 102 exact object comparisons and six rewrite trios. Evidence: $runRoot"
