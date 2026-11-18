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
  & $QpdfExe --json --json-key=qpdf --json-stream-data=inline --decode-level=none $File 1> ($File + '.state.json') 2> ($File + '.state-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Preservation inspection failed: $File" }
  $json = Get-Content -LiteralPath ($File + '.state.json') -Raw | ConvertFrom-Json -AsHashtable
  $tags = @{}; $references = @{}
  foreach ($pair in $json.qpdf[1].GetEnumerator()) {
    $stream = $pair.Value.stream
    $node = if ($null -ne $stream) { $stream.dict } else { $pair.Value.value }
    if ($node -isnot [Collections.IDictionary] -or $null -eq $node['/Tag']) { continue }
    $tag = $node['/Tag']
    $reference = $pair.Key.Substring(4)
    if ($tags.ContainsKey($tag)) { throw "Duplicated preservation tag $tag in $File." }
    $references[$reference] = $tag
    $payloadHash = if ($null -ne $stream) {
      if (-not $stream.Contains('data')) { throw "Missing payload for tagged stream $tag in $File." }
      [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($stream.data)))
    } else { $null }
    $tags[$tag] = @{ node = $node; payload = $payloadHash }
  }
  $graph = [ordered]@{}
  foreach ($tag in @($tags.Keys | Sort-Object)) {
    $record = $tags[$tag]
    $peer = if ($record.node.Contains('/Peer')) {
      $ref = [string]$record.node['/Peer']
      if ($references.ContainsKey($ref)) { $references[$ref] } else { '<untagged>' }
    } else { $null }
    $graph[$tag] = [ordered]@{ peer = $peer; payload = $record.payload }
  }
  return $graph
}

function Assert-PreservedGraph($Actual, $Expected, [string] $Label) {
  if (($Actual | ConvertTo-Json -Depth 5 -Compress) -cne ($Expected | ConvertTo-Json -Depth 5 -Compress)) {
    throw "Preserved graph differs: $Label"
  }
}

