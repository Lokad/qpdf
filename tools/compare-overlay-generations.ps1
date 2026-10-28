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
if (Test-Path -LiteralPath $WorkRoot) { throw 'Overlay generation workspace already exists.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')

function Read-AnnotationState([string]$File) {
  $report = $File + '.state.json'
  & $QpdfExe --json --json-key=qpdf --json-key=pages --json-key=acroform $File 1> $report 2> ($File + '.state-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Native annotation inspection failed: $File" }
  $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
  $objects = $json.qpdf[1]
  $resolve = { param($value)
    if ($value -is [string] -and $value -match '^\d+ \d+ R$') { return ,$objects['obj:' + $value].value }
    return ,$value
  }
  $page = & $resolve $json.pages[0].object
  $annotations = & $resolve $page['/Annots']
  $chains = [Collections.Generic.List[object]]::new()
  foreach ($link in $annotations) {
    $annotation = & $resolve $link
    $action = & $resolve $annotation['/A']
    if ($null -eq $action) { continue }
    $chain = [Collections.Generic.List[object]]::new()
    foreach ($next in $action['/Next']) {
      $target = & $resolve $next
      $chain.Add($(if ($null -eq $target) { $null } else { $target['/N'] }))
    }
    $chains.Add($chain.ToArray())
  }
  $fields = @($json.acroform.fields | ForEach-Object { $_.fullname + '=' + $_.value })
  return ([ordered]@{ next = $chains.ToArray(); fields = $fields } | ConvertTo-Json -Depth 5 -Compress)
}

$cases = @('live-first','stale-first','two-live','explicit-null','newer-stale','old-generation','form-0','form-2')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($name in $cases) {
    $expected = if ($name.StartsWith('form-', [StringComparison]::Ordinal)) { '{"next":[],"fields":["Field=u:kept"]}' }
      elseif ($name -eq 'stale-first') { '{"next":[[null,"/NextPage"]],"fields":[]}' }
      elseif ($name -eq 'two-live') { '{"next":[["/NextPage","/NextPage"]],"fields":[]}' }
      else { '{"next":[["/NextPage",null]],"fields":[]}' }
    foreach ($mode in @('overlay','underlay')) {
      foreach ($route in @('argv','job')) {
        $case = Join-Path $WorkRoot "$name-$mode-$route"
        New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory 'minimal-2page.pdf') -Destination "$case/inputs/a.pdf"
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory "overlay-generations-$name.pdf") -Destination "$case/inputs/o.pdf"
        $tail = @('inputs/a.pdf', ('--' + $mode), 'inputs/o.pdf', '--', 'outputs/a.pdf')
        $emit = @('emit','--workdir',$case,'--out',"outputs/a.pdf=$case/lib.pdf",'--') + $tail
        if ($route -eq 'job') {
          @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; $mode = @{ file = 'inputs/o.pdf' } } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
          $emit = @('emit-job','--workdir',$case,'--out',"outputs/a.pdf=$case/lib.pdf",'--job',"$case/job.json")
        }
        $managed = Invoke-ReferenceProbe -Arguments (@($ProbeDll) + $emit) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $managed -match 'emit: diagnostic') { throw "Generation copy failed: $name $mode $route; $managed" }
        & $QpdfExe "$case/inputs/a.pdf" ('--' + $mode) "$case/inputs/o.pdf" -- "$case/ora.pdf" 1> "$case/native.txt" 2> "$case/native-error.txt"
        if ($LASTEXITCODE -ne 0) { throw "Native generation copy failed: $case" }
        foreach ($file in @("$case/lib.pdf", "$case/ora.pdf")) {
          & $QpdfExe --check $file 1> ($file + '.check.txt') 2> ($file + '.check-error.txt')
          if ($LASTEXITCODE -ne 0) { throw "Generation output failed syntax checks: $file" }
          if ((Read-AnnotationState $file) -cne $expected) { throw "Generation action/field state differs: $file; expected $expected" }
        }
        Write-Output "[overlay-generations/$name/$mode/$route] exact action identities and field registration agree"
      }
    }
  }
} finally { Complete-ReferenceProbe }
