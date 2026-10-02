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
if ($script:failures -gt 0) { throw "Differential lane failed with $($script:failures) failure(s)." }
Write-Output "Differential lane passed: library and oracle outputs validate across selection, merge, uncompress, normalize, json, forms, overlay, qdf, fixqdf, zlibflate, linearized, check-linearization, show-linearization and linearize."