$names = @('forward-sharing','backward-sharing','cycle','self-cycle','shared-stream','nested-unpreservable','simple-unpreservable','unloadable')
$nativeBaselines = 0
$managedComparisons = 0
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($name in $names) {
    $nativeWhole = $null
    foreach ($path in @('whole','select','split')) {
      $case = Join-Path $WorkRoot "$name-$path"
      New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
      Copy-Item -LiteralPath (Join-Path $FixtureDirectory "preservation-$name.pdf") -Destination "$case/inputs/a.pdf"
      $tail = @('inputs/a.pdf','--preserve-unreferenced','--stream-data=preserve')
      $job = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; preserveUnreferenced = $true; streamData = 'preserve' }
      if ($path -eq 'select') { $tail += @('--pages','.','1','--'); $job.pages = @(@{ file = '.'; range = '1' }) }
      $logical = 'outputs/a.pdf'; $nativeFile = 'ora.pdf'
      if ($path -eq 'split') { $tail += '--split-pages=1'; $job.splitPages = '1'; $logical = 'outputs/a-1.pdf'; $nativeFile = 'ora-1.pdf' }
      Push-Location $case
      try {
        & $QpdfExe @tail ora.pdf 1> native.txt 2> native-error.txt
        $nativeExit = $LASTEXITCODE
      } finally { Pop-Location }
      $expectedNativeExit = if ($name -eq 'unloadable' -and $path -ne 'split') { 3 } else { 0 }
      if ($nativeExit -ne $expectedNativeExit) { throw "Unexpected native preservation exit: $case; $nativeExit" }
      $native = Read-PreservedGraph (Join-Path $case $nativeFile)
      $nativeBaselines++
      if ($path -eq 'whole') { $nativeWhole = $native }
      if ($path -eq 'split' -and $native.Count -ne 0) { throw "Native split policy changed: $case" }
      $warning = $name -in @('nested-unpreservable','simple-unpreservable','unloadable')
      $expectedExit = if ($warning) { 3 } else { 0 }
      $expected = if ($warning) { [ordered]@{} } elseif ($path -eq 'split') { $nativeWhole } else { $native }
      if (-not $warning) {
        $expectedReturn = if ($name -eq 'cycle') { 'u:A' } elseif ($name -eq 'self-cycle') { 'u:B' } else { $null }
        if ($expected.Count -ne 3 -or $expected['u:A'].peer -cne 'u:B' -or $expected['u:C'].peer -cne 'u:B' -or
            $expected['u:B'].peer -cne $expectedReturn -or ($null -ne $expected['u:B'].payload) -ne ($name -eq 'shared-stream')) {
          throw "Missing native semantic baseline for $case."
        }
      }
      $routes = @('argv')
      if ("$name-$path" -in @('cycle-select', 'shared-stream-split')) { $routes += 'job' }
      foreach ($route in $routes) {
        $output = "$case/$route.pdf"
        $emit = @('emit','--workdir',$case,'--out',"$logical=$output",'--') + $tail + 'outputs/a.pdf'
        if ($route -eq 'job') {
          $job | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
          $emit = @('emit-job','--workdir',$case,'--out',"$logical=$output",'--job',"$case/job.json")
        }
        $managed = Invoke-ReferenceProbe -Arguments (@($ProbeDll) + $emit) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "Preservation failed: $case; $managed" }
        if ($managed -notmatch "emit: status=Completed exit=$expectedExit" -or $managed -match 'HostOperationFailed') {
          throw "Unexpected preservation exit: $case; $managed"
        }
        Assert-PreservedGraph (Read-PreservedGraph $output) $expected "$case/$route"
        $managedComparisons++
        Write-Output "[preservation/$name/$path/$route] identities, skip policy and output integrity verified"
      }
    }
  }
  foreach ($control in @(
      @{ name = 'forward-sharing'; tags = @('u:A','u:C') },
      @{ name = 'cycle'; tags = @('u:B') },
      @{ name = 'shared-stream'; tags = @('u:B') }
  )) {
    $case = Join-Path $WorkRoot ($control.name + '-whole')
    $source = "$case/argv.pdf"
    $original = Read-PreservedGraph $source
    $json = Get-Content -LiteralPath "$source.state.json" -Raw | ConvertFrom-Json -AsHashtable
    $objects = [ordered]@{}
    foreach ($entry in $json.qpdf[1].GetEnumerator()) {
      $value = $entry.Value.value
      if ($value -is [Collections.IDictionary] -and $value['/Tag'] -in $control.tags -and $value.Contains('/Peer')) {
        $null = $value.Remove('/Peer')
        $objects[$entry.Key] = $entry.Value
      }
      $stream = $entry.Value.stream
      if ($control.name -eq 'shared-stream' -and $stream -and $stream.dict['/Tag'] -in $control.tags) {
        $bytes = [Convert]::FromBase64String($stream.data)
        $bytes[0] = $bytes[0] -bxor 1
        $stream.data = [Convert]::ToBase64String($bytes)
        $objects[$entry.Key] = $entry.Value
      }
    }
    if ($objects.Count -ne $control.tags.Count) { throw 'Could not prepare the altered preservation control.' }
    $patch = "$case/altered-graph.json"
    @{ qpdf=@(@{ jsonversion=2 },$objects) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $patch
    $altered = "$case/altered-graph.pdf"
    & $QpdfExe $source "--update-from-json=$patch" --preserve-unreferenced --stream-data=preserve $altered 1> "$case/altered.txt" 2> "$case/altered-error.txt"
    if ($LASTEXITCODE -ne 0) { throw 'Could not write the altered preservation control.' }
    $different = Read-PreservedGraph $altered
    try {
      Assert-PreservedGraph $different $original $altered
      throw 'The preservation comparison accepted an altered graph.'
    } catch {
      if (-not $_.Exception.Message.StartsWith('Preserved graph differs:', [StringComparison]::Ordinal)) { throw }
    }
    Write-Output "[preservation/negative/$($control.name)] altered graph rejects."
  }
  Write-Output "Preservation lane passed: $managedComparisons managed comparisons, $nativeBaselines native baselines."
} finally { Complete-ReferenceProbe }
