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

if ($script:failures -gt 0) { throw "Differential lane failed with $($script:failures) failure(s)." }
Write-Output "Differential lane passed: library and oracle outputs validate across selection, merge, uncompress, normalize, json, forms, overlay, qdf, fixqdf and zlibflate."
