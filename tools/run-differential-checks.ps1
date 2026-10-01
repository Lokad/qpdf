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
    while ($ds -lt $bytes.Length -and ($bytes[$ds] -eq 13 -or $bytes[$ds] -eq 10)) { $ds++ }
    $de = $pos
    while ($de -gt $ds -and ($bytes[$de - 1] -eq 13 -or $bytes[$de - 1] -eq 10)) { $de-- }
    if ($de -le $ds) { $pos += 9; continue }
    $dpos = $text.LastIndexOf("<<", $spos)
    $dict = $text.Substring($dpos, $spos - $dpos)
    $raw = $bytes[$ds..($de - 1)]
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
    while ($ds -lt $bytes.Length -and ($bytes[$ds] -eq 13 -or $bytes[$ds] -eq 10)) { $ds++ }
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

# --- case: select ---
$case = New-Case 'select'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', '.', '2', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "select: emit failed ($code)" }
$code = Invoke-Oracle $case @('inputs/a.pdf', '--pages', '.', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "select: oracle failed ($code)" }
Assert-Trio 'select' "$case/lib.pdf" "$case/ora.pdf" 1

# --- case: merge ---
$case = New-Case 'merge'
Copy-Item "$fixtureDir/minimal-1page.pdf" "$case/inputs/a.pdf"
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/b.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', 'outputs/a.pdf')
if ($code -ne 0) { throw "merge: emit failed ($code)" }
$code = Invoke-Oracle $case @('--empty', '--pages', 'inputs/a.pdf', '1', 'inputs/b.pdf', '1', 'inputs/b.pdf', '2', '--', "$case/ora.pdf")
if ($code -ne 0) { throw "merge: oracle failed ($code)" }
Assert-Trio 'merge' "$case/lib.pdf" "$case/ora.pdf" 3

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

# --- case: json (page-count agreement) ---
$case = New-Case 'json'
Copy-Item "$fixtureDir/minimal-2page.pdf" "$case/inputs/a.pdf"
$code = Invoke-Emit $case @('emit', '--workdir', $case, '--out', "outputs/a.json=$case/lib.json", '--', 'inputs/a.pdf', '--json', '--json-key=pages', '--', 'outputs/a.json')
if ($code -ne 0) { throw "json: emit failed ($code)" }
$code = Invoke-Oracle $case @('--json', '--json-key=pages', 'inputs/a.pdf')
if ($code -ne 0) { throw "json: oracle failed ($code)" }
Copy-Item "$case/ora-stdout.txt" "$case/ora.json" -Force
$libPages = (Get-Content "$case/lib.json" -Raw | ConvertFrom-Json).pages.Count
$oraPages = (Get-Content "$case/ora.json" -Raw | ConvertFrom-Json).pages.Count
if ($libPages -ne 2 -or $oraPages -ne 2) { Write-Output "[json] FAIL: pages lib=$libPages ora=$oraPages"; $script:failures++ } else { Write-Output "[json] pages agree (2)" }

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

if ($script:failures -gt 0) { throw "Differential lane failed with $($script:failures) failure(s)." }
Write-Output "Differential lane passed: library and oracle outputs validate across selection, merge, uncompress, normalize, json, forms and overlay."
