<#
.SYNOPSIS
  Differential lane: run the managed library and the pinned oracle on the
  same operations, then validate both outputs with the oracle and the
  independent reader. Fails explicitly on any mismatch; never silently skips.
  All generated files stay under external/diff (git-ignored).
#>
param(
  [Parameter(Mandatory = $false)]
  [string] $QpdfExe = $env:QPDF_EXE,
  [Parameter(Mandatory = $false)]
  [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($QpdfExe)) {
  throw "QPDF_EXE is not set. Supply -QpdfExe <path> or set QPDF_EXE to the pinned qpdf 12.4.2 executable."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$probeDll = Join-Path $repoRoot "tools/QpdfProbe/bin/$Configuration/net10.0/QpdfProbe.dll"
if (-not (Test-Path $probeDll)) {
  throw "QpdfProbe.dll not found. Run tools/run-reference-checks.ps1 first (it builds the probe)."
}

$diffRoot = Join-Path $repoRoot 'external/diff'
New-Item -ItemType Directory -Path $diffRoot -Force | Out-Null
$fixtureDir = Join-Path $repoRoot 'tests/Fixtures'
$script:failures = 0

function Invoke-Oracle {
  param([string]$Workdir, [string[]]$OracleArgs)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList $OracleArgs -WorkingDirectory $Workdir -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$Workdir/ora-stdout.txt" -RedirectStandardError "$Workdir/ora-stderr.txt"
  return $p.ExitCode
}

function Invoke-Emit {
  param([string]$Workdir, [string[]]$EmitArgs)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll) + $EmitArgs) -WorkingDirectory $Workdir -NoNewWindow -Wait -PassThru
  return $p.ExitCode
}

function Get-OracleNpages {
  param([string]$File)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-npages', $File) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qnp.txt"
  if ($p.ExitCode -ne 0) { throw "oracle --show-npages failed for $File" }
  return [int]((Get-Content "$env:TEMP/qnp.txt" -Raw).Trim())
}

function Get-PigNpages {
  param([string]$File)
  $out = & dotnet $probeDll validate --input $File 2>&1 | Out-String
  $m = [regex]::Match($out, 'independent: npages=(\d+)')
  if (-not $m.Success) { throw "independent validate failed for $File : $out" }
  return [int]$m.Groups[1].Value
}

function Assert-Trio {
  param([string]$Case, [string]$LibFile, [string]$OraFile, [int]$ExpectedNpages)
  foreach ($f in @($LibFile, $OraFile)) {
    $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--check', $f) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qc.txt" -RedirectStandardError "$env:TEMP/qce.txt"
    if ($p.ExitCode -ne 0) { Write-Output "[$Case] FAIL: oracle --check exit $($p.ExitCode) for $f"; $script:failures++; return }
  }
  $libN = Get-OracleNpages $LibFile
  $oraN = Get-OracleNpages $OraFile
  $libP = Get-PigNpages $LibFile
  $oraP = Get-PigNpages $OraFile
  if ($libN -ne $ExpectedNpages -or $oraN -ne $ExpectedNpages -or $libP -ne $ExpectedNpages -or $oraP -ne $ExpectedNpages) {
    Write-Output "[$Case] FAIL: page counts lib=$libN/$libP ora=$oraN/$oraP expected=$ExpectedNpages"
    $script:failures++
    return
  }
  Write-Output "[$Case] trio ok (check 0, npages=$ExpectedNpages oracle+pig)"
}

function Get-StreamPayloads {
  param([string]$File)
  $bytes = [IO.File]::ReadAllBytes($File)
  $text = [Text.Encoding]::Latin1.GetString($bytes)
  $payloads = @()
  $pos = 0
  while (($pos = $text.IndexOf("endstream", $pos)) -ge 0) {
    $spos = $text.LastIndexOf("stream", $pos - 1)
    $ds = $spos + 6
    if ($ds -lt $bytes.Length -and $bytes[$ds] -eq 13) { $ds++; if ($ds -lt $bytes.Length -and $bytes[$ds] -eq 10) { $ds++ } }
    elseif ($ds -lt $bytes.Length -and $bytes[$ds] -eq 10) { $ds++ }
    $de = $pos
    while ($de -gt $ds -and ($bytes[$de - 1] -eq 13 -or $bytes[$de - 1] -eq 10)) { $de-- }
    if ($de -le $ds) { $pos += 9; continue }
    $dpos = $text.LastIndexOf("<<", $spos)
    $dict = $text.Substring($dpos, $spos - $dpos)
    $raw = $null
    $lm = [regex]::Match($dict, '/Length\s+(\d+)')
    $indirect = [regex]::IsMatch($dict, '/Length\s+\d+\s+\d+\s+R')
    if ($lm.Success -and (-not $indirect)) {
      $len = [int]$lm.Groups[1].Value
      if ($len -gt 0 -and ($ds + $len) -le $bytes.Length) { $raw = $bytes[$ds..($ds + $len - 1)] }
      elseif ($len -eq 0) { $raw = @() }
    }
    if ($null -eq $raw) {
      $de = $pos
      while ($de -gt $ds -and ($bytes[$de - 1] -eq 13 -or $bytes[$de - 1] -eq 10)) { $de-- }
      if ($de -le $ds) { $pos += 9; continue }
      $raw = $bytes[$ds..($de - 1)]
    }
    if ($dict.Contains("/FlateDecode")) {
      $ms = [IO.MemoryStream]::new($raw)
      $zs = [IO.Compression.ZLibStream]::new($ms, [IO.Compression.CompressionMode]::Decompress)
      $out = [IO.MemoryStream]::new()
      $zs.CopyTo($out); $zs.Dispose(); $ms.Dispose()
      $raw = $out.ToArray()
    }
    $payloads += [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
    $pos += 9
  }
  return ($payloads | Sort-Object) -join ","
}

function Get-DecodedText {
  param([string]$File)
  $bytes = [IO.File]::ReadAllBytes($File)
  $text = [Text.Encoding]::Latin1.GetString($bytes)
  $out = @()
  $pos = 0
  while (($pos = $text.IndexOf("endstream", $pos)) -ge 0) {
    $spos = $text.LastIndexOf("stream", $pos - 1)
    $ds = $spos + 6
    if ($ds -lt $bytes.Length -and $bytes[$ds] -eq 13) { $ds++; if ($ds -lt $bytes.Length -and $bytes[$ds] -eq 10) { $ds++ } }
    elseif ($ds -lt $bytes.Length -and $bytes[$ds] -eq 10) { $ds++ }
    $de = $pos
    while ($de -gt $ds -and ($bytes[$de - 1] -eq 13 -or $bytes[$de - 1] -eq 10)) { $de-- }
    if ($de -le $ds) { $pos += 9; continue }
    $dpos = $text.LastIndexOf("<<", $spos)
    $dict = $text.Substring($dpos, $spos - $dpos)
    $raw = $bytes[$ds..($de - 1)]
    if ($dict.Contains("/FlateDecode")) {
      $ms = [IO.MemoryStream]::new($raw)
      $zs = [IO.Compression.ZLibStream]::new($ms, [IO.Compression.CompressionMode]::Decompress)
      $buf = [IO.MemoryStream]::new(); $zs.CopyTo($buf); $zs.Dispose(); $ms.Dispose()
      $raw = $buf.ToArray()
    }
    $out += [Text.Encoding]::Latin1.GetString($raw)
    $pos += 9
  }
  return ($out -join "`n")
}

function New-Case {
  param([string]$Name)
  $d = Join-Path $diffRoot $Name
  if (Test-Path $d) { Remove-Item $d -Recurse -Force }
  New-Item -ItemType Directory -Path "$d/inputs" -Force | Out-Null
  return $d
}

# --- case: select (last and first pages) ---
$case = New-Case 'select'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '2', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "select: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--pages', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "select: oracle failed ($code)" }
Assert-Trio 'select' "$case/lib.pdf" "$case/ora.pdf" 1
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/first.pdf=$case/lib-first.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--', 'outputs/first.pdf')
if ($code -ne 0) { throw "select-first: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-first.pdf")
if ($code -ne 0) { throw "select-first: oracle failed ($code)" }
Assert-Trio 'select-first' "$case/lib-first.pdf" "$case/ora-first.pdf" 1

# --- case: empty (merge from an empty base) ---
$case = New-Case 'empty'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', '--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "empty: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "empty: oracle failed ($code)" }
Assert-Trio 'empty' "$case/lib.pdf" "$case/ora.pdf" 3

$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/bare.pdf=$case/lib-bare.pdf", '--', '--empty', '--', 'outputs/bare.pdf')
if ($code -ne 0) { throw "empty-bare: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--', "$case/ora-bare.pdf")
if ($code -ne 0) { throw "empty-bare: oracle failed ($code)" }
foreach ($pair in @(('lib-bare', "$case/lib-bare.pdf"), ('ora-bare', "$case/ora-bare.pdf"))) {
  $n = Get-OracleNpages $pair[1]
  if ($n -ne 0) { Write-Output "[empty-bare] FAIL: page count $($pair[0])=$n, expected 0"; $script:failures++ }
  $c = Start-Process -FilePath $QpdfExe -ArgumentList @('--check', $pair[1]) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qc.txt" -RedirectStandardError "$env:TEMP/qce.txt"
  if ($c.ExitCode -ne 2) { Write-Output "[empty-bare] FAIL: oracle check exit $($pair[0])=$($c.ExitCode), expected 2"; $script:failures++ }
}
Write-Output "[empty-bare] zero-page outputs agree"
$p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll, 'emit', '--workdir', $case, '--', '--empty', '--show-npages') ) -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/managed-npages.txt"
$line = Select-String -Path "$case/managed-npages.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
if ($null -eq $line) { throw "empty-npages: managed emit produced no status line" }
$m = [int]$line.Matches[0].Groups[1].Value
$o = Start-Process -FilePath $QpdfExe -ArgumentList @('--empty', '--show-npages') -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/ora-npages.txt"
if ($m -ne 0 -or $o.ExitCode -ne 0) { Write-Output "[empty-npages] FAIL: exits managed=$m oracle=$($o.ExitCode), expected 0/0"; $script:failures++ }
elseif ((Get-Content "$case/ora-npages.txt" -Raw).Trim() -ne "0") { Write-Output "[empty-npages] FAIL: oracle page count is not 0"; $script:failures++ }
else { Write-Output "[empty-npages] exit parity (0/0) with zero pages" }
$p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll, 'emit', '--workdir', $case, '--', '--empty', '--check') ) -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/managed-check.txt"
$line = Select-String -Path "$case/managed-check.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
if ($null -eq $line) { throw "empty-check: managed emit produced no status line" }
$m = [int]$line.Matches[0].Groups[1].Value
$o = Invoke-Oracle $case @('--empty', '--check')
if ($m -ne 2 -or $o -ne 2) { Write-Output "[empty-check] FAIL: exits managed=$m oracle=$o, expected 2/2"; $script:failures++ }
else { Write-Output "[empty-check] exit parity (2/2) on page-less input" }

# --- case: merge ---
$case = New-Case 'merge'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "merge: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "merge: oracle failed ($code)" }
Assert-Trio 'merge' "$case/lib.pdf" "$case/ora.pdf" 3

