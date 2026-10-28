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
if (Test-Path -LiteralPath $WorkRoot) { throw 'Form defaults workspace already exists.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')

# These fixtures have merged terminal fields. Resolve their values and form
# defaults through native structural JSON, independently of the managed reader.
function Read-FormState([string] $File) {
  & $QpdfExe --check $File 1> ($File + '.check.txt') 2> ($File + '.check-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Form defaults syntax check failed: $File" }
  $report = $File + '.state.json'
  & $QpdfExe --json --json-key=qpdf --json-key=pages --json-key=acroform --json-stream-data=inline --decode-level=generalized $File 1> $report 2> ($File + '.state-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Form defaults inspection failed: $File" }
  $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
  $objects = $json.qpdf[1]
  $resolve = { param($value)
    if ($value -is [string] -and $value -match '^\d+ \d+ R$') { return ,$objects['obj:' + $value].value }
    return ,$value
  }
  $catalog = & $resolve $objects.trailer.value['/Root']
  $form = & $resolve $catalog['/AcroForm']
  if ($null -eq $form) { $form = @{} }
  $fields = @(foreach ($reported in $json.acroform.fields) {
    $field = & $resolve $reported.object
    $da = & $resolve $field['/DA']; if ($null -eq $da) { $da = & $resolve $form['/DA'] }
    $q = & $resolve $field['/Q']; if ($null -eq $q) { $q = & $resolve $form['/Q'] }; if ($null -eq $q) { $q = 0 }
    [ordered]@{ name = $reported.fullname; value = $reported.value; appearance = $da; quadding = $q }
  })
  $annotations = @($json.pages | ForEach-Object {
    $page = & $resolve $_.object
    $annots = & $resolve $page['/Annots']
    @(foreach ($link in $annots) { (& $resolve $link)['/Subtype'] }) -join ','
  })
  $live = $false
  # Inspect appearances attached to fields or page resources, excluding old
  # unreferenced streams. These fixtures start with empty page XObject resources.
  $appearances = [Collections.Generic.List[object]]::new()
  foreach ($reported in $json.acroform.fields) {
    $field = & $resolve $reported.object
    $ap = & $resolve $field['/AP']
    if ($null -ne $ap['/N']) { $appearances.Add($ap['/N']) }
  }
  foreach ($reported in $json.pages) {
    $page = & $resolve $reported.object
    $resources = & $resolve $page['/Resources']
    if ($null -eq $resources) { continue }
    $xobjects = & $resolve $resources['/XObject']
    if ($null -ne $xobjects) { foreach ($reference in $xobjects.Values) { $appearances.Add($reference) } }
  }
  foreach ($reference in $appearances) {
    $stream = $objects['obj:' + $reference].stream
    if ($null -ne $stream -and $stream.dict['/Subtype'] -eq '/Form') {
      $data = [Convert]::FromBase64String($stream.data)
      if ([Text.Encoding]::ASCII.GetString($data).Contains('(LIVE)', [StringComparison]::Ordinal)) { $live = $true }
    }
  }
  return ([ordered]@{ fields = $fields; need = [bool](& $resolve $form['/NeedAppearances']); annotations = $annotations; live = $live } |
    ConvertTo-Json -Depth 6 -Compress)
}

$cases = [Collections.Generic.List[object]]::new()
foreach ($name in @('direct','indirect-da','indirect-q','indirect-need','all-indirect','field-overrides')) {
  foreach ($mode in @('overlay','underlay')) { $cases.Add(@{ Name = $name; Operation = $mode; Import = $true }) }
}
foreach ($name in @('direct','indirect-need','all-indirect','stale-direct','stale-indirect')) {
  foreach ($operation in @('copy','generate','flatten','both')) { $cases.Add(@{ Name = $name; Operation = $operation; Import = $false }) }
}
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($item in $cases) {
    foreach ($route in @('argv','job')) {
      $name = $item.Name; $operation = $item.Operation
      $case = Join-Path $WorkRoot "$name-$operation-$route"
      New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
      Copy-Item -LiteralPath (Join-Path $FixtureDirectory "form-defaults-$name.pdf") -Destination "$case/inputs/source.pdf"
      $tail = @('inputs/source.pdf')
      $job = @{ inputFile = 'inputs/source.pdf'; outputFile = 'outputs/a.pdf' }
      if ($item.Import) {
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory 'minimal-2page.pdf') -Destination "$case/inputs/target.pdf"
        $tail = @('inputs/target.pdf', ('--' + $operation), 'inputs/source.pdf', '--')
        $job = @{ inputFile = 'inputs/target.pdf'; outputFile = 'outputs/a.pdf'; $operation = @{ file = 'inputs/source.pdf' } }
      } else {
        if ($operation -in @('generate','both')) { $tail += '--generate-appearances'; $job.generateAppearances = $true }
        if ($operation -in @('flatten','both')) { $tail += '--flatten-annotations=all'; $job.flattenAnnotations = 'all' }
      }
      $emit = @('emit','--workdir',$case,'--out',"outputs/a.pdf=$case/lib.pdf",'--') + $tail + 'outputs/a.pdf'
      if ($route -eq 'job') {
        $job | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
        $emit = @('emit-job','--workdir',$case,'--out',"outputs/a.pdf=$case/lib.pdf",'--job',"$case/job.json")
      }
      $managed = Invoke-ReferenceProbe -Arguments (@($ProbeDll) + $emit) 2>&1 | Out-String
      if ($LASTEXITCODE -ne 0) { throw "Managed form defaults failed: $case; $managed" }
      Push-Location $case
      try {
        & $QpdfExe @tail ora.pdf 1> native.txt 2> native-error.txt
        $nativeExit = $LASTEXITCODE
      } finally { Pop-Location }
      $expectedExit = if ($operation -eq 'flatten') { 3 } else { 0 }
      if ($nativeExit -ne $expectedExit -or $managed -notmatch "emit: status=Completed exit=$expectedExit") {
        throw "Form defaults exit mismatch: $case; expected $expectedExit, native $nativeExit; $managed"
      }
      $nativeState = Read-FormState "$case/ora.pdf"
      $managedState = Read-FormState "$case/lib.pdf"
      if ($managedState -cne $nativeState) { throw "Form defaults differ: $case; native $nativeState; managed $managedState" }
      Write-Output "[form-defaults/$name/$operation/$route] defaults, field retention and refreshed value agree"
    }
  }
} finally { Complete-ReferenceProbe }
