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
$rewriteObservations = [Collections.Generic.List[object]]::new()

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
      $bytes.Write([Text.Encoding]::Latin1.GetBytes("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n"))
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
  param([string] $Tag, [string[]] $Arguments, [string[]] $Mappings, [string] $JobFile)
  $probeArgs = if ($JobFile) { @($ProbeDll, 'emit-job', '--workdir', $runRoot, '--job', $JobFile) }
    else { @($ProbeDll, 'emit', '--workdir', $runRoot) }
  foreach ($mapping in $Mappings) { $probeArgs += @('--out', $mapping) }
  if (-not $JobFile) { $probeArgs += @('--') + $Arguments }
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

function Invoke-Oracle {
  param([string] $Tag, [string[]] $Arguments)
  $start = [Diagnostics.ProcessStartInfo]::new($QpdfExe)
  $start.UseShellExecute = $false
  $start.CreateNoWindow = $true
  $start.RedirectStandardOutput = $true
  $start.RedirectStandardError = $true
  foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
  $process = [Diagnostics.Process]::new()
  $process.StartInfo = $start
  $stdout = [IO.MemoryStream]::new()
  $stderr = [IO.MemoryStream]::new()
  try {
    if (-not $process.Start()) { throw 'Oracle process did not start.' }
    $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
    $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
    if (-not $process.WaitForExit(30000)) { throw "Oracle process still live: PID $($process.Id). See $runRoot." }
    $null = $outTask.GetAwaiter().GetResult()
    $null = $errTask.GetAwaiter().GetResult()
    $outBytes = $stdout.ToArray()
    $errBytes = $stderr.ToArray()
    [IO.File]::WriteAllBytes((Join-Path $runRoot "$Tag-stdout.bin"), $outBytes)
    [IO.File]::WriteAllBytes((Join-Path $runRoot "$Tag-stderr.bin"), $errBytes)
    return @{
      Exit = $process.ExitCode
      Value = [Text.Encoding]::Latin1.GetString($outBytes).TrimEnd([char[]]@([char]13,[char]10))
      StdoutBase64 = [Convert]::ToBase64String($outBytes)
      StderrBase64 = [Convert]::ToBase64String($errBytes)
    }
  }
  finally { $stdout.Dispose(); $stderr.Dispose(); $process.Dispose() }
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

$numericSpecs = @(
  @{ Name = 'min-integer'; Body = '[ -9223372036854775808 2 ]'; Clean = $true },
  @{ Name = 'max-integer'; Body = '[ 9223372036854775807 2 ]'; Clean = $true },
  @{ Name = 'canonical-integers'; Body = '[ +001 -00 0001 ]'; Clean = $true },
  @{ Name = 'long-integer'; Body = ('[ ' + ('0' * 2048) + '1 2 ]'); Clean = $true },
  @{ Name = 'positive-overflow'; Body = '[ 9223372036854775808 2 ]' },
  @{ Name = 'negative-overflow'; Body = '[ -9223372036854775809 2 ]' },
  @{ Name = 'root-overflow'; Body = '9223372036854775808' },
  @{ Name = 'long-overflow'; Body = ('[ ' + ('9' * 2048) + ' 2 ]') },
  @{ Name = 'invalid-reference-number'; Body = '[ 2147483648 0 R 2 ]' },
  @{ Name = 'number-int-underflow'; Body = '[ -2147483649 0 R 2 ]' },
  @{ Name = 'generation-int-overflow'; Body = '[ 1 2147483648 R 2 ]' },
  @{ Name = 'generation-long-overflow'; Body = '[ 1 9223372036854775808 R 2 ]' },
  @{ Name = 'invalid-reference-generation'; Body = '[ 1 65536 R 2 ]' },
  @{ Name = 'generation-65535'; Body = '[ 1 65535 R 2 ]' },
  @{ Name = 'negative-generation'; Body = '[ 1 -1 R 2 ]' },
  @{ Name = 'negative-reference'; Body = '[ -1 0 R 2 ]' },
  @{ Name = 'zero-reference'; Body = '[ 0 0 R 2 ]' }
)
foreach ($spec in $numericSpecs) {
  $specs += $spec
  $specs += @{ Name = $spec.Name + '-broken'; Body = $spec.Body; Broken = $true }
}

$midpointOne = '1.00000000000000011102230246251565404236316680908203125'
$tinyDigits = [Numerics.BigInteger]::Pow(5, 1075).ToString([Globalization.CultureInfo]::InvariantCulture)
$tinyMidpoint = '0.' + ('0' * (1075 - $tinyDigits.Length)) + $tinyDigits
$realSpecs = @(
  @{ Name = 'long-real'; Text = '0.' + ('0' * 2048) + '1' },
  @{ Name = 'padded-real'; Text = ('0' * 2048) + '0.5' },
  @{ Name = 'negative-real-zero'; Text = '-' + ('0' * 2048) + '.0000' },
  @{ Name = 'long-real-digits'; Text = '0.' + ('1234567890' * 256) },
  @{ Name = 'large-real'; Text = ('9' * 400) + '.0' },
  @{ Name = 'real-midpoint'; Text = $midpointOne + ('0' * 2048) },
  @{ Name = 'real-midpoint-above'; Text = $midpointOne + ('0' * 2048) + '1' },
  @{ Name = 'real-underflow-midpoint'; Text = $tinyMidpoint + ('0' * 2048) }
)
foreach ($spec in $realSpecs) {
  $specs += @{ Name = $spec.Name; Body = '[ ' + $spec.Text + ' 2 ]'; Clean = $true }
  $specs += @{ Name = $spec.Name + '-broken'; Body = '[ ' + $spec.Text + ' 2 ]'; Broken = $true }
}

$lexicalSpecs = @(
  @{ Name = 'brace-word'; Body = '[ 1{2 3}4 ]' },
  @{ Name = 'brace-keyword'; Body = '[ true{false}null ]' },
  @{ Name = 'brace-name'; Body = '[ /A{2}/B ]' },
  @{ Name = 'name-null'; Body = '[ /A#00B 2 ]' },
  @{ Name = 'name-null-root'; Body = '/A#00B' },
  @{ Name = 'name-null-key'; Body = '<< /A#00B 1 /C 2 >>' },
  @{ Name = 'orphan-recovery'; Body = '<< 5 /A >>' },
  @{ Name = 'orphan-collision-recovery'; Body = '<< 5 /QPDFFake1 3 /A 1 >>' },
  @{ Name = 'name-escaped-hash'; Body = '[ /A#2300B 2 ]'; Clean = $true },
  @{ Name = 'name-escaped-braces'; Body = '[ /A#7BB#7DC 2 ]'; Clean = $true },
  @{ Name = 'hex-display'; Body = '[ <ABCD> <ff00> ]'; Clean = $true },
  @{ Name = 'literal-newlines'; Body = "[ (a`r`nb`rc`nd) ]"; Clean = $true },
  @{ Name = 'literal-lfcr'; Body = "[ (a`n`rb) ]"; Clean = $true },
  @{ Name = 'literal-continuation'; Body = "[ (a\`r`nb\`rc\`nd) ]"; Clean = $true },
  @{ Name = 'literal-escaped-cr'; Body = '[ (a\rb\n) ]'; Clean = $true }
)
foreach ($spec in $lexicalSpecs) {
  $specs += $spec
  $specs += @{ Name = $spec.Name + '-broken'; Body = $spec.Body; Broken = $true }
}

$dictionarySpecs = @(
  @{ Name = 'array-orphan-gap'; Body = '<< [1 2] 3 @ /A 4 >>' },
  @{ Name = 'duplicate-integer-next-array'; Body = '<< @ /A 1 /A 2 [ @ ] >>' },
  @{ Name = 'duplicate-integer-next-error'; Body = '<< @ /A 1 /A 2 @ >>' },
  @{ Name = 'duplicate-integer-next-dictionary'; Body = '<< @ /A 1 /A 2 << /B @ >> >>' },
  @{ Name = 'error-then-duplicate'; Body = '<< @ /A 1 /A 2 >>' },
  @{ Name = 'duplicate-reference-value'; Body = '<< @ /A 1 /A 2 0 R >>' },
  @{ Name = 'duplicate-integer-delayed-error'; Body = '<< @ /A 1 /A 2 3 @ >>' },
  @{ Name = 'duplicate-integer-orphan'; Body = '<< @ /A 1 /A 2 3 >>' },
  @{ Name = 'duplicate-integer-orphans'; Body = '<< @ /A 1 /A 2 3 4 >>' },
  @{ Name = 'duplicate-integer-next-reference'; Body = '<< @ /A 1 /A 2 3 4 R >>' },
  @{ Name = 'duplicate-real-close'; Body = '<< @ /A 1 /A 2.0 >>' },
  @{ Name = 'duplicate-null-value'; Body = '<< @ /A 1 /A null >>' },
  @{ Name = 'duplicate-gap-below'; Body = '<< @ /A 1 /B @ /A 2 3 >>' },
  @{ Name = 'duplicate-gap-at'; Body = '<< @ /A 1 /B @ /A 2 3 4 >>' },
  @{ Name = 'duplicate-gap-reference-tail'; Body = '<< @ /A 1 /B @ /A 2 3 4 R @ >>' },
  @{ Name = 'spaced-orphan-errors'; Body = '<< @ /A 1 /B 2 @ /C 3 /D 4 @ /E 5 >>' },
  @{ Name = 'error-before-orphans'; Body = '<< @ 1 2 /A 3 >>' },
  @{ Name = 'child-orphan-guard'; Body = '<< /A << 1 2 @ /B 3 >> /C 4 >>' },
  @{ Name = 'fake-key-collision-error'; Body = '<< /QPDFFake1 9 1 2 @ /A 3 >>' },
  @{ Name = 'one-orphan-error'; Body = '<< 1 @ /A 3 >>' },
  @{ Name = 'orphan-error-storm'; Body = '<< 1 2 @ @ @ @ @ @ /A 3 >>' },
  @{ Name = 'orphan-late-error'; Body = '<< 1 2 @ /A 3 >>' },
  @{ Name = 'array-then-key-error'; Body = '<< 1 2 /A [ @ ] @ /B 4 >>' },
  @{ Name = 'array-then-value-error'; Body = '<< 1 2 /A [ @ ] /B @ >>' },
  @{ Name = 'orphan-value-gap'; Body = '<< 1 2 /A @ >>' },
  @{ Name = 'child-dictionary-isolation'; Body = '<< 1 2 /A << /B @ /C @ >> >>' },
  @{ Name = 'dictionary-then-key-error'; Body = '<< 1 2 /A << /B @ >> @ /C 4 >>' },
  @{ Name = 'nested-guard-and-parent'; Body = '<< 1 2 /A << 1 @ /B 3 >> /C @ >>' },
  @{ Name = 'orphan-duplicates'; Body = '<< 1 2 /A 3 /A 4 /A 5 >>' },
  @{ Name = 'value-error-duplicate'; Body = '<< 1 2 /A 3 /B @ /B 4 >>' },
  @{ Name = 'nul-key-after-orphans'; Body = '<< 1 2 /A#00B 3 /C 4 >>' },
  @{ Name = 'fake-key-collision-duplicate'; Body = '<< 1 2 /QPDFFake1 9 /A 3 /A 4 >>' },
  @{ Name = 'invalid-reference-followup'; Body = '<< 1 2 0 0 R @ /A 3 >>' },
  @{ Name = 'invalid-reference-gap'; Body = '<< 1 2 0 0 R /A 3 >>' },
  @{ Name = 'orphan-good-gap'; Body = '<< 1 2 3 @ /A 4 >>' },
  @{ Name = 'null-orphan-error'; Body = '<< null null @ /A 3 >>' },
  @{ Name = 'boolean-orphan-error'; Body = '<< true false @ /A 3 >>' }
)
foreach ($spec in $dictionarySpecs) {
  $spec.Budgets = @(0, 1, 2, 3, 4, 5, 6, 15)
  $specs += $spec
  $specs += @{ Name = $spec.Name + '-broken'; Body = $spec.Body; Broken = $true; Budgets = $spec.Budgets }
}

$damagedSpecs = @(
  @{ Name = 'damage-array-backslash-eof'; Body = '[ (abc\'; Clean = $false },
  @{ Name = 'damage-array-dict-close'; Body = '[ << /A 1 ] 2 >> 3 ]'; Clean = $false },
  @{ Name = 'damage-array-endobj'; Body = '[ 1 endobj 2 ]'; Clean = $false },
  @{ Name = 'damage-array-endstream'; Body = '[ 1 endstream 2 ]'; Clean = $false },
  @{ Name = 'damage-array-eof'; Body = '[ 1 2'; Clean = $false },
  @{ Name = 'damage-array-hex-eof'; Body = '[ <ABC'; Clean = $false },
  @{ Name = 'damage-array-literal-eof'; Body = '[ (abc'; Clean = $false },
  @{ Name = 'damage-array-paren'; Body = '[ 1 ) 2 ]'; Clean = $false },
  @{ Name = 'damage-array-stream'; Body = '[ 1 stream 2 ]'; Clean = $false },
  @{ Name = 'damage-dict-array-close'; Body = '<< /A [ 1 >> /B 2 ] /C 3 >>'; Clean = $false },
  @{ Name = 'damage-dict-eof'; Body = '<< /A 1'; Clean = $false },
  @{ Name = 'damage-dict-hex-eof'; Body = '<< /A <ABC'; Clean = $false },
  @{ Name = 'damage-dict-key-close'; Body = '<< /A 1 ] /B 2 >>'; Clean = $false },
  @{ Name = 'damage-dict-literal-eof'; Body = '<< /A (abc'; Clean = $false },
  @{ Name = 'damage-dict-name-short'; Body = '<< /A#2 1 /B 2 >>'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-dot-reals'; Body = '[ . +. -. ]'; Clean = $false },
  @{ Name = 'damage-empty-name'; Body = '/'; Clean = $true },
  @{ Name = 'damage-hex-first'; Body = '[ <GABC> 2 ]'; Clean = $false },
  @{ Name = 'damage-hex-tail'; Body = '[ <A G 1> 2 ]'; Clean = $false },
  @{ Name = 'damage-invalid-numbers'; Body = '[ 1..2 1.2.3 1e3 .5x 1-2 ++1 --1 +-1 ]'; Clean = $false },
  @{ Name = 'damage-literal-byte-range'; Body = '[ (\000\001\037\177\200\377) ]'; Clean = $true },
  @{ Name = 'damage-literal-escapes'; Body = '[ (a\q\8\777\12z\(b\)) ]'; Clean = $true },
  @{ Name = 'damage-literal-nesting'; Body = '[ (a(b)c) (a\)b) (a\\b) ]'; Clean = $true },
  @{ Name = 'damage-literal-raw-high'; Body = [Text.Encoding]::Latin1.GetString([Convert]::FromBase64String('WyAoYYD/YikgXQ==')); Clean = $true },
  @{ Name = 'damage-name-escaped-high'; Body = '[ /A#80#FF ]'; Clean = $true },
  @{ Name = 'damage-name-identity'; Body = '<< /A#2 1 /A#232 2 >>'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-name-incomplete'; Body = '[ /A#2 /B# /C#GG 2 ]'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-name-multiple-hashes'; Body = '/A#Q#G'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-name-raw-high'; Body = [Text.Encoding]::Latin1.GetString([Convert]::FromBase64String('WyAvQYD/IF0=')); Clean = $true },
  @{ Name = 'damage-octal-widths'; Body = '[ (\7\11\112\1123\777\400) ]'; Clean = $true },
  @{ Name = 'damage-odd-hex'; Body = '[ <A> <ABC> <0> ]'; Clean = $true },
  @{ Name = 'damage-root-array-close'; Body = ']'; Clean = $false },
  @{ Name = 'damage-root-backslash-eof'; Body = '(abc\'; Clean = $false },
  @{ Name = 'damage-root-dict-close'; Body = '>>'; Clean = $false },
  @{ Name = 'damage-root-dot'; Body = '.'; Clean = $false },
  @{ Name = 'damage-root-hex-eof'; Body = '<ABC'; Clean = $false },
  @{ Name = 'damage-root-hex-first'; Body = '<GABC>'; Clean = $false },
  @{ Name = 'damage-root-hex-tail'; Body = '<A G 1>'; Clean = $false },
  @{ Name = 'damage-root-literal-eof'; Body = '(abc'; Clean = $false },
  @{ Name = 'damage-root-minus'; Body = '-'; Clean = $false },
  @{ Name = 'damage-root-minus-dot'; Body = '-.'; Clean = $false },
  @{ Name = 'damage-root-name-hash'; Body = '/A#'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-root-name-invalid'; Body = '/A#GG'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-root-name-short'; Body = '/A#2'; Clean = $false; MalformedName = $true },
  @{ Name = 'damage-root-paren'; Body = ')'; Clean = $false },
  @{ Name = 'damage-root-plus'; Body = '+'; Clean = $false },
  @{ Name = 'damage-root-plus-dot'; Body = '+.'; Clean = $false },
  @{ Name = 'damage-signs'; Body = '[ + - . +. -. .. ]'; Clean = $false },
  @{ Name = 'display-del-byte'; Body = '[ (\177abcd) ]'; Clean = $true },
  @{ Name = 'display-empty-hex'; Body = '[ <> ]'; Clean = $true },
  @{ Name = 'display-escaped-controls'; Body = '[ (\010\011\012\014\015) ]'; Clean = $true },
  @{ Name = 'display-hard-control'; Body = '[ (\007abcdefghij) ]'; Clean = $true },
  @{ Name = 'display-low-high-byte'; Body = '[ (\030abcd) ]'; Clean = $true },
  @{ Name = 'display-one-fifth'; Body = '[ (\200abcd) ]'; Clean = $true },
  @{ Name = 'display-over-one-fifth'; Body = '[ (\200abc) ]'; Clean = $true },
  @{ Name = 'display-printable-hex'; Body = '[ <414243> ]'; Clean = $true }
)
foreach ($spec in $damagedSpecs) {
  $specs += $spec
  $specs += @{ Name = $spec.Name + '-broken'; Body = $spec.Body; Clean = $spec.Clean; MalformedName = $spec.MalformedName; Broken = $true }
}

foreach ($spec in $specs) {
  Write-Input $spec.Name $spec.Body ([bool]$spec.Broken)
  $budgets = if ($spec.Budgets) { $spec.Budgets } else { @(0, 1, 2, 3, 6, 15) }
  foreach ($budget in $budgets) {
    $tag = "$($spec.Name)-$budget"
    $logical = "inputs/$($spec.Name).pdf"
    $managed = Invoke-Managed $tag @('--global', "--parser-max-errors=$budget", '--', $logical, '--show-object=5') @()
    $oracleArgs = @('--global', "--parser-max-errors=$budget", '--', (Join-Path $runRoot $logical), '--show-object=5')
    $oracle = Invoke-Oracle "$tag-oracle" $oracleArgs
    $oracleExit = $oracle.Exit
    $oracleValue = $oracle.Value
    $observations.Add(@{ Case = $spec.Name; Budget = $budget; Managed = $managed; OracleExit = $oracleExit; OracleValue = $oracleValue; OracleStdoutBase64 = $oracle.StdoutBase64; OracleStderrBase64 = $oracle.StderrBase64 })
    $expectedExit = if ($spec.Clean -and -not $spec.Broken) { 0 } else { 3 }
    if ($managed.Exit -ne $expectedExit -or $oracleExit -ne $expectedExit -or $managed.Value -cne $oracleValue) {
      throw "Parser values or warning exits differ ($tag): managed='$($managed.Value)', oracle='$oracleValue'. See $runRoot."
    }
  }
  Write-Output "[parser/$($spec.Name)] $($budgets.Count) budget values and exits agree"
}

function Read-RewrittenJson {
  param([string] $Tag, [string] $File, [int] $ExpectedExit)
  & $QpdfExe '--check' $File *> (Join-Path $runRoot "$Tag-$([IO.Path]::GetFileName($File))-check.log")
  if ($LASTEXITCODE -ne $ExpectedExit) { throw "Repaired output oracle check exit differs ($File)." }
  $validation = @(& dotnet $ProbeDll 'validate' '--input' $File 2>&1)
  if ($LASTEXITCODE -ne 0 -or -not (($validation -join "`n") -match 'independent: npages=1')) {
    throw "Independent reader rejected repaired output ($File)."
  }
  $json = @(& $QpdfExe '--json' '--json-key=qpdf' $File 2> (Join-Path $runRoot "$Tag-json-stderr.log")) -join "`n"
  if ($LASTEXITCODE -ne $ExpectedExit) { throw "Repaired output JSON exit differs ($File)." }
  return $json
}

function Read-RewrittenAudit {
  param([string] $Tag, [string] $File, [int] $ExpectedExit)
  $fileTag = $Tag + '-' + [IO.Path]::GetFileNameWithoutExtension($File)
  $null = Read-RewrittenJson $fileTag $File $ExpectedExit
  $pageLines = @(& $QpdfExe '--show-pages' $File 2> (Join-Path $runRoot "$Tag-page-stderr.log")) -join "`n"
  $page = [regex]::Match($pageLines, '(?m)^page 1: (\d+) (\d+) R\s*$')
  if ($LASTEXITCODE -ne 0 -or -not $page.Success) { throw "Rewrite page cannot be located ($Tag)." }
  $pageId = $page.Groups[1].Value + ',' + $page.Groups[2].Value
  $pageValue = @(& $QpdfExe "--show-object=$pageId" $File 2> (Join-Path $runRoot "$Tag-page-stderr.log")) -join "`n"
  if ($LASTEXITCODE -ne $ExpectedExit) { throw "Rewrite page value exit differs ($Tag)." }
  if ($pageValue -notmatch '/Audit\b' -or $pageValue -match '/Audit null(?:\s|>)') { return 'null' }
  $audit = [regex]::Match($pageValue, '/Audit (\d+) (\d+) R')
  if (-not $audit.Success) { throw "Rewrite audit value cannot be located ($Tag)." }
  $auditId = $audit.Groups[1].Value + ',' + $audit.Groups[2].Value
  $actual = Invoke-Oracle "$fileTag-audit" @("--show-object=$auditId", $File)
  if ($actual.Exit -ne $ExpectedExit) { throw "Rewrite audit exit differs ($Tag)." }
  return $actual.Value
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
      $json = Read-RewrittenJson $tag $file 0
      if ($json.Contains('u:@') -ne ($budget -ne 1 -and $inputName -eq 'word')) { throw "Repaired/null object value lost during rewrite ($file)." }
    }
    $rewriteObservations.Add(@{ Case = $inputName; Budget = $budget; ManagedExit = $managed.Exit; OracleExit = 3 })
    Write-Output "[parser/$tag] repaired values survive rewrite; oracle check and independent page counts pass"
  }
}

$numericRewrites = @(
  @{ Name = 'min-integer'; Json = '[-9223372036854775808,2]'; Clean = $true },
  @{ Name = 'long-integer'; Json = '[1,2]'; Clean = $true },
  @{ Name = 'positive-overflow'; Json = 'null' },
  @{ Name = 'negative-overflow'; Json = 'null' },
  @{ Name = 'invalid-reference-number'; Json = 'null' },
  @{ Name = 'invalid-reference-generation'; Json = '[null,2]' }
)
foreach ($spec in $numericRewrites) {
  foreach ($broken in @($false, $true)) {
    $inputName = $spec.Name + $(if ($broken) { '-broken' } else { '' })
    $tag = "rewrite-$inputName-2"
    $managedFile = Join-Path $runRoot "$tag-managed.pdf"
    $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
    $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", '--parser-max-errors=2',
      '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
    & $QpdfExe '--global' '--parser-max-errors=2' '--' (Join-Path $runRoot "inputs/$inputName.pdf") '--pages' '.' '1' '--' $oracleFile 2> (Join-Path $runRoot "$tag-oracle-stderr.log")
    $oracleExit = $LASTEXITCODE
    $expectedExit = if ($spec.Clean -and -not $broken) { 0 } else { 3 }
    if ($managed.Exit -ne $expectedExit -or $oracleExit -ne $expectedExit) { throw "Numeric rewrite exits differ ($tag)." }
    foreach ($file in @($managedFile, $oracleFile)) {
      $json = Read-RewrittenJson $tag $file 0 | ConvertFrom-Json
      $objects = $json.qpdf[1]
      $pages = @($objects.PSObject.Properties | Where-Object { $_.Value.value.'/Type' -eq '/Page' })
      if ($pages.Count -ne 1) { throw "Numeric rewrite has an unexpected page graph ($file)." }
      $audit = $pages[0].Value.value.'/Audit'
      if ($audit -is [string] -and $audit -match '^\d+ \d+ R$') { $audit = $objects.('obj:' + $audit).value }
      $actual = ConvertTo-Json -InputObject $audit -Compress -Depth 4
      if ($actual -cne $spec.Json) { throw "Numeric rewrite value differs ($tag): '$actual' expected '$($spec.Json)'." }
    }
    $rewriteObservations.Add(@{ Case = $inputName; Budget = 2; ManagedExit = $managed.Exit; OracleExit = $oracleExit; Audit = $spec.Json })
    Write-Output "[parser/$tag] numeric values survive rewrite; oracle check and independent page counts pass"
  }
}

foreach ($spec in $realSpecs) {
  foreach ($broken in @($false, $true)) {
    $inputName = $spec.Name + $(if ($broken) { '-broken' } else { '' })
    $tag = "rewrite-$inputName-2"
    $managedFile = Join-Path $runRoot "$tag-managed.pdf"
    $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
    $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", '--parser-max-errors=2',
      '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
    & $QpdfExe '--global' '--parser-max-errors=2' '--' (Join-Path $runRoot "inputs/$inputName.pdf") '--pages' '.' '1' '--' $oracleFile 2> (Join-Path $runRoot "$tag-oracle-stderr.log")
    $oracleExit = $LASTEXITCODE
    $expectedExit = if ($broken) { 3 } else { 0 }
    if ($managed.Exit -ne $expectedExit -or $oracleExit -ne $expectedExit) { throw "Real rewrite exits differ ($tag)." }
    foreach ($file in @($managedFile, $oracleFile)) {
      $actual = Read-RewrittenAudit $tag $file 0
      if ($actual -cne ('[ ' + $spec.Text + ' 2 ]')) { throw "Real text changed during rewrite ($tag)." }
    }
    $rewriteObservations.Add(@{ Case = $inputName; Budget = 2; ManagedExit = $managed.Exit; OracleExit = $oracleExit; RealText = $spec.Text })
    Write-Output "[parser/$tag] exact real text survives rewrite; oracle check and independent page counts pass"
  }
}

foreach ($spec in $lexicalSpecs) {
  foreach ($broken in @($false, $true)) {
    $inputName = $spec.Name + $(if ($broken) { '-broken' } else { '' })
    $tag = "rewrite-$inputName-3"
    $managedFile = Join-Path $runRoot "$tag-managed.pdf"
    $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
    $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", '--parser-max-errors=3',
      '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
    & $QpdfExe '--global' '--parser-max-errors=3' '--' (Join-Path $runRoot "inputs/$inputName.pdf") '--pages' '.' '1' '--' $oracleFile 2> (Join-Path $runRoot "$tag-oracle-stderr.log")
    $oracleExit = $LASTEXITCODE
    $expectedExit = if ($spec.Clean -and -not $broken) { 0 } else { 3 }
    if ($managed.Exit -ne $expectedExit -or $oracleExit -ne $expectedExit) { throw "Lexical rewrite exits differ ($tag)." }
    $expectedValue = @($observations | Where-Object { $_.Case -eq $inputName -and $_.Budget -eq 3 })[0].OracleValue
    foreach ($file in @($managedFile, $oracleFile)) {
      $actual = Read-RewrittenAudit $tag $file 0
      if ($actual -cne $expectedValue) { throw "Lexical value changed during rewrite ($tag)." }
    }
    $rewriteObservations.Add(@{ Case = $inputName; Budget = 3; ManagedExit = $managed.Exit; OracleExit = $oracleExit; Value = $expectedValue })
    Write-Output "[parser/$tag] lexical values survive rewrite; oracle check and independent page counts pass"
  }
}

foreach ($spec in $dictionarySpecs) {
  foreach ($broken in @($false, $true)) {
    # Inspection covers this reference value; page-tree/catalog imports are
    # rejected by the documented contained-copy profile (offline negative test).
    if ($spec.Name -eq 'duplicate-reference-value') { continue }
    $inputName = $spec.Name + $(if ($broken) { '-broken' } else { '' })
    foreach ($budget in @(0, 5)) {
      $tag = "rewrite-$inputName-$budget"
      $managedFile = Join-Path $runRoot "$tag-managed.pdf"
      $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
      $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", "--parser-max-errors=$budget",
        '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
      & $QpdfExe '--global' "--parser-max-errors=$budget" '--' (Join-Path $runRoot "inputs/$inputName.pdf") '--pages' '.' '1' '--' $oracleFile 2> (Join-Path $runRoot "$tag-oracle-stderr.log")
      $oracleExit = $LASTEXITCODE
      if ($managed.Exit -ne 3 -or $oracleExit -ne 3) { throw "Dictionary rewrite exits differ ($tag)." }
      $expectedValue = @($observations | Where-Object { $_.Case -eq $inputName -and $_.Budget -eq $budget })[0].OracleValue
      foreach ($file in @($managedFile, $oracleFile)) {
        $actual = Read-RewrittenAudit $tag $file 0
        if ($actual -cne $expectedValue) { throw "Dictionary value changed during rewrite ($tag)." }
      }
      $rewriteObservations.Add(@{ Case = $inputName; Budget = $budget; ManagedExit = $managed.Exit; OracleExit = $oracleExit; Value = $expectedValue })
      Write-Output "[parser/$tag] dictionary guards survive rewrite; oracle check and independent page counts pass"
    }
  }
}

foreach ($spec in $damagedSpecs) {
  foreach ($broken in @($false, $true)) {
    $inputName = $spec.Name + $(if ($broken) { '-broken' } else { '' })
    foreach ($budget in @(0, 6)) {
      $tag = "rewrite-$inputName-$budget"
      $managedFile = Join-Path $runRoot "$tag-managed.pdf"
      $jobFile = Join-Path $runRoot "$tag-job.pdf"
      $oracleFile = Join-Path $runRoot "$tag-oracle.pdf"
      $managed = Invoke-Managed $tag @("inputs/$inputName.pdf", "--parser-max-errors=$budget",
        '--pages', '.', '1', '--', 'outputs/a.pdf') @("outputs/a.pdf=$managedFile")
      $job = @{
        inputFile = "inputs/$inputName.pdf"; outputFile = 'outputs/a.pdf'
        pages = @(@{ file = '.'; range = '1' }); global = @{ parserMaxErrors = $budget }
      } | ConvertTo-Json -Depth 4
      [IO.File]::WriteAllText((Join-Path $runRoot "$tag-job.json"), $job, [Text.UTF8Encoding]::new($false))
      $managedJob = Invoke-Managed "$tag-job" @() @("outputs/a.pdf=$jobFile") (Join-Path $runRoot "$tag-job.json")
      $oracle = Invoke-Oracle "$tag-oracle-rewrite" @('--global', "--parser-max-errors=$budget", '--',
        (Join-Path $runRoot "inputs/$inputName.pdf"), '--pages', '.', '1', '--', $oracleFile)
      $expectedExit = if ($spec.Clean -and -not $broken) { 0 } else { 3 }
      if ($managed.Exit -ne $expectedExit -or $managedJob.Exit -ne $expectedExit -or $oracle.Exit -ne $expectedExit) {
        throw "Damaged-value rewrite exits differ ($tag)."
      }
      if ((Get-FileHash -LiteralPath $managedFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $jobFile -Algorithm SHA256).Hash) {
        throw "Damaged-value argv/job bytes differ ($tag)."
      }
      $expectedValue = @($observations | Where-Object { $_.Case -eq $inputName -and $_.Budget -eq $budget })[0].OracleValue
      $checkExit = if ($spec.MalformedName) { 3 } else { 0 }
      foreach ($file in @($managedFile, $jobFile, $oracleFile)) {
        $actual = Read-RewrittenAudit $tag $file $checkExit
        if ($actual -cne $expectedValue) { throw "Damaged value changed during rewrite ($tag): '$actual' expected '$expectedValue'." }
      }
      $rewriteObservations.Add(@{ Case = $inputName; Budget = $budget; ManagedExit = $managed.Exit; JobExit = $managedJob.Exit; OracleExit = $oracle.Exit; ReopenedOracleExit = $checkExit; Value = $expectedValue })
      Write-Output "[parser/$tag] exact values and argv/job bytes survive rewrite; oracle exit $checkExit and independent page counts pass"
    }
  }
}
@{
  OracleSHA256 = (Get-FileHash -LiteralPath $QpdfExe -Algorithm SHA256).Hash
  ProbeSHA256 = (Get-FileHash -LiteralPath $ProbeDll -Algorithm SHA256).Hash
  LibrarySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $ProbeDll) 'Lokad.Qpdf.dll') -Algorithm SHA256).Hash
  Values = $observations
  Rewrites = $rewriteObservations
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8NoBOM
Write-Output "Parser lane passed: $($observations.Count) exact object comparisons and $($rewriteObservations.Count) rewrite trios. Evidence: $runRoot"