# --- case: split (managed job vs oracle argv split publication) ---
$case = New-Case 'split'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Set-Content "$case/job.json" '{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "splitPages": ""}'
$code = Invoke-Emit $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/s-1.pdf=$case/lib-s-1.pdf", '--out', "outputs/s-2.pdf=$case/lib-s-2.pdf")
if ($code -ne 0) { throw "split: emit failed ($code)" }
$code = Invoke-Oracle $case @('--split-pages', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-s.pdf")
if ($code -ne 0) { throw "split: oracle failed ($code)" }
Assert-Trio 'split-1' "$case/lib-s-1.pdf" "$case/ora-s-1.pdf" 1
Assert-Trio 'split-2' "$case/lib-s-2.pdf" "$case/ora-s-2.pdf" 1
foreach ($pair in @(('lib-s-1', "$case/lib-s-1.pdf", '(First)', '(Second)'), ('ora-s-1', "$case/ora-s-1.pdf", '(First)', '(Second)'), ('lib-s-2', "$case/lib-s-2.pdf", '(Second)', '(First)'), ('ora-s-2', "$case/ora-s-2.pdf", '(Second)', '(First)'))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains($pair[2])) { Write-Output "[split] FAIL: '$($pair[2])' missing from decoded $($pair[0]) output"; $script:failures++ }
  if ($decoded.Contains($pair[3])) { Write-Output "[split] FAIL: '$($pair[3])' remains in decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[split] group marker assertions done"

# --- case: collate (managed vs oracle interleaved merge) ---
$case = New-Case 'collate'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/m.pdf=$case/lib.pdf", '--', '--collate', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', 'outputs/m.pdf')
if ($code -ne 0) { throw "collate: emit failed ($code)" }
$code = Invoke-Oracle $case @('--collate', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "collate: oracle failed ($code)" }
Assert-Trio 'collate' "$case/lib.pdf" "$case/ora.pdf" 2
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/r.pdf=$case/lib-r.pdf", '--', '--collate', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1-2', 'inputs/a.pdf', '2-1', '--', 'outputs/r.pdf')
if ($code -ne 0) { throw "collate/ranges: emit failed ($code)" }
$code = Invoke-Oracle $case @('--collate', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1-2', 'inputs/a.pdf', '2-1', '--', "$case/ora-r.pdf")
if ($code -ne 0) { throw "collate/ranges: oracle failed ($code)" }
Assert-Trio 'collate-ranges' "$case/lib-r.pdf" "$case/ora-r.pdf" 4
$libText = [IO.File]::ReadAllText("$case/lib-r.pdf")
$libMarks = @()
foreach ($m in [regex]::Matches($libText, '\((First|Second)\)')) { $libMarks += $m.Groups[1].Value }
if (($libMarks -join ',') -ne 'First,Second,Second,First') { Write-Output "[collate-ranges] FAIL: lib order [$($libMarks -join ',')]"; $script:failures++ }
else { Write-Output "[collate-ranges] lib order agrees ([First,Second,Second,First])" }
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-pages', "$case/ora-r.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-r.txt" -RedirectStandardError "$case/show-ora-r-err.txt"
if ($p.ExitCode -ne 0) { Write-Output "[collate-ranges] FAIL: oracle show-pages exit $($p.ExitCode)"; $script:failures++ }
else {
  $oraRefs = @()
  foreach ($m in [regex]::Matches((Get-Content "$case/show-ora-r.txt" -Raw), 'content:\s*\r?\n\s*(\d+) 0 R')) { $oraRefs += $m.Groups[1].Value }
  if ($oraRefs.Count -ne 4 -or $oraRefs[2] -ne $oraRefs[1] -or $oraRefs[3] -ne $oraRefs[0] -or $oraRefs[0] -eq $oraRefs[1]) { Write-Output "[collate-ranges] FAIL: ora sharing pattern [$($oraRefs -join ',')]"; $script:failures++ }
  else { Write-Output "[collate-ranges] ora order agrees under sharing ([$($oraRefs -join ',')])" }
}

# --- case: ranges (managed vs oracle full page-range selections) ---
$case = New-Case 'ranges'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
foreach ($sub in @(@('span', @('.', '1-2'), 2), @('reverse', @('.', '2-1'), 2), @('exclude', @('.', '1-2,x2'), 1), @('omitted', @('inputs/a.pdf'), 2), @('fileform-all', @('--file=inputs/a.pdf'), 2))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', 'inputs/a.pdf', '--pages') + $sub[1] + @('--', "outputs/$name.pdf"))
  if ($code -ne 0) { throw "ranges/$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case (@('inputs/a.pdf', '--pages') + $sub[1] + @('--', "$case/ora-$name.pdf"))
  if ($code -ne 0) { throw "ranges/$name`: oracle failed ($code)" }
  Assert-Trio "ranges-$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" $sub[2]
}
Write-Output "[ranges] span/reverse/exclude/omitted trios done"

# --- case: rotate-ranges (managed vs oracle rotation ranges, upstream outside form) ---
$case = New-Case 'rotate-ranges'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
foreach ($sub in @(@('desc', @('+90:2-1')), @('rel', @('+90:r1')), @('excl', @('+90:1-2,x2')), @('odd', @('+90:1-2:odd')), @('multi', @('+90:r1', '+180:1')))) {
  $name = $sub[0]
  $flags = @()
  foreach ($spec in $sub[1]) { $flags += "--rotate=$spec" }
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--') + $flags + @('inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "outputs/$name.pdf"))
  if ($code -ne 0) { throw "rotate-ranges/$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case ($flags + @('inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-$name.pdf"))
  if ($code -ne 0) { throw "rotate-ranges/$name`: oracle failed ($code)" }
  Assert-Trio "rotate-ranges-$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 2
  $libRotates = @()
  foreach ($m in [regex]::Matches([IO.File]::ReadAllText("$case/lib-$name.pdf"), '/Rotate (\d+)')) { $libRotates += $m.Groups[1].Value }
  $oraRotates = @()
  foreach ($m in [regex]::Matches([IO.File]::ReadAllText("$case/ora-$name.pdf"), '/Rotate (\d+)')) { $oraRotates += $m.Groups[1].Value }
  if (($libRotates -join ',') -ne ($oraRotates -join ',')) { Write-Output "[rotate-ranges/$name] FAIL: rotations lib=[$($libRotates -join ',')] ora=[$($oraRotates -join ',')]"; $script:failures++ }
  else { Write-Output "[rotate-ranges/$name] per-page rotations agree ([$($libRotates -join ',')])" }
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/fileform.pdf=$case/lib-fileform.pdf", '--', '--rotate=+90:r1', 'inputs/a.pdf', '--pages', '--file=inputs/a.pdf', '--range=1-2', '--', 'outputs/fileform.pdf')
if ($code -ne 0) { throw "rotate-ranges/fileform: emit failed ($code)" }
$code = Invoke-Oracle $case @('--rotate=+90:r1', 'inputs/a.pdf', '--pages', '--file=inputs/a.pdf', '--range=1-2', '--', "$case/ora-fileform.pdf")
if ($code -ne 0) { throw "rotate-ranges/fileform: oracle failed ($code)" }
Assert-Trio 'rotate-ranges-fileform' "$case/lib-fileform.pdf" "$case/ora-fileform.pdf" 2
$libRotates = @()
foreach ($m in [regex]::Matches([IO.File]::ReadAllText("$case/lib-fileform.pdf"), '/Rotate (\d+)')) { $libRotates += $m.Groups[1].Value }
$oraRotates = @()
foreach ($m in [regex]::Matches([IO.File]::ReadAllText("$case/ora-fileform.pdf"), '/Rotate (\d+)')) { $oraRotates += $m.Groups[1].Value }
if (($libRotates -join ',') -ne ($oraRotates -join ',') -or ($libRotates -join ',') -ne '90') { Write-Output "[rotate-ranges/fileform] FAIL: rotations lib=[$($libRotates -join ',')] ora=[$($oraRotates -join ',')]"; $script:failures++ }
else { Write-Output "[rotate-ranges/fileform] file-form rotations agree ([90])" }

# --- case: uncompress (flate input produced by oracle) ---
$case = New-Case 'uncompress'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('inputs/a.pdf', '--stream-data=compress', '--', "$case/inputs/c.pdf")
if ($code -ne 0) { throw "uncompress setup: oracle compress failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/c.pdf', '--pages', '.', '1', '.', '2', '--stream-data=uncompress', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "uncompress: emit failed ($code)" }
$code = Invoke-Oracle $case @('--stream-data=uncompress', 'inputs/c.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "uncompress: oracle failed ($code)" }
Assert-Trio 'uncompress' "$case/lib.pdf" "$case/ora.pdf" 2
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[uncompress] FAIL: decoded stream payloads differ"; $script:failures++ } else { Write-Output "[uncompress] payloads match" }

# --- case: normalize (exact decoded-content comparison) ---
$case = New-Case 'normalize'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--normalize-content=y', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "normalize: emit failed ($code)" }
$code = Invoke-Oracle $case @('--normalize-content=y', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "normalize: oracle failed ($code)" }
Assert-Trio 'normalize' "$case/lib.pdf" "$case/ora.pdf" 2
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[normalize] FAIL: normalized content payloads differ"; $script:failures++ } else { Write-Output "[normalize] payloads match" }

# --- case: coalesce (decoded-content comparison) ---
$case = New-Case 'coalesce'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--coalesce-contents', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "coalesce: emit failed ($code)" }
$code = Invoke-Oracle $case @('--coalesce-contents', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "coalesce: oracle failed ($code)" }
Assert-Trio 'coalesce' "$case/lib.pdf" "$case/ora.pdf" 2
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[coalesce] FAIL: coalesced content payloads differ"; $script:failures++ } else { Write-Output "[coalesce] payloads match" }

# --- case: compress (recompressed payloads identical) ---
$case = New-Case 'compress'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('inputs/a.pdf', '--stream-data=compress', '--', "$case/inputs/c.pdf")
if ($code -ne 0) { throw "compress setup: oracle compress failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/c.pdf', '--pages', '.', '1', '.', '2', '--stream-data=compress', '--recompress-flate', '--compression-level=9', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "compress: emit failed ($code)" }
$code = Invoke-Oracle $case @('--stream-data=compress', '--recompress-flate', '--compression-level=9', 'inputs/c.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "compress: oracle failed ($code)" }
Assert-Trio 'compress' "$case/lib.pdf" "$case/ora.pdf" 2
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[compress] FAIL: recompressed content payloads differ"; $script:failures++ } else { Write-Output "[compress] payloads match" }

# --- case: flatten-rotation (baked boxes and counter-transform) ---
$case = New-Case 'flatten-rotation'
Copy-Item "$fixtureDir/rotated-1page.pdf" "$case/inputs/r.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/r.pdf', '--pages', '.', '1', '--flatten-rotation', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "flatten-rotation: emit failed ($code)" }
$code = Invoke-Oracle $case @('--flatten-rotation', 'inputs/r.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "flatten-rotation: oracle failed ($code)" }
Assert-Trio 'flatten-rotation' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/Rotate')) { Write-Output "[flatten-rotation] FAIL: /Rotate remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('792 612')) { Write-Output "[flatten-rotation] FAIL: swapped MediaBox missing from $($pair[0]) output"; $script:failures++ }
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains('0 -1 1 0 0 612 cm')) { Write-Output "[flatten-rotation] FAIL: counter-transform missing from decoded $($pair[0]) output"; $script:failures++ }
}
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[flatten-rotation] FAIL: flattened content payloads differ"; $script:failures++ } else { Write-Output "[flatten-rotation] payloads match" }
Write-Output "[flatten-rotation] box and matrix assertions done"

Copy-Item "$fixtureDir/rotated-ap-1page.pdf" "$case/inputs/ap.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/ap.pdf=$case/lib-ap.pdf", '--', 'inputs/ap.pdf', '--pages', '.', '1', '--flatten-rotation', '--', 'outputs/ap.pdf')
if ($code -ne 0) { throw "flatten-rotation-ap: emit failed ($code)" }
$code = Invoke-Oracle $case @('--flatten-rotation', 'inputs/ap.pdf', '--pages', '.', '1', '--', "$case/ora-ap.pdf")
if ($code -ne 0) { throw "flatten-rotation-ap: oracle failed ($code)" }
Assert-Trio 'flatten-rotation-ap' "$case/lib-ap.pdf" "$case/ora-ap.pdf" 1
foreach ($pair in @(('lib-ap', "$case/lib-ap.pdf"), ('ora-ap', "$case/ora-ap.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText -notmatch '/Matrix\s*\[\s*0\s+-1\s+1\s+0\s+0\s+612\s*\]') { Write-Output "[flatten-rotation-ap] FAIL: rotated appearance matrix missing from $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/UserUnit')) { Write-Output "[flatten-rotation-ap] FAIL: direct user unit missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[flatten-rotation-ap] appearance assertions done"

# --- case: prune (page-operation resource pruning) ---
$case = New-Case 'prune'
Copy-Item "$fixtureDir/unref-1page.pdf" "$case/inputs/u.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/u.pdf', '--pages', '.', '1', '--remove-unreferenced-resources=yes', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "prune: emit failed ($code)" }
$code = Invoke-Oracle $case @('--remove-unreferenced-resources=yes', 'inputs/u.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "prune: oracle failed ($code)" }
Assert-Trio 'prune' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/F2')) { Write-Output "[prune] FAIL: unreferenced /F2 remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/F1')) { Write-Output "[prune] FAIL: referenced /F1 missing from $($pair[0]) output"; $script:failures++ }
}
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[prune] FAIL: pruned content payloads differ"; $script:failures++ } else { Write-Output "[prune] payloads match" }
Write-Output "[prune] resource assertions done"

# --- case: preserve (managed vs oracle resource preservation) ---
$case = New-Case 'preserve'
Copy-Item "$fixtureDir/unref-1page.pdf" "$case/inputs/u.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/u.pdf', '--pages', '.', '1', '--preserve-unreferenced-resources', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "preserve: emit failed ($code)" }
$code = Invoke-Oracle $case @('--preserve-unreferenced-resources', 'inputs/u.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "preserve: oracle failed ($code)" }
Assert-Trio 'preserve' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if (-not $rawText.Contains('/F2')) { Write-Output "[preserve-resources] FAIL: unreferenced /F2 missing from $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/F1')) { Write-Output "[preserve-resources] FAIL: referenced /F1 missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[preserve-resources] resource assertions done"

# --- case: json (structural page-descriptor agreement) ---
$case = New-Case 'json'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.json=$case/lib.json", '--', 'inputs/a.pdf', '--json', '--json-key=pages', '--', 'outputs/a.json')
if ($code -ne 0) { throw "json: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--json-key=pages', 'inputs/a.pdf')
if ($code -ne 0) { throw "json: oracle failed ($code)" }
Copy-Item "$case/ora-stdout.txt" "$case/ora.json" -Force
$libJson = Get-Content "$case/lib.json" -Raw | ConvertFrom-Json
$oraJson = Get-Content "$case/ora.json" -Raw | ConvertFrom-Json
if ($libJson.pages.Count -ne 2 -or $oraJson.pages.Count -ne 2) { Write-Output "[json] FAIL: page count lib=$($libJson.pages.Count) ora=$($oraJson.pages.Count)"; $script:failures++ }
elseif ($libJson.version -ne $oraJson.version) { Write-Output "[json] FAIL: version lib=$($libJson.version) ora=$($oraJson.version)"; $script:failures++ }
else {
  $mismatch = $false
  for ($i = 0; $i -lt 2; $i++) {
    if ($libJson.pages[$i].object -ne $oraJson.pages[$i].object) { $mismatch = $true }
    if (($libJson.pages[$i].contents -join ',') -ne ($oraJson.pages[$i].contents -join ',')) { $mismatch = $true }
    if ($libJson.pages[$i].pageposfrom1 -ne $oraJson.pages[$i].pageposfrom1) { $mismatch = $true }
    if ("$($libJson.pages[$i].label)" -ne "$($oraJson.pages[$i].label)") { $mismatch = $true }
  }
  if ($mismatch) { Write-Output "[json] FAIL: page descriptors differ structurally"; $script:failures++ } else { Write-Output "[json] page descriptors agree structurally (2 pages)" }
}

# --- case: json-images (managed vs oracle page-image descriptors) ---
$case = New-Case 'json-images'
Copy-Item "$fixtureDir/images-1page.pdf" "$case/inputs/raw.pdf"
Copy-Item "$fixtureDir/images-nodim-1page.pdf" "$case/inputs/nodim.pdf"
Copy-Item "$fixtureDir/photo-1page.pdf" "$case/inputs/photo.pdf"
Copy-Item "$fixtureDir/images-filtered-1page.pdf" "$case/inputs/filtered.pdf"
function Compare-ImageJson {
  param([string]$Tag, [string]$LibFile, [string]$OraFile)
  $lib = (Get-Content $LibFile -Raw | ConvertFrom-Json -Depth 20).pages
  $ora = (Get-Content $OraFile -Raw | ConvertFrom-Json -Depth 20).pages
  if ($lib.Count -ne $ora.Count) { Write-Output "[json-images/$Tag] FAIL: page count lib=$($lib.Count) ora=$($ora.Count)"; $script:failures++; return }
  for ($i = 0; $i -lt $lib.Count; $i++) {
    $limages = $lib[$i].images
    $oimages = $ora[$i].images
    if ($limages.Count -ne $oimages.Count) { Write-Output "[json-images/$Tag] FAIL: image count lib=$($limages.Count) ora=$($oimages.Count)"; $script:failures++; return }
    for ($j = 0; $j -lt $limages.Count; $j++) {
      $l = $limages[$j]
      $o = $oimages[$j]
      $same = ("$($l.bitspercomponent)" -eq "$($o.bitspercomponent)") -and ("$($l.height)" -eq "$($o.height)") -and ("$($l.width)" -eq "$($o.width)") -and ($l.name -eq $o.name) -and ($l.object -eq $o.object) -and ($l.filterable -eq $o.filterable) -and (($l.filter -join ',') -eq ($o.filter -join ','))
      $same = $same -and ((ConvertTo-Json $l.colorspace -Compress -Depth 10) -eq (ConvertTo-Json $o.colorspace -Compress -Depth 10))
      $same = $same -and ((ConvertTo-Json $l.decodeparms -Compress -Depth 10) -eq (ConvertTo-Json $o.decodeparms -Compress -Depth 10))
      if (-not $same) { Write-Output "[json-images/$Tag] FAIL: image $j differs"; $script:failures++; return }
    }
  }
  Write-Output "[json-images/$Tag] image descriptors agree"
}
foreach ($sub in @(@('raw', 'inputs/raw.pdf', @()), @('nodim', 'inputs/nodim.pdf', @()), @('photo', 'inputs/photo.pdf', @()), @('filtered', 'inputs/filtered.pdf', @()))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.json=$case/lib-$name.json", '--', $sub[1], '--json', '--', "outputs/$name.json"))
  if ($code -ne 0) { throw "json-images/$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case (@('--json', $sub[1]))
  if ($code -ne 0) { throw "json-images/$name`: oracle failed ($code)" }
  Copy-Item "$case/ora-stdout.txt" "$case/ora-$name.json" -Force
  Compare-ImageJson $name "$case/lib-$name.json" "$case/ora-$name.json"
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/all.json=$case/lib-all.json", '--', 'inputs/filtered.pdf', '--json', '--decode-level=all', '--', 'outputs/all.json')
if ($code -ne 0) { throw "json-images/all: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--decode-level=all', 'inputs/filtered.pdf')
if ($code -ne 0) { throw "json-images/all: oracle failed ($code)" }
Copy-Item "$case/ora-stdout.txt" "$case/ora-all.json" -Force
Compare-ImageJson 'all' "$case/lib-all.json" "$case/ora-all.json"

# --- case: json-import (managed vs oracle PDF construction from qpdf JSON) ---
$case = New-Case 'json-import'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--json', '--json-stream-data=inline', 'inputs/a.pdf', '--', "$case/inputs/base.json")
if ($code -ne 0) { throw "json-import setup: oracle export failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/base.json', '--json-input', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "json-import: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json-input', 'inputs/base.json', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "json-import: oracle failed ($code)" }
Assert-Trio 'json-import' "$case/lib.pdf" "$case/ora.pdf" 2
if ((Get-StreamPayloads "$case/lib.pdf") -ne (Get-StreamPayloads "$case/ora.pdf")) { Write-Output "[json-import] FAIL: decoded stream payloads differ"; $script:failures++ } else { Write-Output "[json-import] payloads match" }

# --- case: json-update (managed vs oracle PDF update from mutated qpdf JSON) ---
$case = New-Case 'json-update'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--json', '--json-stream-data=inline', 'inputs/a.pdf', '--', "$case/inputs/base.json")
if ($code -ne 0) { throw "json-update setup: oracle export failed ($code)" }
$mutJson = Get-Content "$case/inputs/base.json" -Raw | ConvertFrom-Json
$mutJson.qpdf[1].'obj:1 0 R'.value | Add-Member -NotePropertyName '/Custom' -NotePropertyValue 'u:New' -Force
$mutJson | ConvertTo-Json -Depth 100 | Set-Content "$case/inputs/mut.json"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--update-from-json=inputs/mut.json', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "json-update: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--update-from-json=inputs/mut.json', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "json-update: oracle failed ($code)" }
Assert-Trio 'json-update' "$case/lib.pdf" "$case/ora.pdf" 2
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if (-not $rawText.Contains('/Custom')) { Write-Output "[json-update] FAIL: /Custom missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[json-update] mutation assertions done"

# --- case: json-modes (managed vs oracle JSON inspection modifiers) ---
$case = New-Case 'json-modes'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/o.json=$case/lib-o.json", '--', 'inputs/a.pdf', '--json', '--json-object=trailer', '--', 'outputs/o.json')
if ($code -ne 0) { throw "json-modes/object: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--json-object=trailer', 'inputs/a.pdf', '--', "$case/ora-o.json")
if ($code -ne 0) { throw "json-modes/object: oracle failed ($code)" }
$libObj = Get-Content "$case/lib-o.json" -Raw | ConvertFrom-Json
$oraObj = Get-Content "$case/ora-o.json" -Raw | ConvertFrom-Json
$libKeys = ($libObj.PSObject.Properties.Name | Sort-Object) -join ','
$oraKeys = ($oraObj.PSObject.Properties.Name | Sort-Object) -join ','
if ($libKeys -ne $oraKeys) { Write-Output "[json-object] FAIL: top-level keys lib=[$libKeys] ora=[$oraKeys]"; $script:failures++ }
elseif ((($libObj.qpdf[1].trailer | ConvertTo-Json -Compress -Depth 100)) -ne (($oraObj.qpdf[1].trailer | ConvertTo-Json -Compress -Depth 100))) { Write-Output "[json-object] FAIL: trailer values differ"; $script:failures++ }
elseif ($libObj.pages.Count -ne 2 -or $oraObj.pages.Count -ne 2) { Write-Output "[json-object] FAIL: page counts differ"; $script:failures++ }
else { Write-Output "[json-object] trailer filter agrees (same keys, trailer, 2 pages)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/i.json=$case/lib-i.json", '--', 'inputs/a.pdf', '--json', '--json-stream-data=inline', '--', 'outputs/i.json')
if ($code -ne 0) { throw "json-modes/inline: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--json-stream-data=inline', 'inputs/a.pdf', '--', "$case/ora-i.json")
if ($code -ne 0) { throw "json-modes/inline: oracle failed ($code)" }
$libInline = Get-Content "$case/lib-i.json" -Raw | ConvertFrom-Json
$oraInline = Get-Content "$case/ora-i.json" -Raw | ConvertFrom-Json
$inlineMismatch = $false
foreach ($k in @($libInline.qpdf[1].PSObject.Properties.Name)) {
  if ((($libInline.qpdf[1].$k.stream | ConvertTo-Json -Compress -Depth 100)) -ne (($oraInline.qpdf[1].$k.stream | ConvertTo-Json -Compress -Depth 100))) { Write-Output "[json-stream-data] FAIL: stream payload differs for $k"; $script:failures++; $inlineMismatch = $true }
}
if (-not $inlineMismatch) { Write-Output "[json-stream-data] inline payloads match" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/m.json=$case/lib-m.json", '--', 'inputs/a.pdf', '--json-output', '--', 'outputs/m.json')
if ($code -ne 0) { throw "json-modes/output: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json-output', 'inputs/a.pdf', '--', "$case/ora-m.json")
if ($code -ne 0) { throw "json-modes/output: oracle failed ($code)" }
$libMin = Get-Content "$case/lib-m.json" -Raw | ConvertFrom-Json
$oraMin = Get-Content "$case/ora-m.json" -Raw | ConvertFrom-Json
if ((($libMin.qpdf[0] | ConvertTo-Json -Compress -Depth 100)) -ne (($oraMin.qpdf[0] | ConvertTo-Json -Compress -Depth 100))) { Write-Output "[json-output] FAIL: minimal headers differ"; $script:failures++ }
else {
  $minMismatch = $false
  foreach ($k in @($libMin.qpdf[1].PSObject.Properties.Name)) {
    if ((($libMin.qpdf[1].$k | ConvertTo-Json -Compress -Depth 100)) -ne (($oraMin.qpdf[1].$k | ConvertTo-Json -Compress -Depth 100))) { Write-Output "[json-output] FAIL: object differs for $k"; $script:failures++; $minMismatch = $true }
  }
  if (-not $minMismatch) { Write-Output "[json-output] minimal outputs match" }
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/p.json=$case/lib-p.json", '--out', "s-5=$case/lib-s5.bin", '--out', "s-6=$case/lib-s6.bin", '--', 'inputs/a.pdf', '--json', '--json-stream-data=file', '--json-stream-prefix=s', '--', 'outputs/p.json')
if ($code -ne 0) { throw "json-modes/prefix: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--json-stream-data=file', '--json-stream-prefix=s', 'inputs/a.pdf', '--', "$case/ora-p.json")
if ($code -ne 0) { throw "json-modes/prefix: oracle failed ($code)" }
$libPre = Get-Content "$case/lib-p.json" -Raw | ConvertFrom-Json
$oraPre = Get-Content "$case/ora-p.json" -Raw | ConvertFrom-Json
$preMismatch = $false
foreach ($k in @('obj:5 0 R', 'obj:6 0 R')) {
  if ((($libPre.qpdf[1].$k.stream | ConvertTo-Json -Compress -Depth 100)) -ne (($oraPre.qpdf[1].$k.stream | ConvertTo-Json -Compress -Depth 100))) { Write-Output "[json-prefix] FAIL: datafile reference differs for $k"; $script:failures++; $preMismatch = $true }
}
foreach ($pair in @(('s-5', "$case/lib-s5.bin", "$case/s-5"), ('s-6', "$case/lib-s6.bin", "$case/s-6"))) {
  $lh = (Get-FileHash $pair[1] -Algorithm SHA256).Hash
  $oh = (Get-FileHash $pair[2] -Algorithm SHA256).Hash
  if ($lh -ne $oh) { Write-Output "[json-prefix] FAIL: sidecar bytes differ for $($pair[0])"; $script:failures++; $preMismatch = $true }
}
if (-not $preMismatch) { Write-Output "[json-prefix] sidecar references and bytes match" }

# --- case: attachments (managed vs oracle embedded-file operations) ---
$case = New-Case 'attachments'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
[IO.File]::WriteAllBytes("$case/inputs/note.bin", [Text.Encoding]::ASCII.GetBytes('attachment payload one'))
function Get-AttachmentKeys {
  param([string]$File)
  $keys = @()
  foreach ($line in (Get-Content $File)) {
    $m = [regex]::Match($line, '(\S+) -> \d+,\d+')
    if ($m.Success) { $keys += $m.Groups[1].Value }
  }
  return ($keys | Sort-Object) -join ','
}
function Invoke-ManagedList {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--list-attachments') -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  return $p.ExitCode
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib-add.pdf", '--', 'inputs/a.pdf', '--add-attachment', 'inputs/note.bin', '--key=note.txt', '--description=Hi', '--', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "attachments/add: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--add-attachment', 'inputs/note.bin', '--key=note.txt', '--description=Hi', '--', "$case/ora-add.pdf")
if ($code -ne 0) { throw "attachments/add: oracle failed ($code)" }
Assert-Trio 'attachments/add' "$case/lib-add.pdf" "$case/ora-add.pdf" 1
Copy-Item "$case/ora-add.pdf" "$case/inputs/ora-add.pdf" -Force
Copy-Item "$case/lib-add.pdf" "$case/inputs/lib-add.pdf" -Force
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/n.bin=$case/lib-note.bin", '--', 'inputs/ora-add.pdf', '--show-attachment=note.txt', '--', 'outputs/n.bin')
if ($code -ne 0) { throw "attachments/show: emit failed ($code)" }
$code = Invoke-Oracle $case @('--show-attachment=note.txt', 'inputs/lib-add.pdf')
if ($code -ne 0) { throw "attachments/show: oracle failed ($code)" }
Copy-Item "$case/ora-stdout.txt" "$case/ora-note.bin" -Force
$lh = (Get-FileHash "$case/lib-note.bin" -Algorithm SHA256).Hash
$oh = (Get-FileHash "$case/ora-note.bin" -Algorithm SHA256).Hash
$ph = (Get-FileHash "$case/inputs/note.bin" -Algorithm SHA256).Hash
if ($lh -ne $ph -or $oh -ne $ph) { Write-Output "[attachments-show] FAIL: extracted bytes differ from source"; $script:failures++ }
else { Write-Output "[attachments-show] cross-extracted bytes match the source payload" }
$code = Invoke-ManagedList $case 'inputs/ora-add.pdf' 'list-ora'
if ($code -ne 0) { throw "attachments/list: managed failed ($code)" }
$code = Invoke-Oracle $case @('--list-attachments', 'inputs/ora-add.pdf')
if ($code -ne 0) { throw "attachments/list: oracle failed ($code)" }
$libKeys = Get-AttachmentKeys "$case/managed-list-ora.txt"
$oraKeys = Get-AttachmentKeys "$case/ora-stdout.txt"
if ($libKeys -ne $oraKeys -or $libKeys -ne 'note.txt') { Write-Output "[attachments-list] FAIL: key sets lib=[$libKeys] ora=[$oraKeys]"; $script:failures++ }
else { Write-Output "[attachments-list] key sets agree (note.txt)" }
Copy-Item "$case/ora-add.pdf" "$case/inputs/seed.pdf" -Force
foreach ($sub in @(@('', 'copy', ''), @('--prefix=cp-', 'copyp', 'cp-'))) {
  $flag = $sub[0]
  $tag = $sub[1]
  $prefix = $sub[2]
  $emitArgs = @('emit', '--workdir', $case, '--out', "outputs/$tag.pdf=$case/lib-$tag.pdf", '--', 'inputs/a.pdf', '--copy-attachments-from', 'inputs/seed.pdf')
  if ($flag -ne '') { $emitArgs += $flag }
  $emitArgs += @('--', '--', "outputs/$tag.pdf")
  $code = Invoke-Emit $case $emitArgs
  if ($code -ne 0) { throw "attachments/${tag}: emit failed ($code)" }
  $oraArgs = @('inputs/a.pdf', '--copy-attachments-from', 'inputs/seed.pdf')
  if ($flag -ne '') { $oraArgs += $flag }
  $oraArgs += @('--', "$case/ora-$tag.pdf")
  $code = Invoke-Oracle $case $oraArgs
  if ($code -ne 0) { throw "attachments/${tag}: oracle failed ($code)" }
  Assert-Trio "attachments/$tag" "$case/lib-$tag.pdf" "$case/ora-$tag.pdf" 1
  Copy-Item "$case/lib-$tag.pdf" "$case/inputs/lib-$tag.pdf" -Force
  Copy-Item "$case/ora-$tag.pdf" "$case/inputs/ora-$tag.pdf" -Force
  $code = Invoke-ManagedList $case "inputs/lib-$tag.pdf" "list-lib-$tag"
  if ($code -ne 0) { throw "attachments/${tag}: managed list failed ($code)" }
  $code = Invoke-Oracle $case @('--list-attachments', "inputs/ora-$tag.pdf")
  if ($code -ne 0) { throw "attachments/${tag}: oracle list failed ($code)" }
  $libKeys = Get-AttachmentKeys "$case/managed-list-lib-$tag.txt"
  $oraKeys = Get-AttachmentKeys "$case/ora-stdout.txt"
  $expected = "${prefix}note.txt"
  if ($libKeys -ne $oraKeys -or $libKeys -ne $expected) { Write-Output "[attachments-copy] FAIL: $tag key sets lib=[$libKeys] ora=[$oraKeys], expected [$expected]"; $script:failures++ }
  else { Write-Output "[attachments-copy] $tag key sets agree ($expected)" }
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/rm.pdf=$case/lib-rm.pdf", '--', 'inputs/ora-add.pdf', '--remove-attachment=note.txt', '--', 'outputs/rm.pdf')
if ($code -ne 0) { throw "attachments/remove: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/ora-add.pdf', '--remove-attachment=note.txt', '--', "$case/ora-rm.pdf")
if ($code -ne 0) { throw "attachments/remove: oracle failed ($code)" }
Assert-Trio 'attachments/remove' "$case/lib-rm.pdf" "$case/ora-rm.pdf" 1
Copy-Item "$case/lib-rm.pdf" "$case/inputs/lib-rm.pdf" -Force
Copy-Item "$case/ora-rm.pdf" "$case/inputs/ora-rm.pdf" -Force
$code = Invoke-ManagedList $case 'inputs/lib-rm.pdf' 'list-lib-rm'
if ($code -ne 0) { throw "attachments/remove: managed list failed ($code)" }
$code = Invoke-Oracle $case @('--list-attachments', 'inputs/ora-rm.pdf')
if ($code -ne 0) { throw "attachments/remove: oracle list failed ($code)" }
$libKeys = Get-AttachmentKeys "$case/managed-list-lib-rm.txt"
$oraKeys = Get-AttachmentKeys "$case/ora-stdout.txt"
if ($libKeys -ne '' -or $oraKeys -ne '') { Write-Output "[attachments-remove] FAIL: keys remain lib=[$libKeys] ora=[$oraKeys]"; $script:failures++ }
else { Write-Output "[attachments-remove] key sets empty on both sides" }

# --- case: metadata (managed vs oracle info/metadata removal) ---
$case = New-Case 'metadata'
Copy-Item "$fixtureDir/meta-1page.pdf" "$case/inputs/a.pdf"
foreach ($sub in @(('remove-info', '--remove-info'), ('remove-metadata', '--remove-metadata'))) {
  $tag = $sub[0]
  $flag = $sub[1]
  $code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/$tag.pdf=$case/lib-$tag.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', $flag, '--', "outputs/$tag.pdf")
  if ($code -ne 0) { throw "metadata/${tag}: emit failed ($code)" }
  $code = Invoke-Oracle $case @($flag, 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-$tag.pdf")
  if ($code -ne 0) { throw "metadata/${tag}: oracle failed ($code)" }
  Assert-Trio "metadata/$tag" "$case/lib-$tag.pdf" "$case/ora-$tag.pdf" 1
}
foreach ($pair in @(('lib', "$case/lib-remove-info.pdf"), ('ora', "$case/ora-remove-info.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/Metadata')) { Write-Output "[remove-info] FAIL: /Metadata remains in $($pair[0]) output"; $script:failures++ }
  if ($rawText.Contains('/Title')) { Write-Output "[remove-info] FAIL: /Title remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/ModDate')) { Write-Output "[remove-info] FAIL: /ModDate missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[remove-info] info/metadata assertions done"
foreach ($pair in @(('lib', "$case/lib-remove-metadata.pdf"), ('ora', "$case/ora-remove-metadata.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/Metadata')) { Write-Output "[remove-metadata] FAIL: /Metadata remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/Title')) { Write-Output "[remove-metadata] FAIL: /Title missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[remove-metadata] info/metadata assertions done"

# --- case: labels (managed vs oracle page-label setting and removal) ---
$case = New-Case 'labels'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/labeled.pdf=$case/lib-labeled.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--set-page-labels', '1:r', '--', 'outputs/labeled.pdf')
if ($code -ne 0) { throw "labels/set: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--set-page-labels', '1:r', '--', '--pages', '.', '1', '.', '2', '--', "$case/ora-labeled.pdf")
if ($code -ne 0) { throw "labels/set: oracle failed ($code)" }
Assert-Trio 'labels/set' "$case/lib-labeled.pdf" "$case/ora-labeled.pdf" 2
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-labeled.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib.txt" -RedirectStandardError "$case/show-lib-err.txt"
$libCat = (Get-Content "$case/show-lib.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-labeled.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora.txt" -RedirectStandardError "$case/show-ora-err.txt"
$oraCat = (Get-Content "$case/show-ora.txt" -Raw).Trim()
if ($libCat -ne $oraCat) { Write-Output "[set-page-labels] FAIL: labeled catalogs differ"; $script:failures++ }
elseif (-not $libCat.Contains('/PageLabels')) { Write-Output "[set-page-labels] FAIL: labels missing"; $script:failures++ }
else { Write-Output "[set-page-labels] labeled catalogs agree" }
foreach ($sub in @(@('pdfdoc', "1:D/1/$([char]0xC9)"), @('utf16', "1:D/1/$([char]0x65E5)$([char]0x672C)"))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/p-$name.pdf=$case/lib-p-$name.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--set-page-labels', $sub[1], '--', "outputs/p-$name.pdf"))
  if ($code -ne 0) { throw "labels/prefix-$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case @('inputs/a.pdf', '--set-page-labels', $sub[1], '--', '--pages', '.', '1', '.', '2', '--', "$case/ora-p-$name.pdf")
  if ($code -ne 0) { throw "labels/prefix-$name`: oracle failed ($code)" }
  Assert-Trio "labels-prefix-$name" "$case/lib-p-$name.pdf" "$case/ora-p-$name.pdf" 2
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-p-$name.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib-p-$name.txt" -RedirectStandardError "$case/show-lib-p-$name-err.txt"
  $libCat = (Get-Content "$case/show-lib-p-$name.txt" -Raw).Trim()
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-p-$name.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-p-$name.txt" -RedirectStandardError "$case/show-ora-p-$name-err.txt"
  $oraCat = (Get-Content "$case/show-ora-p-$name.txt" -Raw).Trim()
  if ($libCat -ne $oraCat) { Write-Output "[labels-prefix-$name] FAIL: prefixed catalogs differ"; $script:failures++ }
  elseif (-not $libCat.Contains('/PageLabels')) { Write-Output "[labels-prefix-$name] FAIL: labels missing"; $script:failures++ }
  else { Write-Output "[labels-prefix-$name] prefixed catalogs agree" }
}
Copy-Item "$case/ora-labeled.pdf" "$case/inputs/labeled.pdf" -Force
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/plain.pdf=$case/lib-plain.pdf", '--', 'inputs/labeled.pdf', '--pages', '.', '1', '.', '2', '--remove-page-labels', '--', 'outputs/plain.pdf')
if ($code -ne 0) { throw "labels/remove: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/labeled.pdf', '--remove-page-labels', '--', '--pages', '.', '1', '.', '2', '--', "$case/ora-plain.pdf")
if ($code -ne 0) { throw "labels/remove: oracle failed ($code)" }
Assert-Trio 'labels/remove' "$case/lib-plain.pdf" "$case/ora-plain.pdf" 2
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-plain.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib-p.txt" -RedirectStandardError "$case/show-lib-p-err.txt"
$libPlain = (Get-Content "$case/show-lib-p.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-plain.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-p.txt" -RedirectStandardError "$case/show-ora-p-err.txt"
$oraPlain = (Get-Content "$case/show-ora-p.txt" -Raw).Trim()
if ($libPlain -ne $oraPlain) { Write-Output "[remove-page-labels] FAIL: unlabeled catalogs differ"; $script:failures++ }
elseif ($libPlain.Contains('/PageLabels')) { Write-Output "[remove-page-labels] FAIL: labels remain"; $script:failures++ }
else { Write-Output "[remove-page-labels] unlabeled catalogs agree" }

# --- case: labels-merge (managed vs oracle source-tree merging) ---
$case = New-Case 'labels-merge'
Copy-Item "$fixtureDir/labels-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/m.pdf=$case/lib-m.pdf", '--', '--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/a.pdf', '2', 'inputs/b.pdf', '1', '--', 'outputs/m.pdf')
if ($code -ne 0) { throw "labels-merge/merge: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/a.pdf', '2', 'inputs/b.pdf', '1', '--', "$case/ora-m.pdf")
if ($code -ne 0) { throw "labels-merge/merge: oracle failed ($code)" }
Assert-Trio 'labels-merge' "$case/lib-m.pdf" "$case/ora-m.pdf" 3
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-m.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib-m.txt" -RedirectStandardError "$case/show-lib-m-err.txt"
$libCat = (Get-Content "$case/show-lib-m.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-m.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-m.txt" -RedirectStandardError "$case/show-ora-m-err.txt"
$oraCat = (Get-Content "$case/show-ora-m.txt" -Raw).Trim()
if ($libCat -ne $oraCat) { Write-Output "[labels-merge] FAIL: merged catalogs differ"; $script:failures++ }
elseif (-not $libCat.Contains('/PageLabels')) { Write-Output "[labels-merge] FAIL: labels missing"; $script:failures++ }
else { Write-Output "[labels-merge] merged catalogs agree" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/s.pdf=$case/lib-s.pdf", '--', 'inputs/a.pdf', '--pages', '.', '2', '--', 'outputs/s.pdf')
if ($code -ne 0) { throw "labels-merge/subset: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--pages', '.', '2', '--', "$case/ora-s.pdf")
if ($code -ne 0) { throw "labels-merge/subset: oracle failed ($code)" }
Assert-Trio 'labels-merge-subset' "$case/lib-s.pdf" "$case/ora-s.pdf" 1
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-s.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib-s.txt" -RedirectStandardError "$case/show-lib-s-err.txt"
$libSub = (Get-Content "$case/show-lib-s.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-s.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-s.txt" -RedirectStandardError "$case/show-ora-s-err.txt"
$oraSub = (Get-Content "$case/show-ora-s.txt" -Raw).Trim()
if ($libSub -ne $oraSub) { Write-Output "[labels-merge-subset] FAIL: subset catalogs differ"; $script:failures++ }
elseif (-not $libSub.Contains('/PageLabels')) { Write-Output "[labels-merge-subset] FAIL: labels missing"; $script:failures++ }
else { Write-Output "[labels-merge-subset] subset catalogs agree" }
Set-Content "$case/whole-job.json" '{"inputFile": "inputs/a.pdf", "outputFile": "outputs/w.pdf", "coalesceContents": true}'
$code = Invoke-Emit $case @('emit-job', '--workdir', $case, '--job', "$case/whole-job.json", '--out', "outputs/w.pdf=$case/lib-w.pdf")
if ($code -ne 0) { throw "labels-merge/whole: emit-job failed ($code)" }
$code = Invoke-Oracle $case @('--coalesce-contents', 'inputs/a.pdf', '--', "$case/ora-w.pdf")
if ($code -ne 0) { throw "labels-merge/whole: oracle failed ($code)" }
Assert-Trio 'labels-merge-whole' "$case/lib-w.pdf" "$case/ora-w.pdf" 2
function Get-LabelTreeNums {
  param([string]$File)
  $cat = & $QpdfExe --show-object=1 $File 2>$null | Out-String
  $m = [regex]::Match($cat, '/PageLabels\s*(\d+) 0 R')
  if (-not $m.Success) { return $null }
  $tree = & $QpdfExe --show-object=$($m.Groups[1].Value) $File 2>$null | Out-String
  $n = [regex]::Match($tree, '/Nums\s*\[(.*)\]')
  if (-not $n.Success) { return '' }
  return $n.Groups[1].Value.Trim()
}
$libNums = Get-LabelTreeNums "$case/lib-w.pdf"
$oraNums = Get-LabelTreeNums "$case/ora-w.pdf"
if ($null -eq $libNums -or $null -eq $oraNums) { Write-Output "[labels-merge-whole] FAIL: labels missing"; $script:failures++ }
elseif ($libNums -ne $oraNums) { Write-Output "[labels-merge-whole] FAIL: verbatim number trees differ"; $script:failures++ }
else { Write-Output "[labels-merge-whole] whole-file number trees agree" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/sp-1.pdf=$case/lib-sp-1.pdf", '--out', "outputs/sp-2.pdf=$case/lib-sp-2.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--split-pages', '--', 'outputs/sp.pdf')
if ($code -ne 0) { throw "labels-merge/split: emit failed ($code)" }
$code = Invoke-Oracle $case @('--split-pages', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-sp.pdf")
if ($code -ne 0) { throw "labels-merge/split: oracle failed ($code)" }
Assert-Trio 'labels-merge-split-1' "$case/lib-sp-1.pdf" "$case/ora-sp-1.pdf" 1
Assert-Trio 'labels-merge-split-2' "$case/lib-sp-2.pdf" "$case/ora-sp-2.pdf" 1
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-sp-1.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib-sp1.txt" -RedirectStandardError "$case/show-lib-sp1-err.txt"
$libSp1 = (Get-Content "$case/show-lib-sp1.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-sp-1.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora-sp1.txt" -RedirectStandardError "$case/show-ora-sp1-err.txt"
$oraSp1 = (Get-Content "$case/show-ora-sp1.txt" -Raw).Trim()
if ($libSp1 -ne $oraSp1) { Write-Output "[labels-merge-split] FAIL: split catalogs differ"; $script:failures++ }
elseif (-not $libSp1.Contains('/PageLabels')) { Write-Output "[labels-merge-split] FAIL: labels missing"; $script:failures++ }
else { Write-Output "[labels-merge-split] split catalogs agree" }

# --- case: forms (generate + flatten) ---
$case = New-Case 'forms'
Copy-Item "$fixtureDir/forms-1page.pdf" "$case/inputs/f.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/f.pdf', '--pages', '.', '1', '--generate-appearances', '--flatten-annotations=all', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "forms: emit failed ($code)" }
$code = Invoke-Oracle $case @('--generate-appearances', '--flatten-annotations=all', 'inputs/f.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "forms: oracle failed ($code)" }
Assert-Trio 'forms' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/Annots')) { Write-Output "[forms] FAIL: /Annots remains in $($pair[0]) output"; $script:failures++ }
  $decoded = Get-DecodedText $pair[1]
  foreach ($needle in @('(Hello)', '(Stale)', '/Tx BMC', '1 5.2 Td', '/Fxo1 Do')) {
    if (-not $decoded.Contains($needle)) { Write-Output "[forms] FAIL: '$needle' missing from decoded $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[forms] appearance assertions done"

# --- case: overlay ---
$case = New-Case 'overlay'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--overlay', 'inputs/b.pdf', '--', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "overlay: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--overlay', 'inputs/b.pdf', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "overlay: oracle failed ($code)" }
Assert-Trio 'overlay' "$case/lib.pdf" "$case/ora.pdf" 2

# --- case: overlay-ranges (managed vs oracle overlay ranges) ---
$case = New-Case 'overlay-ranges'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/b.pdf"
foreach ($sub in @(
  @('from-desc', @('--from=2-1'), @('(Second)'), @('(First)')),
  @('from-rel', @('--from=r1', '--to=1'), @('(Second)'), @('(First)')),
  @('from-excl', @('--from=1-2,x2'), @('(First)'), @('(Second)')),
  @('from-odd', @('--from=1-2:odd'), @('(First)'), @('(Second)'))
)) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', 'inputs/a.pdf', '--overlay', 'inputs/b.pdf') + $sub[1] + @('--', '--', "outputs/$name.pdf"))
  if ($code -ne 0) { throw "overlay-ranges/$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case (@('inputs/a.pdf', '--overlay', 'inputs/b.pdf') + $sub[1] + @('--', "$case/ora-$name.pdf"))
  if ($code -ne 0) { throw "overlay-ranges/$name`: oracle failed ($code)" }
  Assert-Trio "overlay-ranges-$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 1
  foreach ($pair in @(('lib', "$case/lib-$name.pdf"), ('ora', "$case/ora-$name.pdf"))) {
    $decoded = Get-DecodedText $pair[1]
    if (-not $decoded.Contains($sub[2])) { Write-Output "[overlay-ranges/$name] FAIL: '$($sub[2])' missing from decoded $($pair[0]) output"; $script:failures++ }
    if ($decoded.Contains($sub[3])) { Write-Output "[overlay-ranges/$name] FAIL: '$($sub[3])' remains in decoded $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[overlay-ranges/from] placed-marker assertions done"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/und.pdf=$case/lib-und.pdf", '--', 'inputs/a.pdf', '--underlay', 'inputs/b.pdf', '--from=2-1', '--', '--', 'outputs/und.pdf')
if ($code -ne 0) { throw "overlay-ranges/und: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--underlay', 'inputs/b.pdf', '--from=2-1', '--', "$case/ora-und.pdf")
if ($code -ne 0) { throw "overlay-ranges/und: oracle failed ($code)" }
Assert-Trio 'overlay-ranges-und' "$case/lib-und.pdf" "$case/ora-und.pdf" 1
foreach ($pair in @(('lib', "$case/lib-und.pdf"), ('ora', "$case/ora-und.pdf"))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains('(Second)')) { Write-Output "[overlay-ranges/und] FAIL: '(Second)' missing from decoded $($pair[0]) output"; $script:failures++ }
  if ($decoded.Contains('(First)')) { Write-Output "[overlay-ranges/und] FAIL: '(First)' remains in decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[overlay-ranges/und] placed-marker assertions done"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/toodd.pdf=$case/lib-toodd.pdf", '--', 'inputs/b.pdf', '--overlay', 'inputs/a.pdf', '--to=1-2:odd', '--', '--', 'outputs/toodd.pdf')
if ($code -ne 0) { throw "overlay-ranges/toodd: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/b.pdf', '--overlay', 'inputs/a.pdf', '--to=1-2:odd', '--', "$case/ora-toodd.pdf")
if ($code -ne 0) { throw "overlay-ranges/toodd: oracle failed ($code)" }
Assert-Trio 'overlay-ranges-toodd' "$case/lib-toodd.pdf" "$case/ora-toodd.pdf" 2
foreach ($pair in @(('lib', "$case/lib-toodd.pdf"), ('ora', "$case/ora-toodd.pdf"))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains('(Hello)')) { Write-Output "[overlay-ranges/toodd] FAIL: '(Hello)' missing from decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[overlay-ranges/toodd] stamp assertions done"

# --- case: overlay-annots (managed vs oracle annotation copying) ---
$case = New-Case 'overlay-annots'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/overlay-annot-1page.pdf" "$case/inputs/o.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--overlay', 'inputs/o.pdf', '--', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "overlay-annots: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--overlay', 'inputs/o.pdf', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "overlay-annots: oracle failed ($code)" }
Assert-Trio 'overlay-annots' "$case/lib.pdf" "$case/ora.pdf" 1
function Get-OverlayAnnotSpecs {
  param([string]$File)
  $pages = & $QpdfExe --show-pages $File 2>$null | Out-String
  $m = [regex]::Match($pages, 'page 1:\s*(\d+) 0 R')
  if (-not $m.Success) { throw "overlay-annots: no page 1 in $File" }
  $pageDump = & $QpdfExe --show-object=$($m.Groups[1].Value) $File 2>$null | Out-String
  $am = [regex]::Match($pageDump, '/Annots\s*\[([^\]]*)\]')
  if (-not $am.Success) { throw "overlay-annots: no /Annots in $File page" }
  $refs = [regex]::Matches($am.Groups[1].Value, '(\d+) 0 R') | ForEach-Object { $_.Groups[1].Value }
  $specs = @()
  foreach ($ref in $refs) {
    $dump = & $QpdfExe --show-object=$ref $File 2>$null | Out-String
    $sub = ([regex]::Match($dump, '/Subtype\s*/(\w+)').Groups[1].Value)
    $con = ([regex]::Match($dump, '/Contents\s*(\((?:[^()\\]|\\.)*\)|<[0-9A-Fa-f]*>)').Groups[1].Value)
    $rect = @()
    $rm = [regex]::Match($dump, '/Rect\s*\[([^\]]*)\]')
    if ($rm.Success) { $rect = @($rm.Groups[1].Value.Trim() -split '\s+' | Where-Object { $_ -ne '' } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) }) }
    $matrix = $null
    $hasP = $dump -match '/P(\s|$)'
    $destNull = $false
    $apm = [regex]::Match($dump, '/AP\s*<<\s*/N\s*(\d+) 0 R')
    if ($apm.Success) {
      $sdump = & $QpdfExe --show-object=$($apm.Groups[1].Value) $File 2>$null | Out-String
      $mm = [regex]::Match($sdump, '/Matrix\s*\[([^\]]*)\]')
      if ($mm.Success) { $matrix = @($mm.Groups[1].Value.Trim() -split '\s+' | Where-Object { $_ -ne '' } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) }) }
    }
    $dm = [regex]::Match($dump, '/D\s*\[\s*(\d+) 0 R')
    if ($dm.Success) {
      $tdump = & $QpdfExe --show-object=$($dm.Groups[1].Value) $File 2>$null | Out-String
      if ($tdump.Trim() -eq 'null') { $destNull = $true }
    }
    $specs += @{ Subtype = $sub; Contents = $con; Rect = $rect; Matrix = $matrix; HasP = $hasP; DestNull = $destNull }
  }
  return $specs
}
$libSpecs = Get-OverlayAnnotSpecs "$case/lib.pdf"
$oraSpecs = Get-OverlayAnnotSpecs "$case/ora.pdf"
if ($libSpecs.Count -ne 3 -or $oraSpecs.Count -ne 3) { Write-Output "[overlay-annots] FAIL: annot counts lib=$($libSpecs.Count) ora=$($oraSpecs.Count), expected 3/3"; $script:failures++ }
else {
  for ($i = 0; $i -lt 3; $i++) {
    $l = $libSpecs[$i]
    $o = $oraSpecs[$i]
    if ($l.Subtype -ne $o.Subtype) { Write-Output "[overlay-annots/$i] FAIL: subtype lib=$($l.Subtype) ora=$($o.Subtype)"; $script:failures++ }
    if ($l.Contents -ne $o.Contents) { Write-Output "[overlay-annots/$i] FAIL: contents lib=$($l.Contents) ora=$($o.Contents)"; $script:failures++ }
    foreach ($k in @('Rect', 'Matrix')) {
      $lv = $l[$k]
      $ov = $o[$k]
      if (($null -eq $lv) -ne ($null -eq $ov)) { Write-Output "[overlay-annots/$i] FAIL: $k presence differs"; $script:failures++ }
      elseif ($null -ne $lv) {
        if ($lv.Count -ne $ov.Count) { Write-Output "[overlay-annots/$i] FAIL: $k length"; $script:failures++ }
        else { for ($j = 0; $j -lt $lv.Count; $j++) { if ([Math]::Abs($lv[$j] - $ov[$j]) -gt 1e-6) { Write-Output "[overlay-annots/$i] FAIL: $k[$j] lib=$($lv[$j]) ora=$($ov[$j])"; $script:failures++ } } }
      }
    }
    if ($l.HasP -or $o.HasP) { Write-Output "[overlay-annots/$i] FAIL: /P present lib=$($l.HasP) ora=$($o.HasP)"; $script:failures++ }
  }
  if ($libSpecs[1].DestNull -and $oraSpecs[1].DestNull) { Write-Output "[overlay-annots] link-destination null agreement" }
  else { Write-Output "[overlay-annots] FAIL: link-destination null agreement lib=$($libSpecs[1].DestNull) ora=$($oraSpecs[1].DestNull)"; $script:failures++ }
}
Write-Output "[overlay-annots] annotation assertions done"

# --- case: writer-controls (managed vs oracle header, identifier, and stream endings) ---
$case = New-Case 'writer-controls'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
foreach ($sub in @(('min', '--min-version=1.7', '%PDF-1.7'), ('force', '--force-version=1.1', '%PDF-1.1'))) {
  $code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/$($sub[0]).pdf=$case/lib-$($sub[0]).pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', $sub[1], '--', "outputs/$($sub[0]).pdf")
  if ($code -ne 0) { throw "writer-controls/$($sub[0]): emit failed ($code)" }
  $code = Invoke-Oracle $case @($sub[1], 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-$($sub[0]).pdf")
  if ($code -ne 0) { throw "writer-controls/$($sub[0]): oracle failed ($code)" }
  Assert-Trio "writer-controls/$($sub[0])" "$case/lib-$($sub[0]).pdf" "$case/ora-$($sub[0]).pdf" 1
  $libHead = Get-Content "$case/lib-$($sub[0]).pdf" -TotalCount 1
  $oraHead = Get-Content "$case/ora-$($sub[0]).pdf" -TotalCount 1
  if ($libHead -ne $sub[2] -or $oraHead -ne $sub[2]) { Write-Output "[writer-versions] FAIL: headers lib=[$libHead] ora=[$oraHead], expected [$($sub[2])]"; $script:failures++ }
  else { Write-Output "[writer-versions] headers agree ($($sub[2]))" }
}
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/ext.pdf=$case/lib-ext.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--force-version=1.7.8', '--', 'outputs/ext.pdf')
if ($code -ne 0) { throw "writer-controls/ext: emit failed ($code)" }
$code = Invoke-Oracle $case @('--force-version=1.7.8', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-ext.pdf")
if ($code -ne 0) { throw "writer-controls/ext: oracle failed ($code)" }
Assert-Trio 'writer-controls/ext' "$case/lib-ext.pdf" "$case/ora-ext.pdf" 1
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/lib-ext.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-lib.txt" -RedirectStandardError "$case/show-lib-err.txt"
$libCat = (Get-Content "$case/show-lib.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=1', "$case/ora-ext.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/show-ora.txt" -RedirectStandardError "$case/show-ora-err.txt"
$oraCat = (Get-Content "$case/show-ora.txt" -Raw).Trim()
if ($libCat -ne $oraCat) { Write-Output "[writer-versions] FAIL: extension catalogs differ"; $script:failures++ }
elseif (-not $libCat.Contains('/ExtensionLevel 8')) { Write-Output "[writer-versions] FAIL: extension level missing"; $script:failures++ }
else { Write-Output "[writer-versions] extension catalogs agree (/ExtensionLevel 8)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/static.pdf=$case/lib-static.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--static-id', '--', 'outputs/static.pdf')
if ($code -ne 0) { throw "writer-controls/static: emit failed ($code)" }
$code = Invoke-Oracle $case @('--static-id', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-static.pdf")
if ($code -ne 0) { throw "writer-controls/static: oracle failed ($code)" }
Assert-Trio 'writer-controls/static' "$case/lib-static.pdf" "$case/ora-static.pdf" 1
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=trailer', "$case/lib-static.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/trailer-lib.txt" -RedirectStandardError "$case/trailer-lib-err.txt"
$libTrailer = (Get-Content "$case/trailer-lib.txt" -Raw).Trim()
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-object=trailer', "$case/ora-static.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/trailer-ora.txt" -RedirectStandardError "$case/trailer-ora-err.txt"
$oraTrailer = (Get-Content "$case/trailer-ora.txt" -Raw).Trim()
if ($libTrailer -ne $oraTrailer) { Write-Output "[writer-ids] FAIL: static trailers differ"; $script:failures++ }
elseif (-not $libTrailer.Contains('31415926535897932384626433832795')) { Write-Output "[writer-ids] FAIL: test constant missing"; $script:failures++ }
else { Write-Output "[writer-ids] static trailers agree (test constant)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/det.pdf=$case/lib-det.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--deterministic-id', '--', 'outputs/det.pdf')
if ($code -ne 0) { throw "writer-controls/det: emit failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/plain.pdf=$case/lib-plain.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--', 'outputs/plain.pdf')
if ($code -ne 0) { throw "writer-controls/plain: emit failed ($code)" }
$lh = (Get-FileHash "$case/lib-det.pdf" -Algorithm SHA256).Hash
$ph = (Get-FileHash "$case/lib-plain.pdf" -Algorithm SHA256).Hash
if ($lh -ne $ph) { Write-Output "[writer-ids] FAIL: deterministic flag changes default bytes"; $script:failures++ }
else { Write-Output "[writer-ids] deterministic output matches default bytes" }
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--check', "$case/lib-det.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qc.txt" -RedirectStandardError "$env:TEMP/qce.txt"
if ($p.ExitCode -ne 0) { Write-Output "[writer-ids] FAIL: deterministic output check exit $($p.ExitCode)"; $script:failures++ }
else { Write-Output "[writer-ids] deterministic output validates under oracle check" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/nl.pdf=$case/lib-nl.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--newline-before-endstream', '--', 'outputs/nl.pdf')
if ($code -ne 0) { throw "writer-controls/nl: emit failed ($code)" }
$code = Invoke-Oracle $case @('--newline-before-endstream', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-nl.pdf")
if ($code -ne 0) { throw "writer-controls/nl: oracle failed ($code)" }
Assert-Trio 'writer-controls/nl' "$case/lib-nl.pdf" "$case/ora-nl.pdf" 1
$nlMismatch = $false
foreach ($pair in @(('lib', "$case/lib-nl.pdf"), ('ora', "$case/ora-nl.pdf"))) {
  $bytes = [IO.File]::ReadAllBytes($pair[1])
  $needle = [Text.Encoding]::ASCII.GetBytes('endstream')
  $ends = 0
  $bare = 0
  for ($i = 0; $i + $needle.Length -le $bytes.Length; $i++) {
    $m = $true
    for ($k = 0; $k -lt $needle.Length; $k++) { if ($bytes[$i + $k] -ne $needle[$k]) { $m = $false; break } }
    if ($m) { $ends += 1; if ($i -eq 0 -or $bytes[$i - 1] -ne 10) { $bare += 1 } }
  }
  if ($ends -eq 0 -or $bare -gt 0) { Write-Output "[writer-newlines] FAIL: $($pair[0]) has $ends endstreams, $bare without preceding LF"; $script:failures++; $nlMismatch = $true }
}
if (-not $nlMismatch) { Write-Output "[writer-newlines] stream endings agree (LF before every endstream)" }

# --- case: qdf (editable view with generated object streams) ---
$case = New-Case 'qdf'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--qdf', '--object-streams=generate', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "qdf: emit failed ($code)" }
$code = Invoke-Oracle $case @('--qdf', '--object-streams=generate', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "qdf: oracle failed ($code)" }
Assert-Trio 'qdf' "$case/lib.pdf" "$case/ora.pdf" 2
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('%QDF-1.0', '/Type /ObjStm', '/Type /XRef', '%% Object stream: object')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[qdf] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
$fixQdf = Join-Path (Split-Path $QpdfExe -Parent) 'fix-qdf.exe'
if (Test-Path $fixQdf) {
  $p = Start-Process -FilePath $fixQdf -ArgumentList @("$case/lib.pdf", "$case/fixed.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/fix-stdout.txt" -RedirectStandardError "$case/fix-stderr.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[qdf] FAIL: fix-qdf exit $($p.ExitCode) on library output"; $script:failures++ }
  else {
    $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--check', "$case/fixed.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qc.txt" -RedirectStandardError "$env:TEMP/qce.txt"
    if ($p.ExitCode -ne 0) { Write-Output "[qdf] FAIL: oracle --check exit $($p.ExitCode) on fix-qdf output"; $script:failures++ }
    else { Write-Output "[qdf] fix-qdf round trip ok" }
  }
} else { Write-Output "[qdf] FAIL: fix-qdf.exe missing beside oracle"; $script:failures++ }
Write-Output "[qdf] marker assertions done"

# --- case: qdf-noids (managed vs oracle QDF without original object IDs) ---
$case = New-Case 'qdf-noids'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--qdf', '--no-original-object-ids', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "qdf-noids: emit failed ($code)" }
$code = Invoke-Oracle $case @('--qdf', '--no-original-object-ids', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "qdf-noids: oracle failed ($code)" }
Assert-Trio 'qdf-noids' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('Original object ID')) { Write-Output "[qdf-noids] FAIL: original-ID comment remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('%QDF-1.0')) { Write-Output "[qdf-noids] FAIL: QDF marker missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[qdf-noids] suppression assertions done"

# --- case: fixqdf (managed repair must match oracle fix-qdf byte for byte) ---
$case = New-Case 'fixqdf'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/classic.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--qdf', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "fixqdf setup: classic emit failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/b.pdf=$case/streamed.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--qdf', '--object-streams=generate', '--', 'outputs/b.pdf')
if ($code -ne 0) { throw "fixqdf setup: streamed emit failed ($code)" }
$fixQdf = Join-Path (Split-Path $QpdfExe -Parent) 'fix-qdf.exe'
if (-not (Test-Path $fixQdf)) { throw "fixqdf setup: fix-qdf.exe missing beside oracle" }
foreach ($sub in @(('classic', '(First)', '(First-Longer-Edit)'), ('streamed', '/Helvetica', '/Helvetica-Bold'))) {
  $name, $needleText, $repText = $sub
  $raw = [IO.File]::ReadAllBytes("$case/$name.pdf")
  $needle = [Text.Encoding]::ASCII.GetBytes($needleText)
  $at = -1; $idx = 0
  while ($idx + $needle.Length -le $raw.Length) {
    $m = $true
    for ($k = 0; $k -lt $needle.Length; $k++) { if ($raw[$idx + $k] -ne $needle[$k]) { $m = $false; break } }
    if ($m) { $at = $idx; break }
    $idx++
  }
  if ($at -lt 0) { Write-Output "[fixqdf/$name] FAIL: edit needle missing"; $script:failures++; continue }
  $rep = [Text.Encoding]::ASCII.GetBytes($repText)
  $edited = New-Object byte[] ($raw.Length - $needle.Length + $rep.Length)
  [Buffer]::BlockCopy($raw, 0, $edited, 0, $at)
  [Buffer]::BlockCopy($rep, 0, $edited, $at, $rep.Length)
  [Buffer]::BlockCopy($raw, $at + $needle.Length, $edited, $at + $rep.Length, $raw.Length - $at - $needle.Length)
  [IO.File]::WriteAllBytes("$case/inputs/e-$name.pdf", $edited)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'fix-qdf', '--workdir', $case, '--in', "inputs/e-$name.pdf", '--out', "outputs/f-$name.pdf=$case/lib-$name.pdf") -NoNewWindow -Wait -PassThru
  if ($p.ExitCode -ne 0) { Write-Output "[fixqdf/$name] FAIL: managed fix exit $($p.ExitCode)"; $script:failures++; continue }
  $p = Start-Process -FilePath $fixQdf -ArgumentList @("$case/inputs/e-$name.pdf", "$case/ora-$name.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/fix-$name-out.txt" -RedirectStandardError "$case/fix-$name-err.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[fixqdf/$name] FAIL: oracle fix exit $($p.ExitCode)"; $script:failures++; continue }
  $lh = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes("$case/lib-$name.pdf")))
  $oh = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes("$case/ora-$name.pdf")))
  if ($lh -ne $oh) { Write-Output "[fixqdf/$name] FAIL: managed and oracle repairs differ"; $script:failures++ }
  else { Write-Output "[fixqdf/$name] repairs byte-identical" }
  Assert-Trio "fixqdf/$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 2
}

# --- case: zlibflate (managed companion vs oracle zlib-flate, both directions) ---
$case = New-Case 'zlibflate'
$zfExe = Join-Path (Split-Path $QpdfExe -Parent) 'zlib-flate.exe'
if (-not (Test-Path $zfExe)) { throw "zlibflate setup: zlib-flate.exe missing beside oracle" }
$payload = [Text.Encoding]::ASCII.GetBytes(('The quick brown fox jumps over the lazy dog. ' * 400))
[IO.File]::WriteAllBytes("$case/inputs/plain.bin", $payload)
$binary = New-Object byte[] 4096
for ($i = 0; $i -lt 4096; $i++) { $binary[$i] = [byte]($i % 256) }
[IO.File]::WriteAllBytes("$case/inputs/binary.bin", $binary)
function Invoke-ZlibFlateOracle {
  param([string]$Flags, [string]$InFile, [string]$OutFile, [string]$ErrFile)
  $p = Start-Process -FilePath $zfExe -ArgumentList @($Flags) -NoNewWindow -Wait -PassThru -RedirectStandardInput $InFile -RedirectStandardOutput $OutFile -RedirectStandardError $ErrFile
  return $p.ExitCode
}
function Invoke-ManagedFlate {
  param([string]$In, [string]$OutLogical, [string]$OutFile, [string]$Mode, [string]$Level)
  $list = @($probeDll, 'zlib-flate', '--workdir', $case, '--in', $In, '--out', "$OutLogical=$case/$OutFile", '--mode', $Mode)
  if ($Level -ne "") { $list += @('--level', $Level) }
  $p = Start-Process -FilePath 'dotnet' -ArgumentList $list -NoNewWindow -Wait -PassThru
  return $p.ExitCode
}
foreach ($level in @('1', '9')) {
  $code = Invoke-ManagedFlate "inputs/plain.bin" "outputs/m-$level.bin" "m-$level.bin" 'compress' $level
  if ($code -ne 0) { Write-Output "[zlibflate] FAIL: managed compress=$level exit $code"; $script:failures++; continue }
  $code = Invoke-ZlibFlateOracle "-compress=$level" "$case/inputs/plain.bin" "$case/ora-$level.bin" "$case/ora-$level-err.txt"
  if ($code -ne 0) { Write-Output "[zlibflate] FAIL: oracle compress=$level exit $code"; $script:failures++; continue }
  $code = Invoke-ZlibFlateOracle "-uncompress" "$case/m-$level.bin" "$case/ora-u$level.bin" "$case/ora-u$level-err.txt"
  if ($code -ne 0) { Write-Output "[zlibflate] FAIL: oracle uncompress of managed output exit $code"; $script:failures++; continue }
  if ((Get-FileHash "$case/ora-u$level.bin" -Algorithm SHA256).Hash -ne (Get-FileHash "$case/inputs/plain.bin" -Algorithm SHA256).Hash) { Write-Output "[zlibflate] FAIL: oracle round trip of managed compress=$level differs"; $script:failures++ }
  else { Write-Output "[zlibflate] managed compress=$level round-trips through oracle" }
}
$code = Invoke-ZlibFlateOracle "-compress=9" "$case/inputs/plain.bin" "$case/inputs/ora-c9.bin" "$case/ora-c9-err.txt"
if ($code -ne 0) { Write-Output "[zlibflate] FAIL: oracle compress setup exit $code"; $script:failures++ }
else {
  $code = Invoke-ManagedFlate "inputs/ora-c9.bin" "outputs/u9.bin" "m-u9.bin" 'uncompress' ""
  if ($code -ne 0) { Write-Output "[zlibflate] FAIL: managed uncompress of oracle output exit $code"; $script:failures++ }
  elseif ((Get-FileHash "$case/m-u9.bin" -Algorithm SHA256).Hash -ne (Get-FileHash "$case/inputs/plain.bin" -Algorithm SHA256).Hash) { Write-Output "[zlibflate] FAIL: managed uncompress of oracle output differs"; $script:failures++ }
  else { Write-Output "[zlibflate] managed uncompress matches oracle output" }
}
$l1 = (Get-Item "$case/m-1.bin").Length
$l9 = (Get-Item "$case/m-9.bin").Length
if ($l9 -gt $l1) { Write-Output "[zlibflate] FAIL: level 9 ($l9) larger than level 1 ($l1)"; $script:failures++ }
else { Write-Output "[zlibflate] level ordering ok (1=$l1, 9=$l9)" }
$code = Invoke-ManagedFlate "inputs/binary.bin" "outputs/bin-c9.bin" "m-bin-c9.bin" 'compress' '9'
if ($code -ne 0) { Write-Output "[zlibflate] FAIL: managed binary compress exit $code"; $script:failures++ }
else {
  $code = Invoke-ZlibFlateOracle "-uncompress" "$case/m-bin-c9.bin" "$case/ora-bin.bin" "$case/ora-bin-err.txt"
  if ($code -ne 0) { Write-Output "[zlibflate] FAIL: oracle uncompress of managed binary exit $code"; $script:failures++ }
  elseif ((Get-FileHash "$case/ora-bin.bin" -Algorithm SHA256).Hash -ne (Get-FileHash "$case/inputs/binary.bin" -Algorithm SHA256).Hash) { Write-Output "[zlibflate] FAIL: managed binary round trip differs"; $script:failures++ }
  else { Write-Output "[zlibflate] managed binary compress round-trips through oracle" }
}
$code = Invoke-ManagedFlate "inputs/plain.bin" "outputs/bad.bin" "m-bad.bin" 'uncompress' ""
if ($code -ne 2) { Write-Output "[zlibflate] FAIL: managed uncompress of plain input exit $code, expected 2"; $script:failures++ }
else { Write-Output "[zlibflate] corrupt input fails explicitly" }
$code = Invoke-ZlibFlateOracle "-uncompress" "$case/inputs/plain.bin" "$case/ora-bad.bin" "$case/ora-bad-err.txt"
if ($code -ne 2) { Write-Output "[zlibflate] FAIL: oracle uncompress of plain input exit $code, expected 2"; $script:failures++ }
else { Write-Output "[zlibflate] oracle corrupt parity ok" }
$raw = [IO.File]::ReadAllBytes("$case/inputs/ora-c9.bin")
[IO.File]::WriteAllBytes("$case/inputs/trunc.bin", $raw[0..([int]($raw.Length / 2) - 1)])
$code = Invoke-ZlibFlateOracle "-uncompress" "$case/inputs/trunc.bin" "$case/ora-trunc.bin" "$case/ora-trunc-err.txt"
if ($code -ne 3) { Write-Output "[zlibflate] FAIL: oracle truncated exit $code, expected 3"; $script:failures++ }
else { Write-Output "[zlibflate] oracle truncated warns (exit 3) as documented" }
$code = Invoke-ManagedFlate "inputs/trunc.bin" "outputs/trunc.bin" "m-trunc.bin" 'uncompress' ""
if ($code -ne 2) { Write-Output "[zlibflate] FAIL: managed truncated exit $code, expected 2"; $script:failures++ }
else { Write-Output "[zlibflate] truncated input fails strictly as documented" }

# --- case: errors (managed vs oracle exit-class parity) ---
$case = New-Case 'errors'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
foreach ($sub in @(
  @('page-range', @('emit', '--workdir', $case, '--out', "outputs/x.pdf=$case/lib-x.pdf", '--', 'inputs/a.pdf', '--pages', '.', '9', '--', 'outputs/x.pdf'), @('inputs/a.pdf', '--pages', '.', '9', '--', "$case/ora-x.pdf")),
  @('missing-input', @('emit', '--workdir', $case, '--out', "outputs/y.pdf=$case/lib-y.pdf", '--', 'inputs/missing.pdf', '--pages', '.', '1', '--', 'outputs/y.pdf'), @('inputs/missing.pdf', '--pages', '.', '1', '--', "$case/ora-y.pdf")),
  @('unknown-flag', @('emit', '--workdir', $case, '--out', "outputs/z.pdf=$case/lib-z.pdf", '--', 'inputs/b.pdf', '--pages', '.', '1', '--bogus-flag', '--', 'outputs/z.pdf'), @('--bogus-flag', 'inputs/b.pdf', '--pages', '.', '1', '--', "$case/ora-z.pdf"))
)) {
  $m = Invoke-Emit $case $sub[1]
  $o = Invoke-Oracle $case $sub[2]
  if ($m -ne $o) { Write-Output "[errors/$($sub[0])] FAIL: exit mismatch managed=$m oracle=$o"; $script:failures++ }
  elseif ($m -eq 0) { Write-Output "[errors/$($sub[0])] FAIL: expected nonzero exits, got 0/0"; $script:failures++ }
  else { Write-Output "[errors/$($sub[0])] exit parity ($m/$o)" }
}

# --- case: job-file (managed vs oracle job JSON file execution) ---
$case = New-Case 'job-file'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf" -Force
Set-Content "$case/inputs/job.json" '{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}]}'
Copy-Item "$case/inputs/job.json" "$case/job.json" -Force
New-Item -ItemType Directory -Path "$case/outputs" -Force | Out-Null
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', '--job-json-file', 'inputs/job.json')
if ($code -ne 0) { throw "job-file: emit failed ($code)" }
$code = Invoke-Oracle $case @('--job-json-file=job.json')
if ($code -ne 0) { throw "job-file: oracle failed ($code)" }
Copy-Item "$case/outputs/a.pdf" "$case/ora.pdf" -Force
Assert-Trio 'job-file' "$case/lib.pdf" "$case/ora.pdf" 1

# --- case: replace (managed job vs oracle argv in-place replacement) ---
$case = New-Case 'replace'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/o.pdf"
Set-Content "$case/job.json" '{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "replaceInput": true}'
$code = Invoke-Emit $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "inputs/a.pdf=$case/lib.pdf")
if ($code -ne 0) { throw "replace: emit-job failed ($code)" }
$code = Invoke-Oracle $case @('--replace-input', 'inputs/o.pdf', '--pages', '.', '1', '--')
if ($code -ne 0) { throw "replace: oracle failed ($code)" }
Assert-Trio 'replace' "$case/lib.pdf" "$case/inputs/o.pdf" 1
if ((Get-ChildItem -Path $case -Recurse -Filter *.bak).Count -ne 0) { Write-Output "[replace] FAIL: backup files created"; $script:failures++ }
else { Write-Output "[replace] in-place replacement agrees with no backups" }

# --- case: recovery (managed vs oracle damaged-input behavior) ---
$case = New-Case 'recovery'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf" -Force
$rawBytes = [IO.File]::ReadAllBytes("$case/inputs/b.pdf")
$needleBytes = [Text.Encoding]::ASCII.GetBytes("startxref`n398")
$at = -1
for ($i = 0; $i + $needleBytes.Length -le $rawBytes.Length; $i++) {
  $found = $true
  for ($k = 0; $k -lt $needleBytes.Length; $k++) { if ($rawBytes[$i + $k] -ne $needleBytes[$k]) { $found = $false; break } }
  if ($found) { $at = $i; break }
}
if ($at -lt 0) { throw "recovery setup: startxref needle missing" }
[Text.Encoding]::ASCII.GetBytes("999").CopyTo($rawBytes, $at + 10)
[IO.File]::WriteAllBytes("$case/inputs/b.pdf", $rawBytes)
function Invoke-RecoveryExit {
  param([string]$Workdir, [string[]]$EmitArgs, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll) + $EmitArgs) -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/recovery-$Tag.txt"
  $line = Select-String -Path "$Workdir/recovery-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "recovery: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
$m = Invoke-RecoveryExit $case @('emit', '--workdir', $case, '--out', "outputs/r.pdf=$case/lib-r.pdf", '--', 'inputs/b.pdf', '--pages', '.', '1', '--', 'outputs/r.pdf') 'plain'
$o = Invoke-Oracle $case @('inputs/b.pdf', '--pages', '.', '1', '--', "$case/ora-r.pdf")
if ($m -ne 3 -or $o -ne 3) { Write-Output "[recovery/plain] FAIL: exits managed=$m oracle=$o, expected 3/3"; $script:failures++ }
else {
  Write-Output "[recovery/plain] exit parity (3/3)"
  $libN = Get-OracleNpages "$case/lib-r.pdf"
  $oraN = Get-OracleNpages "$case/ora-r.pdf"
  $libP = Get-PigNpages "$case/lib-r.pdf"
  $oraP = Get-PigNpages "$case/ora-r.pdf"
  if ($libN -ne 1 -or $oraN -ne 1 -or $libP -ne 1 -or $oraP -ne 1) { Write-Output "[recovery/plain] FAIL: page counts lib=$libN/$libP ora=$oraN/$oraP, expected 1"; $script:failures++ }
  else { Write-Output "[recovery/plain] recovered page counts agree (1)" }
}
$m = Invoke-RecoveryExit $case @('emit', '--workdir', $case, '--out', "outputs/s.pdf=$case/lib-s.pdf", '--', 'inputs/b.pdf', '--suppress-recovery', '--pages', '.', '1', '--', 'outputs/s.pdf') 'suppress'
$o = Invoke-Oracle $case @('--suppress-recovery', 'inputs/b.pdf', '--pages', '.', '1', '--', "$case/ora-s.pdf")
if ($m -ne 2 -or $o -ne 2) { Write-Output "[recovery/suppress] FAIL: exits managed=$m oracle=$o, expected 2/2"; $script:failures++ }
else {
  Write-Output "[recovery/suppress] exit parity (2/2)"
  if ((Test-Path "$case/lib-s.pdf") -or (Test-Path "$case/ora-s.pdf")) { Write-Output "[recovery/suppress] FAIL: output published despite strict failure"; $script:failures++ }
  else { Write-Output "[recovery/suppress] nothing published on either side" }
}

# --- case: linearized (managed --check vs oracle --check) ---
$case = New-Case 'linearized'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--linearize', '--static-id', 'inputs/a.pdf', '--', "$case/inputs/lin.pdf")
if ($code -ne 0) { throw "linearized setup: oracle linearize failed ($code)" }
function Invoke-ManagedCheck {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--check') -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "linearized: managed check produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
function Invoke-OracleCheck {
  param([string]$File)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--check', $File) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qc.txt" -RedirectStandardError "$env:TEMP/qce.txt"
  return $p.ExitCode
}
function Edit-FirstBytes {
  param([string]$File, [string]$Needle, [string]$Replacement)
  if ($Needle.Length -ne $Replacement.Length) { throw "linearized setup: edit must preserve length" }
  $raw = [IO.File]::ReadAllBytes($File)
  $nb = [Text.Encoding]::ASCII.GetBytes($Needle)
  $rb = [Text.Encoding]::ASCII.GetBytes($Replacement)
  $at = -1; $idx = 0
  while ($idx + $nb.Length -le $raw.Length) {
    $m = $true
    for ($k = 0; $k -lt $nb.Length; $k++) { if ($raw[$idx + $k] -ne $nb[$k]) { $m = $false; break } }
    if ($m) { $at = $idx; break }
    $idx++
  }
  if ($at -lt 0) { throw "linearized setup: needle missing ($Needle)" }
  [Buffer]::BlockCopy($rb, 0, $raw, $at, $rb.Length)
  [IO.File]::WriteAllBytes($File, $raw)
}
$code = Invoke-ManagedCheck $case "inputs/lin.pdf" "valid"
$ora = Invoke-OracleCheck "$case/inputs/lin.pdf"
if ($code -ne 0 -or $ora -ne 0) { Write-Output "[linearized] FAIL: valid exits managed=$code oracle=$ora, expected 0/0"; $script:failures++ }
else {
  Write-Output "[linearized] valid file exits 0/0"
  if ((Get-PigNpages "$case/inputs/lin.pdf") -ne 2) { Write-Output "[linearized] FAIL: independent page count"; $script:failures++ }
}
Copy-Item "$case/inputs/lin.pdf" "$case/inputs/badN.pdf"
Edit-FirstBytes "$case/inputs/badN.pdf" "/N 2" "/N 3"
$code = Invoke-ManagedCheck $case "inputs/badN.pdf" "badn"
$ora = Invoke-OracleCheck "$case/inputs/badN.pdf"
if ($code -ne 2 -or $ora -ne 3) { Write-Output "[linearized] FAIL: badN exits managed=$code oracle=$ora, expected 2/3"; $script:failures++ }
else { Write-Output "[linearized] page-count mismatch fails on both sides (2/3)" }
Copy-Item "$case/inputs/lin.pdf" "$case/inputs/badH.pdf"
Edit-FirstBytes "$case/inputs/badH.pdf" "[ 550 127 ]" "[ 550 999 ]"
$code = Invoke-ManagedCheck $case "inputs/badH.pdf" "badh"
$ora = Invoke-OracleCheck "$case/inputs/badH.pdf"
if ($code -ne 2 -or $ora -ne 3) { Write-Output "[linearized] FAIL: badH exits managed=$code oracle=$ora, expected 2/3"; $script:failures++ }
else { Write-Output "[linearized] hint-length mismatch fails on both sides (2/3)" }


# --- case: identity (managed vs oracle identity requests) ---
$case = New-Case 'identity'
New-Item -ItemType Directory -Path "$case/inputs" -Force | Out-Null
function Invoke-ManagedIdentity {
  param([string]$Workdir, [string]$Flag, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $Flag) -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "identity: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
function Invoke-OracleIdentity {
  param([string]$Workdir, [string]$Flag, [string]$Tag)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @($Flag) -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/oracle-$Tag.txt" -RedirectStandardError "$Workdir/oracle-$Tag-err.txt"
  return $p.ExitCode
}
foreach ($sub in @(@('--version', '12.4.2', 'version'), @('--copyright', 'Copyright', 'copyright'), @('--help', 'qpdf', 'help'), @('--job-json-help', 'inputFile', 'job-json-help'), @('--json-help', 'acroform', 'json-help'), @('--completion-bash', 'completion', 'completion-bash'), @('--completion-zsh', 'compdef', 'completion-zsh'))) {
  $m = Invoke-ManagedIdentity $case $sub[0] "id-$($sub[2])"
  $o = Invoke-OracleIdentity $case $sub[0] "id-$($sub[2])"
  if ($m -ne 0 -or $o -ne 0) { Write-Output "[identity/$($sub[2])] FAIL: exits managed=$m oracle=$o, expected 0/0"; $script:failures++; continue }
  $mText = Get-Content "$case/managed-id-$($sub[2]).txt" -Raw
  $oText = Get-Content "$case/oracle-id-$($sub[2]).txt" -Raw
  if (-not $mText.Contains($sub[1]) -or -not $oText.Contains($sub[1])) { Write-Output "[identity/$($sub[2])] FAIL: marker [$($sub[1])] missing on a side"; $script:failures++ }
  else { Write-Output "[identity/$($sub[2])] exits 0/0 with shared marker" }
}

# --- case: hints (managed vs oracle file-handle hints) ---
$case = New-Case 'hints'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/plain.pdf=$case/lib-plain.pdf", '--', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', 'outputs/plain.pdf')
if ($code -ne 0) { throw "hints: baseline emit failed ($code)" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/hinted.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--keep-files-open=y', '--keep-files-open-threshold=200', '--', 'outputs/hinted.pdf')
if ($code -ne 0) { throw "hints: emit failed ($code)" }
$code = Invoke-Oracle $case @('--keep-files-open=y', '--keep-files-open-threshold=200', '--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "hints: oracle failed ($code)" }
Assert-Trio 'hints' "$case/lib.pdf" "$case/ora.pdf" 3
$lh = (Get-FileHash "$case/lib.pdf" -Algorithm SHA256).Hash
$ph = (Get-FileHash "$case/lib-plain.pdf" -Algorithm SHA256).Hash
if ($lh -ne $ph) { Write-Output "[hints] FAIL: hinted bytes differ from unhinted bytes"; $script:failures++ }
else { Write-Output "[hints] hinted output matches unhinted bytes" }

# --- case: global (managed vs oracle --global limit blocks) ---
$case = New-Case 'global'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
foreach ($sub in @(@('filters', @('--max-stream-filters=25')), @('nesting', @('--parser-max-nesting=100')), @('nesting499', @('--parser-max-nesting=499')), @('containers', @('--parser-max-container-size=1000', '--parser-max-container-size-damaged=1000')))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', '--global') + $sub[1] + @('--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "outputs/$name.pdf"))
  if ($code -ne 0) { throw "global/$name`: emit failed ($code)" }
  $code = Invoke-Oracle $case (@('--global') + $sub[1] + @('--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-$name.pdf"))
  if ($code -ne 0) { throw "global/$name`: oracle failed ($code)" }
  Assert-Trio "global-$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 2
}
Write-Output "[global] block parity done"

# --- case: warnings (warning exit-code parity) ---
$case = New-Case 'warnings'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Edit-FirstBytes "$case/inputs/a.pdf" "startxref`n398" "startxref`n999"
function Invoke-ManagedExit {
  param([string]$Workdir, [string[]]$EmitArgs, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll) + $EmitArgs) -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "warnings: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
foreach ($sub in @(
  @('plain', @(), 3),
  @('exit-0', @('--warning-exit-0'), 0),
  @('no-warn', @('--no-warn'), 3)
)) {
  $name = $sub[0]
  $m = Invoke-ManagedExit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1') + $sub[1] + @('--', "outputs/$name.pdf")) "warn-$name"
  $o = Invoke-Oracle $case ((@() + $sub[1]) + @('inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-$name.pdf"))
  if ($m -ne $sub[2] -or $o -ne $sub[2]) { Write-Output "[warnings/$name] FAIL: exits managed=$m oracle=$o, expected $($sub[2])"; $script:failures++ }
  else { Write-Output "[warnings/$name] exit parity ($m/$o)" }
}

# --- case: inline (managed vs oracle inline-image externalization) ---
$case = New-Case 'inline'
Copy-Item "$fixtureDir/inline-1page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--externalize-inline-images', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "inline: emit failed ($code)" }
$code = Invoke-Oracle $case @('--externalize-inline-images', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "inline: oracle failed ($code)" }
Assert-Trio 'inline' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains('/IIm1 Do')) { Write-Output "[inline] FAIL: '/IIm1 Do' missing from decoded $($pair[0]) output"; $script:failures++ }
  if ($decoded.Contains(' BI ')) { Write-Output "[inline] FAIL: inline image remains in decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[inline] externalization assertions done"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/k.pdf=$case/lib-keep.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--externalize-inline-images', '--ii-min-bytes=100000', '--', 'outputs/k.pdf')
if ($code -ne 0) { throw "inline/keep: emit failed ($code)" }
$code = Invoke-Oracle $case @('--externalize-inline-images', '--ii-min-bytes=100000', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-keep.pdf")
if ($code -ne 0) { throw "inline/keep: oracle failed ($code)" }
Assert-Trio 'inline-keep' "$case/lib-keep.pdf" "$case/ora-keep.pdf" 1
foreach ($pair in @(('lib-keep', "$case/lib-keep.pdf"), ('ora-keep', "$case/ora-keep.pdf"))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains(' BI ')) { Write-Output "[inline/keep] FAIL: inline image missing from decoded $($pair[0]) output"; $script:failures++ }
  if ($decoded.Contains('/IIm1 Do')) { Write-Output "[inline/keep] FAIL: unexpected '/IIm1 Do' in decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[inline/keep] threshold assertions done"

# --- case: optimize (managed vs oracle image optimization) ---
$case = New-Case 'optimize'
Copy-Item "$fixtureDir/photo-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/inline-1page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--optimize-images', '--jpeg-quality=50', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "optimize: emit failed ($code)" }
$code = Invoke-Oracle $case @('--optimize-images', '--jpeg-quality=50', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "optimize: oracle failed ($code)" }
Assert-Trio 'optimize' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/DCTDecode', '/Width 160', '/Height 160', '/DeviceRGB')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[optimize] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[optimize] recompression assertions done"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/s.pdf=$case/lib-skip.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--optimize-images', '--oi-min-width=100000', '--oi-min-height=100000', '--oi-min-area=100000000', '--', 'outputs/s.pdf')
if ($code -ne 0) { throw "optimize/skip: emit failed ($code)" }
$code = Invoke-Oracle $case @('--optimize-images', '--oi-min-width=100000', '--oi-min-height=100000', '--oi-min-area=100000000', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora-skip.pdf")
if ($code -ne 0) { throw "optimize/skip: oracle failed ($code)" }
Assert-Trio 'optimize-skip' "$case/lib-skip.pdf" "$case/ora-skip.pdf" 1
if ((Get-StreamPayloads "$case/lib-skip.pdf") -ne (Get-StreamPayloads "$case/ora-skip.pdf")) { Write-Output "[optimize/skip] FAIL: kept image payloads differ"; $script:failures++ }
else { Write-Output "[optimize/skip] threshold payloads match" }
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/k.pdf=$case/lib-keep.pdf", '--', 'inputs/b.pdf', '--pages', '.', '1', '--optimize-images', '--keep-inline-images', '--', 'outputs/k.pdf')
if ($code -ne 0) { throw "optimize/keep: emit failed ($code)" }
$code = Invoke-Oracle $case @('--optimize-images', '--keep-inline-images', 'inputs/b.pdf', '--pages', '.', '1', '--', "$case/ora-keep.pdf")
if ($code -ne 0) { throw "optimize/keep: oracle failed ($code)" }
Assert-Trio 'optimize-keep' "$case/lib-keep.pdf" "$case/ora-keep.pdf" 1
foreach ($pair in @(('lib-keep', "$case/lib-keep.pdf"), ('ora-keep', "$case/ora-keep.pdf"))) {
  $decoded = Get-DecodedText $pair[1]
  if (-not $decoded.Contains(' BI ')) { Write-Output "[optimize/keep] FAIL: inline image missing from decoded $($pair[0]) output"; $script:failures++ }
}
Write-Output "[optimize/keep] keep-inline assertions done"

# --- case: diagnostics (managed vs oracle output-neutral diagnostics) ---
$case = New-Case 'diagnostics'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/plain.pdf=$case/lib-plain.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', 'outputs/plain.pdf')
if ($code -ne 0) { throw "diagnostics: baseline emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-plain.pdf")
if ($code -ne 0) { throw "diagnostics: baseline oracle failed ($code)" }
Assert-Trio 'diagnostics-plain' "$case/lib-plain.pdf" "$case/ora-plain.pdf" 2
foreach ($sub in @('progress', 'verbose', 'report-memory-usage')) {
  $m = Invoke-ManagedExit $case @('emit', '--workdir', $case, '--out', "outputs/$sub.pdf=$case/lib-$sub.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', "--$sub", '--', "outputs/$sub.pdf") "diag-$sub"
  $o = Invoke-Oracle $case @("--$sub", 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--', "$case/ora-$sub.pdf")
  if ($m -ne 0 -or $o -ne 0) { Write-Output "[diagnostics/$sub] FAIL: exits managed=$m oracle=$o, expected 0/0"; $script:failures++; continue }
  Assert-Trio "diagnostics-$sub" "$case/lib-$sub.pdf" "$case/ora-$sub.pdf" 2
  $lh = (Get-FileHash "$case/lib-$sub.pdf" -Algorithm SHA256).Hash
  $ph = (Get-FileHash "$case/lib-plain.pdf" -Algorithm SHA256).Hash
  if ($lh -ne $ph) { Write-Output "[diagnostics/$sub] FAIL: flagged bytes differ from unflagged bytes"; $script:failures++ }
  else { Write-Output "[diagnostics/$sub] exits 0/0 with output-neutral diagnostics" }
}
$m = Invoke-ManagedExit $case @('emit', '--workdir', $case, '--out', "outputs/schema.json=$case/lib-schema.json", '--', 'inputs/a.pdf', '--json', '--json-key=pages', '--test-json-schema', '--', 'outputs/schema.json') "diag-schema"
$o = Invoke-Oracle $case @('--test-json-schema', '--json', '--json-key=pages', 'inputs/a.pdf', '--', "$case/ora-schema.json")
if ($m -ne 0 -or $o -ne 0) { Write-Output "[diagnostics/schema] FAIL: exits managed=$m oracle=$o, expected 0/0"; $script:failures++ }
else {
  $libKeys = ((Get-Content "$case/lib-schema.json" -Raw | ConvertFrom-Json).PSObject.Properties.Name | Sort-Object) -join ','
  $oraKeys = ((Get-Content "$case/ora-schema.json" -Raw | ConvertFrom-Json).PSObject.Properties.Name | Sort-Object) -join ','
  if ($libKeys -ne $oraKeys) { Write-Output "[diagnostics/schema] FAIL: top-level keys lib=[$libKeys] ora=[$oraKeys]"; $script:failures++ }
  else { Write-Output "[diagnostics/schema] exits 0/0 with matching top-level keys [$libKeys]" }
}

# --- case: check-linearization (managed vs oracle inspection) ---
$case = New-Case 'check-linearization'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--linearize', '--static-id', 'inputs/a.pdf', '--', "$case/inputs/lin.pdf")
if ($code -ne 0) { throw "check-linearization setup: oracle linearize failed ($code)" }
function Invoke-ManagedLinCheck {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--check-linearization') -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  return $p.ExitCode
}
function Invoke-OracleLinCheck {
  param([string]$File)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--check-linearization', $File) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/ql.txt" -RedirectStandardError "$env:TEMP/qle.txt"
  return $p.ExitCode
}
foreach ($sub in @(@("valid", "inputs/lin.pdf", 0, 0, "no linearization errors"), @("plain", "inputs/a.pdf", 0, 0, "is not linearized"))) {
  $tag = $sub[0]
  $code = Invoke-ManagedLinCheck $case $sub[1] "clin-$tag"
  $ora = Invoke-OracleLinCheck "$case/$($sub[1])"
  if ($code -ne $sub[2] -or $ora -ne $sub[3]) { Write-Output "[check-linearization/$tag] FAIL: exits managed=$code oracle=$ora, expected $($sub[2])/$($sub[3])"; $script:failures++; continue }
  $managedText = Get-Content "$case/managed-clin-$tag.txt" -Raw
  $oracleText = (Get-Content "$env:TEMP/ql.txt" -Raw) + (Get-Content "$env:TEMP/qle.txt" -Raw)
  if (-not $managedText.Contains($sub[4]) -or -not $oracleText.Contains($sub[4])) { Write-Output "[check-linearization/$tag] FAIL: message mismatch"; $script:failures++ }
  else { Write-Output "[check-linearization/$tag] exits $($sub[2])/$($sub[3]) with matching message" }
}
Copy-Item "$case/inputs/lin.pdf" "$case/inputs/badH.pdf"
Edit-FirstBytes "$case/inputs/badH.pdf" "[ 550 127 ]" "[ 550 999 ]"
$code = Invoke-ManagedLinCheck $case "inputs/badH.pdf" "clin-badh"
$ora = Invoke-OracleLinCheck "$case/inputs/badH.pdf"
if ($code -ne 2 -or $ora -ne 3) { Write-Output "[check-linearization/badH] FAIL: exits managed=$code oracle=$ora, expected 2/3"; $script:failures++ }
else { Write-Output "[check-linearization/badH] hint defects fail on both sides (2/3)" }

# --- case: show-linearization (managed vs oracle dump) ---
$case = New-Case 'show-linearization'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--linearize', '--static-id', 'inputs/a.pdf', '--', "$case/inputs/lin.pdf")
if ($code -ne 0) { throw "show-linearization setup: oracle linearize failed ($code)" }
function Invoke-ManagedShow {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--show-linearization') -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  return $p.ExitCode
}
function Invoke-OracleShow {
  param([string]$File)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-linearization', $File) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
  return $p.ExitCode
}
function Get-NormalizedDump {
  param([string]$ManagedFile, [string]$OracleFile)
  $managed = Get-Content $ManagedFile | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Skip 1
  $oracle = Get-Content $OracleFile | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Skip 1
  return @((($managed -join "`n")), (($oracle -join "`n")))
}
$code = Invoke-ManagedShow $case "inputs/lin.pdf" "show-valid"
$ora = Invoke-OracleShow "$case/inputs/lin.pdf"
if ($code -ne 0 -or $ora -ne 0) { Write-Output "[show-linearization/valid] FAIL: exits managed=$code oracle=$ora, expected 0/0"; $script:failures++ }
else {
  $dumps = Get-NormalizedDump "$case/managed-show-valid.txt" "$env:TEMP/qs.txt"
  if ($dumps[0] -ne $dumps[1]) { Write-Output "[show-linearization/valid] FAIL: dump mismatch"; $script:failures++ }
  else { Write-Output "[show-linearization/valid] dumps match line for line" }
}
$code = Invoke-ManagedShow $case "inputs/a.pdf" "show-plain"
$ora = Invoke-OracleShow "$case/inputs/a.pdf"
if ($code -ne 0 -or $ora -ne 0) { Write-Output "[show-linearization/plain] FAIL: exits managed=$code oracle=$ora, expected 0/0"; $script:failures++ }
elseif (-not (Select-String -Path "$case/managed-show-plain.txt" -Pattern 'is not linearized' -Quiet) -or -not (Select-String -Path "$env:TEMP/qs.txt" -Pattern 'is not linearized' -Quiet)) { Write-Output "[show-linearization/plain] FAIL: message mismatch"; $script:failures++ }
else { Write-Output "[show-linearization/plain] both report not linearized" }
Copy-Item "$case/inputs/lin.pdf" "$case/inputs/badH.pdf"
Edit-FirstBytes "$case/inputs/badH.pdf" "[ 550 127 ]" "[ 550 999 ]"
$code = Invoke-ManagedShow $case "inputs/badH.pdf" "show-badh"
$ora = Invoke-OracleShow "$case/inputs/badH.pdf"
if ($code -ne 2 -or $ora -ne 2) { Write-Output "[show-linearization/badH] FAIL: exits managed=$code oracle=$ora, expected 2/2"; $script:failures++ }
else { Write-Output "[show-linearization/badH] unreadable hints fail on both sides (2/2)" }

# --- case: show-npages (managed vs oracle page counts) ---
$case = New-Case 'show-npages'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
function Invoke-ManagedNpages {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--show-npages') -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'PageCount Info \S+: (\d+)' | Select-Object -First 1
  if ($null -eq $line -or $p.ExitCode -ne 0) { throw "show-npages: managed inspection failed ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
foreach ($sub in @(@("two", "inputs/a.pdf", 2), @("one", "inputs/b.pdf", 1))) {
  $name = $sub[0]
  $m = Invoke-ManagedNpages $case $sub[1] "np-$name"
  $o = Invoke-Oracle $case @('--show-npages', $sub[1])
  if ($o -ne 0) { Write-Output "[show-npages/$name] FAIL: oracle exit $o"; $script:failures++; continue }
  $oraCount = [int]((Get-Content "$case/ora-stdout.txt" -Raw).Trim())
  if ($m -ne $sub[2] -or $oraCount -ne $sub[2]) { Write-Output "[show-npages/$name] FAIL: counts managed=$m oracle=$oraCount, expected $($sub[2])"; $script:failures++ }
  else { Write-Output "[show-npages/$name] page counts agree ($($sub[2]))" }
}

# --- case: show-pages (managed vs oracle page dictionaries) ---
$case = New-Case 'show-pages'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
function Invoke-ManagedPages {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--show-pages') -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  return $p.ExitCode
}
foreach ($sub in @(@("two", "inputs/a.pdf"), @("one", "inputs/b.pdf"))) {
  $name = $sub[0]
  $mcode = Invoke-ManagedPages $case $sub[1] "pages-$name"
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-pages', "$case/$($sub[1])") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
  $ocode = $p.ExitCode
  if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[show-pages/$name] FAIL: show exits managed=$mcode oracle=$ocode"; $script:failures++; continue }
  $mLines = Get-Content "$case/managed-pages-$name.txt" | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-pages/$name] FAIL: page dump mismatch"; $script:failures++ }
  else { Write-Output "[show-pages/$name] page dumps match line for line" }
}

# --- case: show-pages-with-images (managed vs oracle image listing) ---
$case = New-Case 'show-pages-with-images'
Copy-Item "$fixtureDir/images-1page.pdf" "$case/inputs/i.pdf"
Copy-Item "$fixtureDir/images-nodim-1page.pdf" "$case/inputs/n.pdf"
function Invoke-ManagedPagesImages {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--show-pages', '--with-images') -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "show-pages-with-images: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
$mcode = Invoke-ManagedPagesImages $case "inputs/i.pdf" "images-clean"
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-pages', '--with-images', "$case/inputs/i.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
$ocode = $p.ExitCode
if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[show-pages-with-images/clean] FAIL: exits managed=$mcode oracle=$ocode, expected 0/0"; $script:failures++ }
else {
  $mLines = Get-Content "$case/managed-images-clean.txt" | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-pages-with-images/clean] FAIL: dump mismatch"; $script:failures++ }
  else { Write-Output "[show-pages-with-images/clean] dumps match line for line" }
}
$mcode = Invoke-ManagedPagesImages $case "inputs/n.pdf" "images-nodim"
$p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-pages', '--with-images', "$case/inputs/n.pdf") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
$ocode = $p.ExitCode
if ($mcode -ne 3 -or $ocode -ne 3) { Write-Output "[show-pages-with-images/nodim] FAIL: exits managed=$mcode oracle=$ocode, expected 3/3"; $script:failures++ }
else {
  $mLines = Get-Content "$case/managed-images-nodim.txt" | Where-Object { $_ -match '^emit: diagnostic PageListing ' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-pages-with-images/nodim] FAIL: listing mismatch"; $script:failures++ }
  else { Write-Output "[show-pages-with-images/nodim] listings match with warning exits 3/3" }
}

# --- case: show-object (managed vs oracle object definitions) ---
$case = New-Case 'show-object'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/objstm-1page.pdf" "$case/inputs/o.pdf"
foreach ($sub in @(@("dict", "inputs/a.pdf", "3"), @("stream", "inputs/a.pdf", "4"), @("trailer", "inputs/a.pdf", "trailer"), @("missing", "inputs/a.pdf", "99"), @("compressed", "inputs/o.pdf", "1"))) {
  $name = $sub[0]
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $case, '--', $sub[1], "--show-object=$($sub[2])") -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/managed-obj-$name.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-object/$name] FAIL: managed exit $($p.ExitCode)"; $script:failures++; continue }
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @("--show-object=$($sub[2])", "$case/$($sub[1])") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-object/$name] FAIL: oracle exit $($p.ExitCode)"; $script:failures++; continue }
  $mLines = Get-Content "$case/managed-obj-$name.txt" | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-object/$name] FAIL: object dump mismatch"; $script:failures++ }
  else { Write-Output "[show-object/$name] object dumps match line for line" }
}

# --- case: show-object-stream-data (managed vs oracle stream payloads) ---
$case = New-Case 'show-object-stream-data'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Oracle $case @('--stream-data=compress', 'inputs/a.pdf', '--', "$case/inputs/c.pdf")
if ($code -ne 0) { throw "show-object-stream-data setup: oracle compress failed ($code)" }
function Invoke-ManagedStream {
  param([string]$Workdir, [string[]]$EmitArgs, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList (@($probeDll) + $EmitArgs) -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "show-object-stream-data: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
function Invoke-OracleBytes {
  param([string[]]$OracleArgs, [string]$OutFile)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList $OracleArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $OutFile -RedirectStandardError "$env:TEMP/qse.txt"
  return $p.ExitCode
}
function Assert-StreamBytesEqual {
  param([string]$Name, [string]$LibFile, [string]$OraFile)
  $lh = (Get-FileHash $LibFile -Algorithm SHA256).Hash
  $oh = (Get-FileHash $OraFile -Algorithm SHA256).Hash
  if ($lh -ne $oh) { Write-Output "[show-object-stream-data/$Name] FAIL: payload bytes differ"; $script:failures++ }
  else { Write-Output "[show-object-stream-data/$Name] payloads byte-identical" }
}
$code = Invoke-ManagedStream $case @('emit', '--workdir', $case, '--out', "outputs/r.bin=$case/lib-r.bin", '--', 'inputs/a.pdf', '--show-object=4', '--raw-stream-data', '--', 'outputs/r.bin') "stream-raw"
if ($code -ne 0) { Write-Output "[show-object-stream-data/raw] FAIL: managed exit $code"; $script:failures++ }
else {
  $code = Invoke-OracleBytes @('--show-object=4', '--raw-stream-data', "$case/inputs/a.pdf") "$case/ora-r.bin"
  if ($code -ne 0) { Write-Output "[show-object-stream-data/raw] FAIL: oracle exit $code"; $script:failures++ }
  else { Assert-StreamBytesEqual 'raw' "$case/lib-r.bin" "$case/ora-r.bin" }
}
$code = Invoke-ManagedStream $case @('emit', '--workdir', $case, '--out', "outputs/f.bin=$case/lib-f.bin", '--', 'inputs/c.pdf', '--show-object=4', '--filtered-stream-data', '--', 'outputs/f.bin') "stream-filtered"
if ($code -ne 0) { Write-Output "[show-object-stream-data/filtered] FAIL: managed exit $code"; $script:failures++ }
else {
  $code = Invoke-OracleBytes @('--show-object=4', '--filtered-stream-data', "$case/inputs/c.pdf") "$case/ora-f.bin"
  if ($code -ne 0) { Write-Output "[show-object-stream-data/filtered] FAIL: oracle exit $code"; $script:failures++ }
  else { Assert-StreamBytesEqual 'filtered' "$case/lib-f.bin" "$case/ora-f.bin" }
}
foreach ($sub in @(@("missing", "inputs/a.pdf", "99", "trailerless"), @("trailer", "inputs/a.pdf", "trailer", "dictionary"))) {
  $name = $sub[0]
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $case, '--', $sub[1], "--show-object=$($sub[2])", '--raw-stream-data') -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/managed-$name.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-object-stream-data/$name] FAIL: managed exit $($p.ExitCode)"; $script:failures++; continue }
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @("--show-object=$($sub[2])", '--raw-stream-data', "$case/$($sub[1])") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-object-stream-data/$name] FAIL: oracle exit $($p.ExitCode)"; $script:failures++; continue }
  $mLines = Get-Content "$case/managed-$name.txt" | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-object-stream-data/$name] FAIL: dump mismatch"; $script:failures++ }
  else { Write-Output "[show-object-stream-data/$name] dumps match line for line" }
}

# --- case: show-xref (managed vs oracle cross-reference entries) ---
$case = New-Case 'show-xref'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/objstm-1page.pdf" "$case/inputs/o.pdf"
foreach ($sub in @(@("classic", "inputs/a.pdf"), @("streams", "inputs/o.pdf"))) {
  $name = $sub[0]
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $case, '--', $sub[1], '--show-xref') -NoNewWindow -Wait -PassThru -WorkingDirectory $case -RedirectStandardOutput "$case/managed-xref-$name.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-xref/$name] FAIL: managed exit $($p.ExitCode)"; $script:failures++; continue }
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--show-xref', "$case/$($sub[1])") -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qs.txt" -RedirectStandardError "$env:TEMP/qse.txt"
  if ($p.ExitCode -ne 0) { Write-Output "[show-xref/$name] FAIL: oracle exit $($p.ExitCode)"; $script:failures++; continue }
  $mLines = Get-Content "$case/managed-xref-$name.txt" | Where-Object { $_ -match '^emit: diagnostic' } | ForEach-Object { $_ -replace '^emit: diagnostic \S+ \S+ ', '' } | Where-Object { $_.Trim().Length -gt 0 }
  $oLines = Get-Content "$env:TEMP/qs.txt" | Where-Object { $_.Trim().Length -gt 0 }
  if (($mLines -join "`n") -ne ($oLines -join "`n")) { Write-Output "[show-xref/$name] FAIL: xref dump mismatch"; $script:failures++ }
  else { Write-Output "[show-xref/$name] xref dumps match line for line" }
}

# --- case: remove-acroform (managed vs oracle form-dictionary removal) ---
$case = New-Case 'remove-acroform'
Copy-Item "$fixtureDir/forms-1page.pdf" "$case/inputs/f.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/f.pdf', '--pages', '.', '1', '--remove-acroform', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "remove-acroform: emit failed ($code)" }
$code = Invoke-Oracle $case @('--remove-acroform', 'inputs/f.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "remove-acroform: oracle failed ($code)" }
Assert-Trio 'remove-acroform' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/AcroForm')) { Write-Output "[remove-acroform] FAIL: /AcroForm remains in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/Annots')) { Write-Output "[remove-acroform] FAIL: /Annots missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[remove-acroform] form-dictionary assertions done"

# --- case: forms-preserve (managed vs oracle form-dictionary preservation) ---
$case = New-Case 'forms-preserve'
Copy-Item "$fixtureDir/forms-1page.pdf" "$case/inputs/f.pdf"
Copy-Item "$fixtureDir/forms-2page.pdf" "$case/inputs/g.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib-full.pdf", '--', 'inputs/f.pdf', '--pages', '.', '1', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "forms-preserve/full: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/f.pdf', '--pages', '.', '1', '--', "$case/ora-full.pdf")
if ($code -ne 0) { throw "forms-preserve/full: oracle failed ($code)" }
Assert-Trio 'forms-preserve-full' "$case/lib-full.pdf" "$case/ora-full.pdf" 1
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/s.pdf=$case/lib-sub.pdf", '--', 'inputs/g.pdf', '--pages', '.', '1', '--', 'outputs/s.pdf')
if ($code -ne 0) { throw "forms-preserve/sub: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/g.pdf', '--pages', '.', '1', '--', "$case/ora-sub.pdf")
if ($code -ne 0) { throw "forms-preserve/sub: oracle failed ($code)" }
Assert-Trio 'forms-preserve-sub' "$case/lib-sub.pdf" "$case/ora-sub.pdf" 1
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/m.pdf=$case/lib-merge.pdf", '--', 'inputs/g.pdf', '--pages', 'inputs/g.pdf', '1', 'inputs/f.pdf', '1', '--', 'outputs/m.pdf')
if ($code -ne 0) { throw "forms-preserve/merge: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--pages', 'inputs/g.pdf', '1', 'inputs/f.pdf', '1', '--', "$case/ora-merge.pdf")
if ($code -ne 0) { throw "forms-preserve/merge: oracle failed ($code)" }
Assert-Trio 'forms-preserve-merge' "$case/lib-merge.pdf" "$case/ora-merge.pdf" 2
foreach ($pair in @(('lib-full', "$case/lib-full.pdf"), ('ora-full', "$case/ora-full.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/AcroForm', '/Fields', '(Name)', '(Hello)', '/NeedAppearances')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[forms-preserve/full] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[forms-preserve/full] registration assertions done"
foreach ($pair in @(('lib-sub', "$case/lib-sub.pdf"), ('ora-sub', "$case/ora-sub.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/AcroForm', '/Fields', '(First)', '(AAA)', '/NeedAppearances', '/DR')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[forms-preserve/sub] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
  foreach ($needle in @('(Second)', '(BBB)')) {
    if ($rawText.Contains($needle)) { Write-Output "[forms-preserve/sub] FAIL: '$needle' remains in $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[forms-preserve/sub] pruning assertions done"
foreach ($pair in @(('lib-merge', "$case/lib-merge.pdf"), ('ora-merge', "$case/ora-merge.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/AcroForm', '/Fields', '(First)', '(AAA)', '(Name)', '(Hello)')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[forms-preserve/merge] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[forms-preserve/merge] registration assertions done"
foreach ($trio in @(@('full', "$case/lib-full.pdf", "$case/ora-full.pdf"), @('sub', "$case/lib-sub.pdf", "$case/ora-sub.pdf"), @('merge', "$case/lib-merge.pdf", "$case/ora-merge.pdf"))) {
  $tag = $trio[0]
  $libJsonRaw = & $QpdfExe --json $trio[1] 2>$null | Out-String
  $oraJsonRaw = & $QpdfExe --json $trio[2] 2>$null | Out-String
  $libJson = $libJsonRaw | ConvertFrom-Json
  $oraJson = $oraJsonRaw | ConvertFrom-Json
  if ($libJson.acroform.needappearances -ne $oraJson.acroform.needappearances) { Write-Output "[forms-preserve/$tag] FAIL: needAppearances mismatch"; $script:failures++ }
  $libFields = @($libJson.acroform.fields | ForEach-Object { "$($_.fullname)=$($_.value)" } | Sort-Object)
  $oraFields = @($oraJson.acroform.fields | ForEach-Object { "$($_.fullname)=$($_.value)" } | Sort-Object)
  if (($libFields -join ',') -ne ($oraFields -join ',')) { Write-Output "[forms-preserve/$tag] FAIL: fields lib=[$($libFields -join ',')] ora=[$($oraFields -join ',')]"; $script:failures++ }
  else { Write-Output "[forms-preserve/$tag] field semantics agree ([$($libFields -join ',')])" }
}
Write-Output "[forms-preserve] JSON semantic assertions done"

# --- case: outlines-preserve (managed vs oracle outline preservation for ordinary) ---
$case = New-Case 'outlines-preserve'
Copy-Item "$fixtureDir/outlined-2page.pdf" "$case/inputs/o.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/o.pdf', '--pages', '.', '1', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "outlines-preserve: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/o.pdf', '--pages', 'inputs/o.pdf', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "outlines-preserve: oracle failed ($code)" }
Assert-Trio 'outlines-preserve' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/Outlines', '(Ch1)', '(Ch2)')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[outlines-preserve] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[outlines-preserve] outline assertions done"

# --- case: structure-preserve (managed vs oracle structure preservation for ordinary) ---
$case = New-Case 'structure-preserve'
Copy-Item "$fixtureDir/struct-preserve-1page.pdf" "$case/inputs/s.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/s.pdf', '--pages', '.', '1', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "structure-preserve: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/s.pdf', '--pages', 'inputs/s.pdf', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "structure-preserve: oracle failed ($code)" }
Assert-Trio 'structure-preserve' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  foreach ($needle in @('/StructTreeRoot', '/MarkInfo', '/Marked')) {
    if (-not $rawText.Contains($needle)) { Write-Output "[structure-preserve] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
}
Write-Output "[structure-preserve] structure assertions done"

# --- case: preserve-unreferenced (managed vs oracle object preservation) ---
$case = New-Case 'preserve-unreferenced'
Copy-Item "$fixtureDir/orphans-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--preserve-unreferenced', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "preserve-unreferenced: emit failed ($code)" }
$code = Invoke-Oracle $case @('--preserve-unreferenced', 'inputs/a.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "preserve-unreferenced: oracle failed ($code)" }
Assert-Trio 'preserve-unreferenced' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  $decoded = Get-DecodedText $pair[1]
  foreach ($needle in @('(OrphanOne)', 'HELLO-ORPHAN', '/Metadata', '<x>meta</x>')) {
    if (-not $rawText.Contains($needle) -and -not $decoded.Contains($needle)) { Write-Output "[preserve-unreferenced] FAIL: '$needle' missing from $($pair[0]) output"; $script:failures++ }
  }
  if ($rawText -notmatch '\[\s*0\s+0\s+10\s+10\s*\]') { Write-Output "[preserve-unreferenced] FAIL: dangling page missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[preserve-unreferenced] loose-object assertions done"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/m.pdf=$case/lib-empty.pdf", '--', '--empty', '--preserve-unreferenced', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', '--', 'outputs/m.pdf')
if ($code -ne 0) { throw "preserve-unreferenced/empty: emit failed ($code)" }
$code = Invoke-Oracle $case @('--preserve-unreferenced', '--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', '--', "$case/ora-empty.pdf")
if ($code -ne 0) { throw "preserve-unreferenced/empty: oracle failed ($code)" }
Assert-Trio 'preserve-unreferenced-empty' "$case/lib-empty.pdf" "$case/ora-empty.pdf" 2
foreach ($pair in @(('lib-empty', "$case/lib-empty.pdf"), ('ora-empty', "$case/ora-empty.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('(OrphanOne)')) { Write-Output "[preserve-unreferenced/empty] FAIL: orphan kept in $($pair[0]) output"; $script:failures++ }
}
Write-Output "[preserve-unreferenced/empty] empty-base assertions done"

# --- case: remove-structure (managed vs oracle structure-tree removal) ---
$case = New-Case 'remove-structure'
Copy-Item "$fixtureDir/struct-1page.pdf" "$case/inputs/s.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/s.pdf', '--pages', '.', '1', '--remove-structure', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "remove-structure: emit failed ($code)" }
$code = Invoke-Oracle $case @('--remove-structure', 'inputs/s.pdf', '--pages', '.', '1', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "remove-structure: oracle failed ($code)" }
Assert-Trio 'remove-structure' "$case/lib.pdf" "$case/ora.pdf" 1
foreach ($pair in @(('lib', "$case/lib.pdf"), ('ora', "$case/ora.pdf"))) {
  $rawText = [IO.File]::ReadAllText($pair[1])
  if ($rawText.Contains('/StructTreeRoot') -or $rawText.Contains('/MarkInfo')) { Write-Output "[remove-structure] FAIL: structure keys remain in $($pair[0]) output"; $script:failures++ }
  if (-not $rawText.Contains('/Type /Pages')) { Write-Output "[remove-structure] FAIL: pages tree missing from $($pair[0]) output"; $script:failures++ }
}
Write-Output "[remove-structure] structure-tree assertions done"

# --- case: ignore-xref-streams (managed vs oracle classic-table reading) ---
$case = New-Case 'ignore-xref-streams'
Copy-Item "$fixtureDir/hybrid-xstm.pdf" "$case/inputs/h.pdf"
Copy-Item "$fixtureDir/xstm-classic.pdf" "$case/inputs/x.pdf"
Copy-Item "$fixtureDir/objstm-1page.pdf" "$case/inputs/o.pdf"
function Invoke-ManagedNpagesFlag {
  param([string]$Workdir, [string]$InFile, [string]$Tag)
  $p = Start-Process -FilePath 'dotnet' -ArgumentList @($probeDll, 'emit', '--workdir', $Workdir, '--', $InFile, '--show-npages', '--ignore-xref-streams') -NoNewWindow -Wait -PassThru -WorkingDirectory $Workdir -RedirectStandardOutput "$Workdir/managed-$Tag.txt"
  $line = Select-String -Path "$Workdir/managed-$Tag.txt" -Pattern 'emit: status=\w+ exit=(\d+)' | Select-Object -First 1
  if ($null -eq $line) { throw "ignore-xref-streams: managed emit produced no status line ($Tag)" }
  return [int]$line.Matches[0].Groups[1].Value
}
function Invoke-OracleNpagesFlag {
  param([string]$File)
  $p = Start-Process -FilePath $QpdfExe -ArgumentList @('--ignore-xref-streams', '--show-npages', $File) -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$env:TEMP/qnp.txt"
  return $p.ExitCode
}
foreach ($sub in @(@("hybrid", "inputs/h.pdf", 3), @("classic", "inputs/x.pdf", 0), @("objstm", "inputs/o.pdf", 3))) {
  $name = $sub[0]
  $m = Invoke-ManagedNpagesFlag $case $sub[1] "xref-$name"
  $o = Invoke-OracleNpagesFlag "$case/$($sub[1])"
  if ($m -ne $sub[2] -or $o -ne $sub[2]) { Write-Output "[ignore-xref-streams/$name] FAIL: exits managed=$m oracle=$o, expected $($sub[2])"; $script:failures++; continue }
  if ((Get-PigNpages "$case/$($sub[1])") -ne 1) { Write-Output "[ignore-xref-streams/$name] FAIL: independent page count"; $script:failures++ }
  else { Write-Output "[ignore-xref-streams/$name] exit parity ($m/$o) with independent count 1" }
}
Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/h.pdf=$case/lib-h.pdf", '--', 'inputs/h.pdf', '--pages', '.', '1', '--ignore-xref-streams', '--', 'outputs/h.pdf') | Out-Null
Invoke-Oracle $case @('--ignore-xref-streams', 'inputs/h.pdf', '--pages', '.', '1', '--', "$case/ora-h.pdf") | Out-Null
Assert-Trio 'ignore-xref-streams/hybrid' "$case/lib-h.pdf" "$case/ora-h.pdf" 1

# --- case: linearize-pass1 (managed vs oracle first pass) ---
$case = New-Case 'linearize-pass1'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/p1.bin=$case/lib-p1.bin", '--out', "outputs/final.pdf=$case/lib-final.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '.', '2', '--linearize', '--linearize-pass1=outputs/p1.bin', '--', 'outputs/final.pdf')
if ($code -ne 0) { throw "linearize-pass1: managed emit failed ($code)" }
$code = Invoke-Oracle $case @('--linearize', '--static-id', '--linearize-pass1=ora-p1.bin', 'inputs/a.pdf', '--', "$case/ora-final.pdf")
if ($code -ne 0) { throw "linearize-pass1: oracle failed ($code)" }
Copy-Item "$case/ora-p1.bin" "$case/inputs/orap1.bin"
$libP1 = [IO.File]::ReadAllBytes("$case/lib-p1.bin")
$libFinal = [IO.File]::ReadAllBytes("$case/lib-final.pdf")
$oraP1 = [IO.File]::ReadAllBytes("$case/inputs/orap1.bin")
function Test-HasNeedle {
  param([byte[]]$Bytes, [string]$Needle)
  $nb = [Text.Encoding]::ASCII.GetBytes($Needle)
  for ($i = 0; $i + $nb.Length -le $Bytes.Length; $i++) {
    $m = $true
    for ($k = 0; $k -lt $nb.Length; $k++) { if ($Bytes[$i + $k] -ne $nb[$k]) { $m = $false; break } }
    if ($m) { return $true }
  }
  return $false
}
if (-not (Test-HasNeedle $libP1 '0000000000 00000 n ')) { Write-Output '[linearize-pass1] FAIL: managed pass1 lacks zeroed xref entries'; $script:failures++ }
elseif (Test-HasNeedle $libFinal '0000000000 00000 n ') { Write-Output '[linearize-pass1] FAIL: managed final keeps zeroed xref entries'; $script:failures++ }
elseif ($libFinal.Length -le $libP1.Length) { Write-Output '[linearize-pass1] FAIL: managed final not longer than pass1'; $script:failures++ }
else { Write-Output '[linearize-pass1] managed pass1 shape holds (zeroed entries, shorter than final)' }
if (-not (Test-HasNeedle $oraP1 '0000000000 00000 n ')) { Write-Output '[linearize-pass1] FAIL: oracle pass1 lacks zeroed xref entries'; $script:failures++ }
else { Write-Output '[linearize-pass1] oracle pass1 shape holds' }
$code = Invoke-OracleCheck "$case/lib-final.pdf"
if ($code -ne 0) { Write-Output "[linearize-pass1] FAIL: managed final check exit $code"; $script:failures++ }
else { Write-Output '[linearize-pass1] managed final validates under oracle check' }
Copy-Item "$case/lib-final.pdf" "$case/inputs/check-final.pdf"
$code = Invoke-ManagedCheck $case "inputs/check-final.pdf" "pass1-final"
if ($code -ne 0) { Write-Output "[linearize-pass1] FAIL: managed final check exit $code"; $script:failures++ }
else { Write-Output "[linearize-pass1] managed final validates under managed check" }

# --- case: linearize-outlines (managed vs oracle with outlines) ---
$case = New-Case 'linearize-outlines'
Copy-Item "$fixtureDir/outlined-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/outlined-use-2page.pdf" "$case/inputs/u.pdf"
foreach ($sub in @(@("plain", "inputs/a.pdf"), @("use", "inputs/u.pdf"))) {
  $tag = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$tag.pdf=$case/lib-$tag.pdf", '--', $sub[1], '--pages', '.', '1', '.', '2', '--linearize', '--', "outputs/$tag.pdf"))
  if ($code -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: managed emit exit $code"; $script:failures++; continue }
  $code = Invoke-Oracle $case @('--linearize', '--static-id', $sub[1], '--', "$case/ora-$tag.pdf")
  if ($code -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: oracle linearize exit $code"; $script:failures++; continue }
  Copy-Item "$case/lib-$tag.pdf" "$case/inputs/check-$tag.pdf"
  $mcode = Invoke-ManagedCheck $case "inputs/check-$tag.pdf" "ol-$tag"
  $ocode = Invoke-OracleCheck "$case/lib-$tag.pdf"
  if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: managed output checks managed=$mcode oracle=$ocode"; $script:failures++; continue }
  $ocode = Invoke-OracleCheck "$case/ora-$tag.pdf"
  if ($ocode -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: oracle output check exit $ocode"; $script:failures++; continue }
  $mcode = Invoke-ManagedShow $case "inputs/check-$tag.pdf" "olshow-lib-$tag"
  $ocode = Invoke-OracleShow "$case/lib-$tag.pdf"
  if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: show exits on managed output managed=$mcode oracle=$ocode"; $script:failures++; continue }
  $dumps = Get-NormalizedDump "$case/managed-olshow-lib-$tag.txt" "$env:TEMP/qs.txt"
  if ($dumps[0] -ne $dumps[1]) { Write-Output "[linearize-outlines/$tag] FAIL: show dump mismatch on managed output"; $script:failures++; continue }
  Copy-Item "$case/ora-$tag.pdf" "$case/inputs/ora-$tag.pdf"
  $mcode = Invoke-ManagedShow $case "inputs/ora-$tag.pdf" "olshow-ora-$tag"
  $ocode = Invoke-OracleShow "$case/ora-$tag.pdf"
  if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-outlines/$tag] FAIL: show exits on oracle output managed=$mcode oracle=$ocode"; $script:failures++; continue }
  $dumps = Get-NormalizedDump "$case/managed-olshow-ora-$tag.txt" "$env:TEMP/qs.txt"
  if ($dumps[0] -ne $dumps[1]) { Write-Output "[linearize-outlines/$tag] FAIL: show dump mismatch on oracle output"; $script:failures++ }
  else { Write-Output "[linearize-outlines/$tag] outputs validate and show dumps match on both files" }
}

# --- case: linearize-thumbs (managed vs oracle with thumbnails) ---
$case = New-Case 'linearize-thumbs'
Copy-Item "$fixtureDir/thumb-1page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/t.pdf=$case/lib-t.pdf", '--', 'inputs/a.pdf', '--pages', '.', '1', '--linearize', '--', 'outputs/t.pdf')
if ($code -ne 0) { Write-Output '[linearize-thumbs] FAIL: managed emit exit $code'; $script:failures++ }
else {
  $code = Invoke-Oracle $case @('--linearize', '--static-id', 'inputs/a.pdf', '--', "$case/ora-t.pdf")
  if ($code -ne 0) { Write-Output '[linearize-thumbs] FAIL: oracle linearize exit $code'; $script:failures++ }
  else {
    Copy-Item "$case/lib-t.pdf" "$case/inputs/check-t.pdf"
    $mcode = Invoke-ManagedCheck $case "inputs/check-t.pdf" "thumb-t"
    $ocode = Invoke-OracleCheck "$case/lib-t.pdf"
    if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-thumbs] FAIL: managed output checks managed=$mcode oracle=$ocode"; $script:failures++ }
    else {
      $ocode = Invoke-OracleCheck "$case/ora-t.pdf"
      if ($ocode -ne 0) { Write-Output "[linearize-thumbs] FAIL: oracle output check exit $ocode"; $script:failures++ }
      else {
        $mcode = Invoke-ManagedShow $case "inputs/check-t.pdf" "thumbshow-lib"
        $ocode = Invoke-OracleShow "$case/lib-t.pdf"
        if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-thumbs] FAIL: show exits managed=$mcode oracle=$ocode"; $script:failures++ }
        else {
          $dumps = Get-NormalizedDump "$case/managed-thumbshow-lib.txt" "$env:TEMP/qs.txt"
          if ($dumps[0] -ne $dumps[1]) { Write-Output '[linearize-thumbs] FAIL: show dump mismatch'; $script:failures++ }
          else { Write-Output '[linearize-thumbs] outputs validate and show dumps match' }
        }
      }
    }
  }
}

# --- case: linearize (managed writer vs oracle writer) ---
$case = New-Case 'linearize'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
foreach ($sub in @(@("two", "inputs/a.pdf", @(".", "1", ".", "2"), 2), @("one", "inputs/b.pdf", @(".", "1"), 1))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', $sub[1], '--pages') + $sub[2] + @('--linearize', '--', "outputs/$name.pdf"))
  if ($code -ne 0) { Write-Output "[linearize/$name] FAIL: managed emit exit $code"; $script:failures++; continue }
  $code = Invoke-Oracle $case @('--linearize', '--static-id', $sub[1], '--', "$case/ora-$name.pdf")
  if ($code -ne 0) { Write-Output "[linearize/$name] FAIL: oracle linearize exit $code"; $script:failures++; continue }
  Copy-Item "$case/lib-$name.pdf" "$case/inputs/check-$name.pdf"
  $code = Invoke-ManagedCheck $case "inputs/check-$name.pdf" "lin-$name"
  $ora = Invoke-OracleCheck "$case/lib-$name.pdf"
  if ($code -ne 0 -or $ora -ne 0) { Write-Output "[linearize/$name] FAIL: managed output check exits managed=$code oracle=$ora, expected 0/0"; $script:failures++; continue }
  $ora = Invoke-OracleCheck "$case/ora-$name.pdf"
  if ($ora -ne 0) { Write-Output "[linearize/$name] FAIL: oracle output check exit $ora"; $script:failures++; continue }
  if ((Get-PigNpages "$case/lib-$name.pdf") -ne $sub[3] -or (Get-PigNpages "$case/ora-$name.pdf") -ne $sub[3]) { Write-Output "[linearize/$name] FAIL: independent page counts"; $script:failures++ }
  else { Write-Output "[linearize/$name] writer outputs validate on both sides" }
}

# --- case: linearize-streams (managed vs oracle writer with object streams) ---
$case = New-Case 'linearize-streams'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/b.pdf"
Copy-Item "$fixtureDir/outlined-2page.pdf" "$case/inputs/o.pdf"
foreach ($sub in @(@("two", "inputs/a.pdf", @(".", "1", ".", "2"), 2), @("one", "inputs/b.pdf", @(".", "1"), 1), @("outlines", "inputs/o.pdf", @(".", "1", ".", "2"), 2))) {
  $name = $sub[0]
  $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/$name.pdf=$case/lib-$name.pdf", '--', $sub[1], '--pages') + $sub[2] + @('--linearize', '--object-streams=generate', '--', "outputs/$name.pdf"))
  if ($code -ne 0) { Write-Output "[linearize-streams/$name] FAIL: managed emit exit $code"; $script:failures++; continue }
  $code = Invoke-Oracle $case @('--linearize', '--static-id', '--object-streams=generate', $sub[1], '--', "$case/ora-$name.pdf")
  if ($code -ne 0) { Write-Output "[linearize-streams/$name] FAIL: oracle linearize exit $code"; $script:failures++; continue }
  $rawText = [IO.File]::ReadAllText("$case/lib-$name.pdf")
  if (-not $rawText.Contains('/Type /ObjStm') -or -not $rawText.Contains('/Type /XRef')) { Write-Output "[linearize-streams/$name] FAIL: managed output lacks object/xref streams"; $script:failures++; continue }
  Copy-Item "$case/lib-$name.pdf" "$case/inputs/check-$name.pdf"
  $code = Invoke-ManagedCheck $case "inputs/check-$name.pdf" "linstreams-$name"
  $ora = Invoke-OracleCheck "$case/lib-$name.pdf"
  if ($code -ne 0 -or $ora -ne 0) { Write-Output "[linearize-streams/$name] FAIL: managed output check exits managed=$code oracle=$ora, expected 0/0"; $script:failures++; continue }
  $ora = Invoke-OracleCheck "$case/ora-$name.pdf"
  if ($ora -ne 0) { Write-Output "[linearize-streams/$name] FAIL: oracle output check exit $ora"; $script:failures++; continue }
  Copy-Item "$case/ora-$name.pdf" "$case/inputs/oracheck-$name.pdf"
  $code = Invoke-ManagedCheck $case "inputs/oracheck-$name.pdf" "linstreams-ora-$name"
  if ($code -ne 0) { Write-Output "[linearize-streams/$name] FAIL: oracle output managed-check exit $code"; $script:failures++; continue }
  $mcode = Invoke-ManagedShow $case "inputs/check-$name.pdf" "linstreams-show-$name"
  $ocode = Invoke-OracleShow "$case/lib-$name.pdf"
  if ($mcode -ne 0 -or $ocode -ne 0) { Write-Output "[linearize-streams/$name] FAIL: show exits managed=$mcode oracle=$ocode"; $script:failures++; continue }
  if ((Get-PigNpages "$case/lib-$name.pdf") -ne $sub[3] -or (Get-PigNpages "$case/ora-$name.pdf") -ne $sub[3]) { Write-Output "[linearize-streams/$name] FAIL: independent page counts"; $script:failures++ }
  else { Write-Output "[linearize-streams/$name] streamed writer outputs validate on both sides" }
}
if ($script:failures -gt 0) { throw "Differential lane failed with $($script:failures) failure(s)." }
Write-Output "Differential lane passed: library and oracle outputs validate across selection, merge, split, collate, ranges, rotate-ranges, empty, uncompress, normalize, coalesce, flatten-rotation, prune, preserve, preserve-unreferenced, json, json-import, json-update, json-modes, compress, writer-controls, attachments, metadata, labels, labels-merge (merged, subset, whole-file and split catalogs agree), forms, overlay, overlay-ranges, overlay-annots (copied subtypes, contents, transformed rectangles, appearance matrices and nulled page-boundary destinations agree), errors, job-file, replace, recovery, identity, hints, global, warnings, show-npages, show-pages, show-pages-with-images, diagnostics, optimize, inline, ignore-xref-streams, show-object, show-object-stream-data, show-xref, remove-acroform, forms-preserve, outlines-preserve, structure-preserve, remove-structure, qdf, qdf-noids, fixqdf, zlibflate, linearized, check-linearization, show-linearization, linearize, linearize-streams, linearize-pass1, linearize-outlines and linearize-thumbs."
