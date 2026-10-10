<# Explicit development lane for transformed inspection identities and effects. #>
param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll,
  [Parameter(Mandatory = $true)] [string] $WorkRoot,
  [Parameter(Mandatory = $true)] [string] $FixtureDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$FixtureDirectory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {

function Write-AuthoredPdf([string]$Path, [bool]$ContentGenerations) {
  $first = if ($ContentGenerations) { '[5 3 R 6 4 R]' } else { '5 0 R' }
  $second = if ($ContentGenerations) { '6 4 R' } else { '6 0 R' }
  $objects = @(
    @{N=1;G=0;V='<< /Type /Catalog /Pages 2 0 R >>'},
    @{N=2;G=0;V='<< /Type /Pages /Kids [8 0 R] /Count 2 /MediaBox [0 0 600 800] /Resources << /ProcSet [/PDF] >> /Rotate 90 >>'},
    @{N=3;G=2;V="<< /Type /Page /Parent 8 0 R /Contents $first /Rotate null >>"},
    @{N=4;G=0;V="<< /Type /Page /Parent 8 0 R /Contents $second >>"},
    @{N=5;G=$(if ($ContentGenerations) {3} else {0});V="<< /Length 3 >>`nstream`nq Q`nendstream"},
    @{N=6;G=$(if ($ContentGenerations) {4} else {0});V="<< /Length 3 >>`nstream`nq Q`nendstream"},
    @{N=7;G=0;V='(orphan)'},
    @{N=8;G=0;V='<< /Type /Pages /Parent 2 0 R /Kids [3 2 R 4 0 R] /Count 2 >>'}
  )
  $buffer = [IO.MemoryStream]::new()
  try {
    $write = { param([string]$Text) $buffer.Write([Text.Encoding]::ASCII.GetBytes($Text)) }
    & $write "%PDF-1.4`n"
    $entries = @{}
    foreach ($object in $objects) {
      $entries[$object.N] = @{Offset=$buffer.Position;Generation=$object.G}
      & $write "$($object.N) $($object.G) obj`n$($object.V)`nendobj`n"
    }
    $xref = $buffer.Position
    & $write "xref`n0 9`n0000000000 65535 f `n"
    for ($n=1; $n -le 8; $n++) { & $write (('{0:D10} {1:D5} n ' -f [long]$entries[$n].Offset,[int]$entries[$n].Generation) + "`n") }
    & $write "trailer`n<< /Size 9 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n"
    [IO.File]::WriteAllBytes($Path,$buffer.ToArray())
  } finally { $buffer.Dispose() }
}

$cases = @(
  @{N='first';R='1';O='2';Line='<< /Count 1 /Kids [ 3 0 R ] /Type /Pages >>'},
  @{N='second';R='2';O='2';Line='<< /Count 1 /Kids [ 4 0 R ] /Type /Pages >>'},
  @{N='reverse';R='2,1';O='2';Line='<< /Count 2 /Kids [ 4 0 R 3 0 R ] /Type /Pages >>'},
  @{N='duplicate';R='1,1';O='2';Line='<< /Count 2 /Kids [ 3 0 R 8 0 R ] /Type /Pages >>'},
  @{N='duplicate-rotate';R='1,1';Rotate='+90:2';O='8';Pattern='/Rotate 90'},
  @{N='duplicate-original';R='1,1';Rotate='+90:2';O='3';Forbidden='/Rotate'},
  @{N='removed';R='1';O='4';Line='null'},
  @{N='rotate';Rotate='-90:2';O='4';Pattern='/Rotate 270'},
  @{N='select-rotate';R='2';Rotate='+90:1';O='4';Pattern='/Rotate 90'},
  @{N='same-named-source';R='2';Source='inputs/a.pdf';O='2';Line='<< /Count 1 /Kids [ 4 0 R ] /Type /Pages >>'},
  @{N='alias-source';R='2';Source='./inputs/a.pdf';O='2';Line='<< /Count 1 /Kids [ 8 0 R ] /Type /Pages >>'},
  @{N='foreign-source';R='2';Source='inputs/b.pdf';O='2';Line='<< /Count 1 /Kids [ 8 0 R ] /Type /Pages >>'},
  @{N='foreign-duplicate';R='1,1';Source='inputs/b.pdf';O='2'},
  @{N='foreign-interleaved';Sources=@(@{file='inputs/b.pdf';range='1'},@{file='.';range='2'},@{file='inputs/b.pdf';range='2'});O='2'},
  @{N='collation';Sources=@(@{file='.';range='1,2'},@{file='inputs/b.pdf';range='1,2'});Collate='1';O='2'},
  @{N='nested-first';Nested=$true;R='1';O='3,2';Pattern='/MediaBox 9 0 R'},
  @{N='nested-root';Nested=$true;R='1';O='2';Line='<< /Count 1 /Kids [ 3 2 R ] /Type /Pages >>'},
  @{N='nested-box';Nested=$true;R='1';O='9';Line='[ 0 0 600 800 ]'},
  @{N='nested-resources';Nested=$true;R='1';O='10';Line='<< /ProcSet [ /PDF ] >>'},
  @{N='nested-duplicate';Nested=$true;R='1,1';Rotate='+90:2';O='11';Pattern='/Rotate 180'},
  @{N='nested-original';Nested=$true;R='1,1';Rotate='+90:2';O='3,2';Pattern='/Rotate 90'},
  @{N='nested-stale-generation';Nested=$true;R='1';O='3,0';Line='null'},
  @{N='nested-removed';Nested=$true;R='1';O='4';Line='null'},
  @{N='nested-null-rotation';Nested=$true;Rotate='+90:1';O='3,2';Pattern='/Rotate 180'},
  @{N='nested-inherited-rotation';Nested=$true;Rotate='-90:2';O='4';Pattern='/Rotate 0'},
  @{N='content-generations';Nested=$true;ContentGenerations=$true;R='1,1';O='11';Line='    5 3 R'},
  @{N='content-generations-ordinary';Nested=$true;ContentGenerations=$true;O='3,2';Line='    6 4 R'},
  @{N='range-bounds';R='9';O='2';Exit=2},
  @{N='range-syntax';R='x';O='2';Exit=2},
  @{N='rotation-value';Rotate='+45:1';O='2';Exit=2},
  @{N='missing-source';R='1';Source='inputs/missing.pdf';O='2';Exit=2}
)
$records = foreach ($item in $cases) {
  foreach ($check in @($false,$true)) {
    foreach ($route in @('job','argv','mixed')) {
      $name = "$($item.N)-$route-check-$check"
      $case = Join-Path $WorkRoot $name
      if (Test-Path -LiteralPath $case) { throw "Existing case output: $case" }
      New-Item -ItemType Directory -Path "$case/inputs","$case/outputs" | Out-Null
      if ($item.Nested) { Write-AuthoredPdf "$case/inputs/a.pdf" ([bool]$item.ContentGenerations) }
      else { Copy-Item -LiteralPath "$FixtureDirectory/minimal-2page.pdf" -Destination "$case/inputs/a.pdf" }
      Copy-Item -LiteralPath "$FixtureDirectory/minimal-2page.pdf" -Destination "$case/inputs/b.pdf"
      [IO.File]::WriteAllBytes("$case/outputs/prior.pdf",[byte[]]@(0,127,255,65))
      $before = (Get-FileHash "$case/outputs/prior.pdf").Hash
      $settings = @{showNpages='';showPages='';showXref='';showObject=$item.O}
      [string[]]$argv = @('inputs/a.pdf','--show-npages','--show-pages','--show-xref',('--show-object='+$item.O))
      if ($check) { $settings.check=''; $argv += '--check' }
      if ($item.Rotate) { $settings.rotate=@($item.Rotate); $argv += ('--rotate='+$item.Rotate) }
      if ($item.Collate) { $settings.collate=$item.Collate; $argv += ('--collate='+$item.Collate) }
      $sources = if ($item.Sources) { $item.Sources } elseif ($item.R) { @(@{file=$(if ($item.Source) {$item.Source} else {'.'});range=$item.R}) } else { @() }
      if ($sources.Count -gt 0) {
        $settings.pages=@($sources); $argv += '--pages'
        foreach ($source in $sources) { $argv += @($source.file,$source.range) }
        $argv += '--'
      }
      $job = @{inputFile='inputs/a.pdf'}
      foreach ($key in $settings.Keys) { $job[$key]=$settings[$key] }
      [IO.File]::WriteAllText("$case/inputs/job.json",($job|ConvertTo-Json -Depth 8),$utf8)
      [IO.File]::WriteAllText("$case/inputs/settings.json",($settings|ConvertTo-Json -Depth 8),$utf8)
      [string[]]$nativeArgs = if ($route -eq 'job') {@('--job-json-file=inputs/job.json')} elseif ($route -eq 'mixed') {@('inputs/a.pdf','--job-json-file=inputs/settings.json')} else {$argv}
      [string[]]$managedArgs = if ($route -eq 'job') {@('emit-job','--workdir',$case,'--job',"$case/inputs/job.json")} else {@('emit','--workdir',$case,'--')+$nativeArgs}
      Push-Location $case
      try {
        & $QpdfExe @nativeArgs 1> "$case/native.txt" 2> "$case/native-error.txt"; $nativeExit=$LASTEXITCODE
        $managed=@(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$managedArgs) 2>&1|ForEach-Object {$_.ToString()}); $managedExit=$LASTEXITCODE
      } finally { Pop-Location }
      [IO.File]::WriteAllText("$case/managed.txt",($managed -join "`n")+"`n",$utf8)
      $expected = if ($item.ContainsKey('Exit')) {$item.Exit} else {0}
      $status = if ($expected -eq 2) {'Failed'} else {'Completed'}
      if ($nativeExit -ne $expected -or $managedExit -ne $expected -or ($managed -join "`n") -notmatch "emit: status=$status exit=$expected\b") { throw "$name exits differ; see $case" }
      $native = @(Get-Content "$case/native.txt" | Where-Object {$_ -match '^(\d+$|\d+/\d+: |<< .* >>$|\[ .* \]$|null$|page \d+:|  (content|images):$|    )'})
      $left = @($managed | ForEach-Object {
        if ($_ -match '^emit: diagnostic (PageCount|XrefListing|ObjectListing|PageListing) Info (.*)$') {
          if ($Matches[1] -eq 'PageCount') { $Matches[2] -replace '^inputs/a\.pdf: ','' } else { $Matches[2] }
        }
      })
      if (($left -join "`n") -cne ($native -join "`n")) { throw "$name ordered graph effects differ; see $case" }
      if (@($managed | Where-Object {$_ -match '^emit: bytes '}).Count -ne 0) { throw "$name returned an unexpected binary payload." }
      if ($expected -eq 2 -and ($left.Count -ne 0 -or [IO.File]::ReadAllText("$case/native.txt") -match 'checking inputs/a.pdf')) { throw "$name inspected/checked before source/range rejection." }
      if ($item.Line -and $native -cnotcontains $item.Line) { throw "$name missing exact effect." }
      if ($item.Pattern -and ($native -join "`n") -notmatch $item.Pattern) { throw "$name missing object effect." }
      if ($item.Forbidden -and ($native -join "`n") -match $item.Forbidden) { throw "$name altered the wrong occurrence." }
      if ((Get-FileHash "$case/outputs/prior.pdf").Hash -ne $before) { throw "$name changed prior output." }
      [pscustomobject]@{Name=$name;Route=$route;Check=$check;NativeExit=$nativeExit;ManagedExit=$managedExit;OrderedListingLines=$left.Count;InputSHA256=(Get-FileHash "$case/inputs/a.pdf").Hash;PriorOutputPreserved=$true}
      Write-Host "$($name): exit $expected; ordered graph effects agree"
    }
  }
}
foreach ($route in @('job','argv','mixed')) {
  $name = "selected-stream-$route"
  $case = Join-Path $WorkRoot $name
  if (Test-Path -LiteralPath $case) { throw "Existing case output: $case" }
  New-Item -ItemType Directory -Path "$case/inputs" | Out-Null
  Copy-Item -LiteralPath "$FixtureDirectory/minimal-2page.pdf" -Destination "$case/inputs/a.pdf"
  $settings = @{pages=@(@{file='.';range='1'});showObject='5';filteredStreamData=''}
  $job = @{inputFile='inputs/a.pdf'}
  foreach ($key in $settings.Keys) { $job[$key]=$settings[$key] }
  [IO.File]::WriteAllText("$case/inputs/job.json",($job|ConvertTo-Json -Depth 8),$utf8)
  [IO.File]::WriteAllText("$case/inputs/settings.json",($settings|ConvertTo-Json -Depth 8),$utf8)
  [string[]]$argv = if ($route -eq 'job') {@('--job-json-file=inputs/job.json')} elseif ($route -eq 'mixed') {@('inputs/a.pdf','--job-json-file=inputs/settings.json')} else {@('inputs/a.pdf','--pages','.','1','--','--show-object=5','--filtered-stream-data')}
  [string[]]$emit = if ($route -eq 'job') {@('emit-job','--workdir',$case,'--job',"$case/inputs/job.json")} else {@('emit','--workdir',$case,'--')+$argv}
  Push-Location $case
  try {
    & $QpdfExe @argv 1> "$case/native.bin" 2> "$case/native-error.txt"; $nativeExit=$LASTEXITCODE
    $managed=@(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$emit) 2>&1|ForEach-Object {$_.ToString()}); $managedExit=$LASTEXITCODE
  } finally { Pop-Location }
  [IO.File]::WriteAllText("$case/managed.txt",($managed -join "`n")+"`n",$utf8)
  [byte[]]$expected = [Text.Encoding]::ASCII.GetBytes('BT /F1 24 Tf 100 700 Td (First) Tj ET')
  $hex = [Convert]::ToHexString([IO.File]::ReadAllBytes("$case/native.bin"))
  $payload = @($managed | Where-Object {$_ -match '^emit: bytes '})
  if ($nativeExit -ne 0 -or $managedExit -ne 0 -or ($managed -join "`n") -notmatch 'emit: status=Completed exit=0\b' -or $hex -cne [Convert]::ToHexString($expected) -or $payload.Count -ne 1 -or $payload[0] -cne "emit: bytes $hex") { throw "$name selected binary bytes differ; see $case" }
  $records += [pscustomobject]@{Name=$name;Route=$route;Check=$false;NativeExit=$nativeExit;ManagedExit=$managedExit;BinaryBytes=$expected.Length;InputSHA256=(Get-FileHash "$case/inputs/a.pdf").Hash}
  Write-Host "$($name): exit 0; selected filtered bytes agree"
}
[IO.File]::WriteAllText("$WorkRoot/results.json",($records|ConvertTo-Json -Depth 6)+"`n",$utf8)
Write-Host "Checker transformation lane passed: $($records.Count) comparisons. Other contexts and audit gates remain open."

} finally { Complete-ReferenceProbe }
