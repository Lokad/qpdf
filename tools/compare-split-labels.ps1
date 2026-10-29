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
if (Test-Path -LiteralPath $WorkRoot) { throw 'Split-label workspace already exists.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')

function Read-LabelState([string] $File) {
  & $QpdfExe --check $File 1> ($File + '.check.txt') 2> ($File + '.check-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Split-label syntax check failed: $File" }
  & $QpdfExe --json=2 --json-key=pages --json-key=pagelabels --json-key=qpdf $File 1> ($File + '.state.json') 2> ($File + '.state-error.txt')
  if ($LASTEXITCODE -ne 0) { throw "Split-label inspection failed: $File" }
  $json = Get-Content -LiteralPath ($File + '.state.json') -Raw | ConvertFrom-Json -AsHashtable
  $objects = $json.qpdf[1]
  $resolve = { param($value)
    if ($value -is [string] -and $value -match '^\d+ \d+ R$') { return ,$objects['obj:' + $value].value }
    return ,$value
  }
  $labels = @(foreach ($entry in $json.pagelabels) {
    $label = & $resolve $entry.label
    [ordered]@{ index = $entry.index; style = (& $resolve $label['/S']); prefix = (& $resolve $label['/P']);
      start = $(if ($label.ContainsKey('/St')) { & $resolve $label['/St'] } else { 1 }) }
  })
  $pages = @(foreach ($page in $json.pages) {
    $dictionary = & $resolve $page.object
    if ($dictionary['/ReviewPage'] -isnot [long] -and $dictionary['/ReviewPage'] -isnot [int]) { throw "Missing page marker: $File" }
    $dictionary['/ReviewPage']
  })
  $state = [ordered]@{ pages = $pages; labels = $labels }
  return ConvertTo-Json -InputObject $state -Depth 4 -Compress
}

$cases = @('default','start','transition','boundary','prefix','indirect','unlabelled') |
  ForEach-Object { @{ Name = $_; Fixture = $_; Range = '1-6'; Pages = 6 } }
$cases += @{ Name = 'subset'; Fixture = 'start'; Range = '2-5'; Pages = 4 }
$cases += @{ Name = 'reordered'; Fixture = 'start'; Range = '6,4,2,3,5,1'; Pages = 6 }
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($item in $cases) {
    foreach ($size in @(1,2)) {
      foreach ($route in @('argv','job')) {
        $case = Join-Path $WorkRoot ($item.Name + '-' + $size + '-' + $route)
        New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory ('labels-split-' + $item.Fixture + '.pdf')) -Destination "$case/inputs/source.pdf"
        # Native 12.4.2 cannot split the indirect fixture (foreign-object error).
        # Its direct equivalent independently supplies the expected label values.
        $control = if ($item.Fixture -eq 'indirect') { 'start' } else { $item.Fixture }
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory ('labels-split-' + $control + '.pdf')) -Destination "$case/inputs/control.pdf"
        $emit = @('emit','--workdir',$case)
        $parts = @()
        for ($first = 1; $first -le $item.Pages; $first += $size) {
          $last = [Math]::Min($item.Pages, $first + $size - 1)
          $part = if ($first -eq $last) { "$first" } else { "$first-$last" }
          $parts += $part; $emit += @('--out', "outputs/split-$part.pdf=$case/lib-$part.pdf")
        }
        if ($route -eq 'job') {
          @{ inputFile = 'inputs/source.pdf'; outputFile = 'outputs/split.pdf'; splitPages = "$size";
            pages = @(@{ file = '.'; range = $item.Range }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
          $emit[0] = 'emit-job'; $emit += @('--job', "$case/job.json")
        } else {
          $emit += @('--','inputs/source.pdf',"--split-pages=$size",'--pages','.',$item.Range,'--','outputs/split.pdf')
        }
        & $QpdfExe --check "$case/inputs/source.pdf" 1> "$case/source-check.txt" 2> "$case/source-check-error.txt"
        if ($LASTEXITCODE -ne 0) { throw "Invalid split-label source: $case" }
        $managed = Start-ReferenceProcess -FilePath 'dotnet' -ArgumentList (@($ProbeDll) + $emit) -WorkingDirectory $case -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/managed.txt" -RedirectStandardError "$case/managed-error.txt"
        if ($managed.ExitCode -ne 0) { throw "Managed split-label operation failed: $case" }
        # Select first: native's combined selection+split applies label positions
        # from the original document instead of the selected pages.
        & $QpdfExe "$case/inputs/control.pdf" --pages . $item.Range -- "$case/selected.pdf" 1> "$case/native-selection.txt" 2> "$case/native-selection-error.txt"
        if ($LASTEXITCODE -ne 0) { throw "Native split-label selection failed: $case" }
        & $QpdfExe "$case/selected.pdf" "--split-pages=$size" "$case/ora.pdf" 1> "$case/native.txt" 2> "$case/native-error.txt"
        if ($LASTEXITCODE -ne 0) { throw "Native split-label control failed: $case" }
        foreach ($part in $parts) {
          $actual = Read-LabelState "$case/lib-$part.pdf"
          $expected = Read-LabelState "$case/ora-$part.pdf"
          if ($actual -cne $expected) { throw "Split labels differ: $case/$part; native $expected; managed $actual" }
        }
        Write-Output "[split-labels/$($item.Name)/$size/$route] all $($parts.Count) outputs retain their label runs"
      }
    }
  }
} finally { Complete-ReferenceProbe }
