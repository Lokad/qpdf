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
if (Test-Path -LiteralPath $WorkRoot) { throw 'Preservation workspace already exists.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')

function Read-PreservedGraph([string] $File) {
  & $QpdfExe --check $File 1> ($File + '.check.txt') 2> ($File + '.check-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Preservation output fails syntax checks: $File" }
  & $QpdfExe --json --json-key=qpdf $File 1> ($File + '.state.json') 2> ($File + '.state-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Preservation inspection failed: $File" }
  $json = Get-Content -LiteralPath ($File + '.state.json') -Raw | ConvertFrom-Json -AsHashtable
  $tags = @{}; $nodes = @{}
  foreach ($pair in $json.qpdf[1].GetEnumerator()) {
    $node = $pair.Value.value
    if ($null -eq $node) { $node = $pair.Value.stream.dict }
    if ($node -isnot [Collections.IDictionary] -or $null -eq $node['/Tag']) { continue }
    $tag = $node['/Tag']; $reference = $pair.Key.Substring(4)
    if (-not $tags.ContainsKey($tag)) { $tags[$tag] = [Collections.Generic.List[string]]::new() }
    $tags[$tag].Add($reference); $nodes[$reference] = $node
  }
  $counts = [ordered]@{}
  foreach ($tag in @($tags.Keys | Sort-Object)) { $counts[$tag] = $tags[$tag].Count }
  $shared = $null
  if ($tags.ContainsKey('u:A') -and $tags.ContainsKey('u:C')) {
    $shared = $nodes[$tags['u:A'][0]]['/Peer'] -ceq $nodes[$tags['u:C'][0]]['/Peer']
  }
  $selfCycles = @(foreach ($reference in $tags['u:B']) { $nodes[$reference]['/Peer'] -ceq $reference })
  return [ordered]@{ counts = $counts; shared = $shared; selfCycles = @($selfCycles | Sort-Object) }
}

$names = @('forward-sharing','backward-sharing','cycle','self-cycle','shared-stream','nested-unpreservable','simple-unpreservable','unloadable')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($name in $names) {
    $nativeWhole = $null
    foreach ($path in @('whole','select','split')) {
      foreach ($route in @('argv','job')) {
        $case = Join-Path $WorkRoot "$name-$path-$route"
        New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory "preservation-$name.pdf") -Destination "$case/inputs/a.pdf"
        $tail = @('inputs/a.pdf','--preserve-unreferenced','--stream-data=preserve')
        $job = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; preserveUnreferenced = $true; streamData = 'preserve' }
        if ($path -eq 'select') { $tail += @('--pages','.','1','--'); $job.pages = @(@{ file = '.'; range = '1' }) }
        $logical = 'outputs/a.pdf'; $nativeFile = 'ora.pdf'
        if ($path -eq 'split') { $tail += '--split-pages=1'; $job.splitPages = '1'; $logical = 'outputs/a-1.pdf'; $nativeFile = 'ora-1.pdf' }
        $emit = @('emit','--workdir',$case,'--out',"$logical=$case/lib.pdf",'--') + $tail + 'outputs/a.pdf'
        if ($route -eq 'job') {
          $job | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
          $emit = @('emit-job','--workdir',$case,'--out',"$logical=$case/lib.pdf",'--job',"$case/job.json")
        }
        $managed = Invoke-ReferenceProbe -Arguments (@($ProbeDll) + $emit) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "Preservation failed: $case; $managed" }
        $warning = $name -in @('nested-unpreservable','simple-unpreservable','unloadable')
        $expectedExit = if ($warning) { 3 } else { 0 }
        if ($managed -notmatch "emit: status=Completed exit=$expectedExit" -or $managed -match 'HostOperationFailed') {
          throw "Unexpected preservation exit: $case; $managed"
        }
        Push-Location $case
        try {
          & $QpdfExe @tail ora.pdf 1> native.txt 2> native-error.txt
          $nativeExit = $LASTEXITCODE
        } finally { Pop-Location }
        $expectedNativeExit = if ($name -eq 'unloadable' -and $path -ne 'split') { 3 } else { 0 }
        if ($nativeExit -ne $expectedNativeExit) { throw "Unexpected native preservation exit: $case; $nativeExit" }
        $native = Read-PreservedGraph (Join-Path $case $nativeFile)
        $actual = Read-PreservedGraph "$case/lib.pdf"
        if ($path -eq 'whole') { $nativeWhole = $native }
        if ($warning) {
          # Contained-copy skips differ from native; split must still publish a
          # coherent graph and must not retain fragments of failed candidates.
          if ($actual.counts.Count -ne 0) { throw "Partial failed preservation escaped: $case" }
        } else {
          # Managed split deliberately retains these roots; native split drops
          # them. Compare its identities to the native whole-file control.
          $expected = if ($path -eq 'split') { $nativeWhole } else { $native }
          if (($actual | ConvertTo-Json -Depth 5 -Compress) -cne ($expected | ConvertTo-Json -Depth 5 -Compress)) {
            throw "Preserved sharing/cycles differ: $case"
          }
        }
        if ($path -eq 'split' -and $native.counts.Count -ne 0) { throw "Native split policy changed: $case" }
        Write-Output "[preservation/$name/$path/$route] identities, skip policy and output integrity verified"
      }
    }
  }
} finally { Complete-ReferenceProbe }
