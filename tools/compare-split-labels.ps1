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

function Read-LabelState([string] $File, [bool] $RequireCanonicalPrefix) {
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
  # qpdf's pagelabels projection supplies default starts even when the raw
  # dictionary omits them. Inspect the raw managed number tree for this guard.
  if ($RequireCanonicalPrefix) {
    $catalog = & $resolve $objects['trailer']['value']['/Root']
    $tree = & $resolve $catalog['/PageLabels']
    if ($null -ne $tree) {
      $numbers = & $resolve $tree['/Nums']
      if ($numbers -isnot [array] -or $numbers.Count % 2 -ne 0) { throw "Managed split labels must have a flat number tree: $File" }
      for ($index = 1; $index -lt $numbers.Count; $index += 2) {
        $rawLabel = & $resolve $numbers[$index]
        $rawStyle = & $resolve $rawLabel['/S']
        if ($null -eq $rawStyle -and $rawLabel.ContainsKey('/St')) {
          throw "Prefix-only label contains an inactive start: $File"
        }
      }
    }
  }
  $labels = @(foreach ($entry in $json.pagelabels) {
    $label = & $resolve $entry.label
    $style = & $resolve $label['/S']
    # No numbering style means there is no counter to compare. Numbered starts
    # stay exact, including the default that qpdf supplies in this projection.
    $start = if ($null -eq $style) { $null } elseif ($label.ContainsKey('/St')) { & $resolve $label['/St'] } else { 1 }
    [ordered]@{ index = $entry.index; style = $style; prefix = (& $resolve $label['/P']); start = $start }
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
$nativeBaselines = 0
$managedComparisons = 0
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
  foreach ($item in $cases) {
    foreach ($size in @(1,2)) {
      $case = Join-Path $WorkRoot "$($item.Name)-$size"
      New-Item -ItemType Directory -Path (Join-Path $case 'inputs') | Out-Null
      $source = "$case/inputs/source.pdf"
      Copy-Item -LiteralPath (Join-Path $FixtureDirectory ('labels-split-' + $item.Fixture + '.pdf')) -Destination $source
      # Native 12.4.2 needs the direct equivalent of the indirect fixture.
      $control = if ($item.Fixture -eq 'indirect') {
        Join-Path $FixtureDirectory 'labels-split-start.pdf'
      } else { $source }
      & $QpdfExe --check $source 1> "$case/source-check.txt" 2> "$case/source-check-error.txt"
      if ($LASTEXITCODE -ne 0) { throw "Invalid split-label source: $case" }
      # Native combined selection+split uses the original label positions.
      & $QpdfExe $control --pages . $item.Range -- "$case/selected.pdf" 1> "$case/native-selection.txt" 2> "$case/native-selection-error.txt"
      if ($LASTEXITCODE -ne 0) { throw "Native split-label selection failed: $case" }
      & $QpdfExe "$case/selected.pdf" "--split-pages=$size" "$case/ora.pdf" 1> "$case/native.txt" 2> "$case/native-error.txt"
      if ($LASTEXITCODE -ne 0) { throw "Native split-label control failed: $case" }
      $expected = [ordered]@{}
      for ($first = 1; $first -le $item.Pages; $first += $size) {
        $last = [Math]::Min($item.Pages, $first + $size - 1)
        $part = if ($first -eq $last) { "$first" } else { "$first-$last" }
        $expected[$part] = Read-LabelState "$case/ora-$part.pdf" $false
      }
      $nativeBaselines++

      $routes = @('argv')
      if ($item.Name -eq 'reordered' -and $size -eq 2) { $routes += 'job' }
      foreach ($route in $routes) {
        $emit = @('emit','--workdir',$case)
        foreach ($part in $expected.Keys) {
          $emit += @('--out', "outputs/split-$part.pdf=$case/$route-$part.pdf")
        }
        if ($route -eq 'job') {
          @{ inputFile = 'inputs/source.pdf'; outputFile = 'outputs/split.pdf'; splitPages = "$size";
            pages = @(@{ file = '.'; range = $item.Range }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$case/job.json"
          $emit[0] = 'emit-job'; $emit += @('--job', "$case/job.json")
        } else {
          $emit += @('--','inputs/source.pdf',"--split-pages=$size",'--pages','.',$item.Range,'--','outputs/split.pdf')
        }
        $managed = Start-ReferenceProcess -FilePath 'dotnet' -ArgumentList (@($ProbeDll) + $emit) -WorkingDirectory $case -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$case/managed-$route.txt" -RedirectStandardError "$case/managed-$route-error.txt"
        if ($managed.ExitCode -ne 0) { throw "Managed split-label operation failed: $case ($route)" }
        foreach ($part in $expected.Keys) {
          $actual = Read-LabelState "$case/$route-$part.pdf" $true
          if ($actual -cne $expected[$part]) { throw "Split labels differ: $case/$part ($route); native $($expected[$part]); managed $actual" }
        }
        $managedComparisons++
        Write-Output "[split-labels/$($item.Name)/$size/$route] all $($expected.Count) outputs retain their label runs"
      }
    }
  }
  # Keep both rejection boundaries visible alongside the native prefix controls.
  try {
    Read-LabelState "$WorkRoot/prefix-1/ora-2.pdf" $true | Out-Null
    throw 'Inactive managed counter guard did not reject the copied native label.'
  } catch {
    if (-not $_.Exception.Message.StartsWith('Prefix-only label contains an inactive start:', [StringComparison]::Ordinal)) { throw }
  }
  $numbered = "$WorkRoot/default-1/argv-1.pdf"
  $changed = "$WorkRoot/changed-numbered-start.pdf"
  & $QpdfExe $numbered --set-page-labels 1:D/999 -- $changed 1> "$WorkRoot/changed-numbered.txt" 2> "$WorkRoot/changed-numbered-error.txt"
  if ($LASTEXITCODE -ne 0) { throw 'Numbered-start negative control could not be prepared.' }
  if ((Read-LabelState $numbered $true) -ceq (Read-LabelState $changed $true)) {
    throw 'Numbered-start comparison failed to distinguish a changed counter.'
  }
  Write-Output '[split-labels/negative] inactive managed counters reject; meaningful numeric changes differ'
  Write-Output "Split-label lane passed: $managedComparisons managed comparisons, $nativeBaselines native baselines."
} finally { Complete-ReferenceProbe }
