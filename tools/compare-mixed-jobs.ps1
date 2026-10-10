<#
.SYNOPSIS
  Compare mixed argv/job-file configuration with the pinned oracle.
  Called by the full differential lane; also runnable for focused diagnosis.
#>
param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll,
  [Parameter(Mandatory = $true)] [string] $WorkRoot,
  [Parameter(Mandatory = $true)] [string] $FixtureDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$utf8 = [Text.UTF8Encoding]::new($false)
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$FixtureDirectory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
$cases = @(
  @{ Name='partial-before'; Args=@('--job-json-file=inputs/settings.json','inputs/a.pdf','outputs/a.pdf'); Version='1.5' },
  @{ Name='partial-between'; Args=@('inputs/a.pdf','--job-json-file=inputs/settings.json','outputs/a.pdf'); Version='1.5' },
  @{ Name='partial-after'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json'); Version='1.5' },
  @{ Name='force-before'; Args=@('inputs/a.pdf','outputs/a.pdf','--force-version=1.6','--job-json-file=inputs/settings.json'); Version='1.5' },
  @{ Name='force-after'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--force-version=1.6'); Version='1.6' },
  @{ Name='min-before'; Args=@('inputs/a.pdf','outputs/a.pdf','--min-version=1.6','--job-json-file=inputs/min.json'); Version='1.5' },
  @{ Name='min-after'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/min.json','--min-version=1.6'); Version='1.6' },
  @{ Name='file-input-count'; Args=@('--job-json-file=inputs/input.json','--show-npages'); Count=1 },
  @{ Name='file-input-check'; Args=@('--job-json-file=inputs/input.json','--check') },
  @{ Name='argv-input-file-count'; Args=@('inputs/a.pdf','--job-json-file=inputs/count.json'); Count=1 },
  @{ Name='two-partials'; Args=@('--job-json-file=inputs/input.json','--job-json-file=inputs/count.json'); Count=1 },
  @{ Name='two-inputs'; Args=@('--job-json-file=inputs/input.json','--job-json-file=inputs/input.json','--show-npages'); Exit=2 },
  @{ Name='two-settings'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--job-json-file=inputs/other.json'); Version='1.6' },
  @{ Name='positional-cursor'; Args=@('--job-json-file=inputs/input.json','outputs/a.pdf'); Exit=2 },
  @{ Name='argv-input-file-output'; Args=@('inputs/a.pdf','--job-json-file=inputs/output.json'); Version='1.4' },
  @{ Name='output-collision'; Args=@('--job-json-file=inputs/output.json','inputs/a.pdf','outputs/a.pdf'); Exit=2 },
  @{ Name='output-inspection'; Args=@('--job-json-file=inputs/complete.json','--show-npages'); Exit=2 },
  @{ Name='global-context'; Args=@('inputs/a.pdf','--global','--job-json-file=inputs/count.json','--'); Exit=2 },
  @{ Name='pages-context'; Args=@('inputs/a.pdf','--pages','--job-json-file=inputs/count.json','.','1','--','outputs/a.pdf'); Exit=2 },
  @{ Name='empty-file-cursor'; Args=@('--job-json-file=inputs/empty.json','outputs/a.pdf'); Exit=2 },
  @{ Name='argv-empty-output-file'; Args=@('--empty','--job-json-file=inputs/output.json'); Version='1.3'; Pages=0; CheckExit=2 },
  @{ Name='file-before-global'; Args=@('inputs/a.pdf','--job-json-file=inputs/global.json','--global','--max-stream-filters=0','--','--show-npages'); Count=1 },
  @{ Name='global-before-file'; Args=@('inputs/a.pdf','--global','--max-stream-filters=0','--','--job-json-file=inputs/global.json','--show-npages'); Count=1 },
  @{ Name='repeat-settings'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--job-json-file=inputs/settings.json'); Version='1.5' },
  @{ Name='file-empty-two-positionals'; Args=@('--job-json-file=inputs/empty.json','inputs/a.pdf','outputs/a.pdf'); Version='1.4' },
  @{ Name='file-empty-argv-count'; Args=@('--job-json-file=inputs/empty.json','inputs/a.pdf','--show-npages'); Count=1 },
  @{ Name='argv-input-file-empty'; Args=@('inputs/a.pdf','--job-json-file=inputs/empty.json','--show-npages'); Exit=2 },
  @{ Name='whole-rotation'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--rotate=+90:1'); Version='1.5'; Rotate=90 },
  @{ Name='whole-labels'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--set-page-labels','1:D/8/prefix','--'); Version='1.5'; Label=$true },
  @{ Name='whole-collate'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--collate'); Version='1.5' },
  @{ Name='whole-replace'; Args=@('--job-json-file=inputs/input.json','--replace-input'); Version='1.4'; Replace=$true },
  @{ Name='bad-force-overwritten'; Args=@('inputs/a.pdf','outputs/a.pdf','--force-version=bogus','--job-json-file=inputs/settings.json'); Version='1.5' },
  @{ Name='json-objects'; Args=@('inputs/a.pdf','--job-json-file=inputs/settings.json','--json=2','--json-object=trailer','--json-object=1'); Json=$true },
  @{ Name='bare-compress'; Args=@('inputs/a.pdf','outputs/a.pdf','--job-json-file=inputs/settings.json','--compress-streams'); Exit=2 },
  # Separate argv parameters are an existing managed extension; the oracle
  # comparison uses its documented joined form for the same configuration.
  @{ Name='separate-form'; Args=@('inputs/a.pdf','--job-json-file','inputs/settings.json','outputs/a.pdf'); NativeArgs=@('inputs/a.pdf','--job-json-file=inputs/settings.json','outputs/a.pdf'); Version='1.5' },
  @{ Name='nested-scalar'; Args=@('inputs/a.pdf','outputs/a.pdf','--force-version=1.7','--job-json-file=inputs/nested.json'); Version='1.5' },
  @{ Name='pages'; Args=@('inputs/a.pdf','--job-json-file=inputs/settings.json','--pages','inputs/b.pdf','2,1','--','outputs/a.pdf'); Version='1.5'; Pages=2 },
  @{ Name='overlay'; Args=@('inputs/a.pdf','--job-json-file=inputs/settings.json','--overlay','inputs/a.pdf','--','--','outputs/a.pdf'); Version='1.5' },
  @{ Name='attachment-add'; Args=@('inputs/a.pdf','--job-json-file=inputs/settings.json','--add-attachment','inputs/payload.bin','--key=payload','--','--','outputs/a.pdf'); Version='1.5'; Attachment=$true },
  @{ Name='overlay-context'; Args=@('inputs/a.pdf','--overlay','inputs/a.pdf','--job-json-file=inputs/settings.json','--','outputs/a.pdf'); Exit=2 },
  @{ Name='attachment-context'; Args=@('inputs/a.pdf','--add-attachment','inputs/payload.bin','--job-json-file=inputs/settings.json','--','outputs/a.pdf'); Exit=2 },
  @{ Name='global-terminator'; Args=@('inputs/a.pdf','--job-json-file=inputs/count.json','--global','--parser-max-errors=10'); Exit=2 },
  @{ Name='rotation-replaces'; Args=@('inputs/a.pdf','outputs/a.pdf','--rotate=+180:1','--job-json-file=inputs/rotation.json'); Version='1.4'; Rotate=90 }
  @{ Name='ordinary-json'; Args=@('inputs/a.pdf','--json=2','--json-object=trailer','--json-object=1'); Json=$true },
  @{ Name='ordinary-json-terminator'; Args=@('inputs/a.pdf','--json=2','--json-object=trailer','--json-object=1','--'); Json=$true }
)
$jobs = @{
  'settings.json'='{"forceVersion":"1.5"}'
  'other.json'='{"forceVersion":"1.6"}'
  'min.json'='{"minVersion":"1.5"}'
  'input.json'='{"inputFile":"inputs/a.pdf"}'
  'output.json'='{"outputFile":"outputs/a.pdf"}'
  'count.json'='{"showNpages":""}'
  'complete.json'='{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf"}'
  'empty.json'='{"empty":""}'
  'global.json'='{"global":{"maxStreamFilters":"1"}}'
  'nested.json'='{"forceVersion":"1.6","jobJsonFile":"inputs/settings.json"}'
  'rotation.json'='{"rotate":["+90:1"]}'
}
$records = @()
foreach ($item in $cases) {
  $name = "mixed-job-$($item.Name)"
  $case = [IO.Path]::GetFullPath((Join-Path $WorkRoot $name))
  $allowed = $WorkRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $case.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Case path escaped workspace' }
  if (Test-Path -LiteralPath $case) { Remove-Item -LiteralPath $case -Recurse -Force }
  New-Item -ItemType Directory -Path "$case/inputs", "$case/outputs" -Force | Out-Null
  Copy-Item -LiteralPath "$FixtureDirectory/minimal-1page.pdf" -Destination "$case/inputs/a.pdf"
  Copy-Item -LiteralPath "$FixtureDirectory/minimal-2page.pdf" -Destination "$case/inputs/b.pdf"
  [IO.File]::WriteAllBytes("$case/inputs/payload.bin", [byte[]](0,1,2,127,128,255))
  foreach ($job in $jobs.GetEnumerator()) { [IO.File]::WriteAllText("$case/inputs/$($job.Key)", $job.Value, $utf8) }
  $expectedExit = if ($item.ContainsKey('Exit')) { $item.Exit } else { 0 }
  $logical = if ($item.Replace) { 'inputs/a.pdf' } else { 'outputs/a.pdf' }
  $emit = @('emit','--workdir',$case)
  if ($item.Version) { $emit += @('--out', "$logical=$case/lib.pdf") }
  $emit += @('--') + $item.Args
  Push-Location $case
  try {
    $managed = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$emit) 2>&1 | ForEach-Object { $_.ToString() })
    $processExit = $LASTEXITCODE
    [IO.File]::WriteAllText("$case/managed.txt", ($managed -join "`n") + "`n", $utf8)
    $nativeArgs = if ($item.ContainsKey('NativeArgs')) { $item.NativeArgs } else { $item.Args }
    & $QpdfExe @nativeArgs 1> "$case/native.txt" 2> "$case/native-error.txt"
    $nativeExit = $LASTEXITCODE
  } finally { Pop-Location }
  $status = if ($expectedExit -eq 0) { 'Completed' } else { 'Failed' }
  if ($processExit -ne $expectedExit -or ($managed -join "`n") -notmatch "emit: status=$status exit=$expectedExit\b" -or $nativeExit -ne $expectedExit) {
    throw "$name execution mismatch: managed process=$processExit native=$nativeExit expected=$expectedExit. See $case."
  }
  if ($item.ContainsKey('Count')) {
    if (($managed -join "`n") -notmatch "PageCount Info inputs/a.pdf: $($item.Count)\b" -or
        ([IO.File]::ReadAllText("$case/native.txt").Trim()) -ne [string] $item.Count) { throw "$name count differs" }
  }
  if ($item.Json) {
    $line = @($managed | Where-Object { $_.StartsWith('emit: text ', [StringComparison]::Ordinal) })
    if ($line.Count -ne 1) { throw "$name missing JSON text result" }
    $managedText = $line[0].Substring('emit: text '.Length) | ConvertFrom-Json
    $left = $managedText | ConvertFrom-Json -AsHashtable
    $right = [IO.File]::ReadAllText("$case/native.txt") | ConvertFrom-Json -AsHashtable
    foreach ($json in @($left, $right)) {
      $objects = $json.qpdf[1]
      if ($json.version -ne 2 -or $objects.Count -ne 2 -or -not $objects.ContainsKey('obj:1 0 R') -or -not $objects.ContainsKey('trailer')) {
        throw "$name JSON object selection differs"
      }
      if ($objects['obj:1 0 R'].value['/Type'] -ne '/Catalog' -or $objects['obj:1 0 R'].value['/Pages'] -ne '2 0 R') {
        throw "$name JSON catalog differs"
      }
    }
    foreach ($key in @('/Size','/Root')) {
      if ($left.qpdf[1].trailer.value[$key] -ne $right.qpdf[1].trailer.value[$key]) { throw "$name JSON trailer differs: $key" }
    }
  }
  $pages = if ($item.ContainsKey('Pages')) { $item.Pages } else { 1 }
  $checkExit = if ($item.ContainsKey('CheckExit')) { $item.CheckExit } else { 0 }
  if ($item.Version) {
    Move-Item -LiteralPath "$case/$logical" -Destination "$case/native.pdf"
    foreach ($file in @("$case/lib.pdf","$case/native.pdf")) {
      $stem = if ($file.EndsWith('lib.pdf')) { 'lib' } else { 'native' }
      $bytes = [IO.File]::ReadAllBytes($file)
      $ascii = [Text.Encoding]::ASCII.GetString($bytes)
      if (-not $ascii.StartsWith("%PDF-$($item.Version)", [StringComparison]::Ordinal)) { throw "$name header differs" }
      & $QpdfExe --check $file 1> "$case/$stem-check.txt" 2> "$case/$stem-check-error.txt"
      if ($LASTEXITCODE -ne $checkExit) { throw "$name check differs: $stem exit $LASTEXITCODE expected $checkExit" }
      $count = @(& $QpdfExe --show-npages $file 2> "$case/$stem-count-error.txt")
      if ($LASTEXITCODE -ne 0 -or ($count -join '').Trim() -ne [string] $pages) { throw "$name native count differs: $stem" }
      $validate = @('validate', '--input', $file)
      if ($pages -eq 0) { $validate += '--allow-empty' }
      $pig = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$validate) 2>&1 | ForEach-Object { $_.ToString() })
      [IO.File]::WriteAllText("$case/$stem-independent.txt", ($pig -join "`n") + "`n", $utf8)
      if ($LASTEXITCODE -ne 0 -or ($pig -join "`n") -notmatch "independent: npages=$pages\b") { throw "$name independent count differs: $stem" }
      if ($item.Rotate -and $ascii -notmatch "/Rotate $($item.Rotate)\b") { throw "$name rotation differs" }
      if ($item.Label -and ($ascii -notmatch '\(prefix\)' -or $ascii -notmatch '/St 8\b')) { throw "$name labels differ" }
      if ($item.Attachment) {
        & $QpdfExe --show-attachment=payload $file 1> "$case/$stem-payload.bin" 2> "$case/$stem-attachment-error.txt"
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash "$case/$stem-payload.bin").Hash -ne (Get-FileHash "$case/inputs/payload.bin").Hash) {
          throw "$name attachment payload differs"
        }
      }
    }
    Write-Output "[$name] trio ok (check $checkExit, npages=$pages oracle+pig)"
  } elseif (Test-Path -LiteralPath "$case/outputs/a.pdf") { throw "$name unexpected publication" }
  $records += [pscustomobject]@{ Name=$name; ExpectedExit=$expectedExit; ManagedProcessExit=$processExit; NativeExit=$nativeExit; Rewrite=[bool]$item.Version; Pages=$pages; CheckExit=$checkExit; ManagedSyntaxExtension=$item.ContainsKey('NativeArgs') }
  Write-Output "[$name] native argv/file semantics agree (exit $expectedExit)"
}
[IO.File]::WriteAllText((Join-Path $WorkRoot 'results.json'), ($records | ConvertTo-Json -Depth 4) + "`n", $utf8)
Write-Output "Mixed-job lane passed: $($records.Count) comparisons; $(@($records | Where-Object Rewrite).Count) independently checked rewrites."

} finally { Complete-ReferenceProbe }
