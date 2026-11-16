# Development-only assembly controls. Direct native JSON retains primary label
# positions; compare assembled labels against native PDF selection before JSON.
function Get-JsonAssemblyProjection {
  param($Json)
  $version = $Json['version']
  $objects = if ($version -eq 2) { $Json['qpdf'][1] } else { $Json['objects'] }
  $tags = @($Json['pages'] | ForEach-Object {
    $key = if ($version -eq 2) { 'obj:' + $_['object'] } else { $_['object'] }
    $value = if ($version -eq 2) { $objects[$key]['value'] } else { $objects[$key] }
    [regex]::Replace([string]$value['/Tag'], '^u:', '')
  })
  $labels = @($Json['pages'] | ForEach-Object {
    if ($null -eq $_['label']) { '' } else { [regex]::Replace([string]$_['label']['/P'], '^u:', '') }
  })
  $positions = @($Json['outlines'] | ForEach-Object { $_['destpageposfrom1'] })
  return [pscustomobject]@{ Tags = $tags; Labels = $labels; Positions = $positions }
}

function Test-JsonAssemblySequence {
  param($Actual, $Expected)
  return (ConvertTo-Json -InputObject @($Actual) -Compress) -ceq (ConvertTo-Json -InputObject @($Expected) -Compress)
}

function Invoke-ReferenceJsonAssembly {
  foreach ($row in @(
    @{ Name='reorder'; Primary='inputs/a.pdf'; Pages=@('.', '2,1'); Collate=@(); Tags=@('A2','A1'); Position=@(2); Version=2 },
    @{ Name='duplicate'; Primary='inputs/a.pdf'; Pages=@('.', '1,1,2'); Collate=@(); Tags=@('A1','A1','A2'); Position=@(1); Version=2 },
    @{ Name='merge'; Primary='inputs/a.pdf'; Pages=@('.', '1', 'inputs/b.pdf', '1', '.', '2'); Collate=@(); Tags=@('A1','B1','A2'); Position=@(1); Version=2 },
    @{ Name='collate'; Primary='inputs/a.pdf'; Pages=@('.', '1-2', 'inputs/b.pdf', '1-2'); Collate=@('--collate=1'); Tags=@('A1','B1','A2','B2'); Position=@(1); Version=2 },
    @{ Name='empty'; Primary='--empty'; Pages=@('inputs/b.pdf', '2,1'); Collate=@(); Tags=@('B2','B1'); Position=@(); Version=2 },
    @{ Name='v1'; Primary='inputs/a.pdf'; Pages=@('.', '2,1'); Collate=@(); Tags=@('A2','A1'); Position=@(2); Version=1 }
  )) {
    $case = New-Case ('json-page-assembly-' + $row.Name)
    Copy-Item -LiteralPath (Join-Path $fixtureDir 'json-assembly-a.pdf') -Destination "$case/inputs/a.pdf"
    Copy-Item -LiteralPath (Join-Path $fixtureDir 'json-assembly-b.pdf') -Destination "$case/inputs/b.pdf"
    $selection = @($row.Primary, '--pages') + $row.Pages + @('--') + $row.Collate
    $jsonOptions = @("--json=$($row.Version)", '--json-stream-data=inline')
    $code = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/report.json=$case/lib.json", '--') + $selection + $jsonOptions + @('outputs/report.json'))
    if ($code -ne 0) { throw "JSON assembly/$($row.Name): managed execution failed ($code)." }
    $managed = Get-JsonAssemblyProjection (Get-Content -LiteralPath "$case/lib.json" -Raw | ConvertFrom-Json -AsHashtable)
    $code = Invoke-Oracle $case ($selection + $jsonOptions)
    if ($code -ne 0) { throw "JSON assembly/$($row.Name): native inspection failed ($code)." }
    Copy-Item -LiteralPath "$case/ora-stdout.txt" -Destination "$case/direct-native.json"
    $native = Get-JsonAssemblyProjection (Get-Content -LiteralPath "$case/direct-native.json" -Raw | ConvertFrom-Json -AsHashtable)
    foreach ($projection in @($managed, $native)) {
      if (-not (Test-JsonAssemblySequence $projection.Tags $row.Tags) -or -not (Test-JsonAssemblySequence $projection.Positions $row.Position)) {
        throw "JSON assembly/$($row.Name): page identities or named-outline positions disagree."
      }
    }
    $code = Invoke-Oracle $case ($selection + @('--decrypt', "$case/selected.pdf"))
    if ($code -ne 0) { throw "JSON assembly/$($row.Name): native PDF selection failed ($code)." }
    $code = Invoke-Oracle $case @("$case/selected.pdf", '--json=2')
    if ($code -ne 0) { throw "JSON assembly/$($row.Name): selected PDF inspection failed ($code)." }
    Copy-Item -LiteralPath "$case/ora-stdout.txt" -Destination "$case/selected-native.json"
    $selected = Get-JsonAssemblyProjection (Get-Content -LiteralPath "$case/selected-native.json" -Raw | ConvertFrom-Json -AsHashtable)
    if (-not (Test-JsonAssemblySequence $managed.Labels $selected.Labels) -or -not (Test-JsonAssemblySequence $managed.Labels $row.Tags)) {
      throw "JSON assembly/$($row.Name): labels do not follow their source occurrences."
    }
    $wrong = @($managed.Tags); $wrong[0] += '-changed'
    if (Test-JsonAssemblySequence $wrong $row.Tags) { throw 'JSON assembly tag negative control accepted altered content.' }
    if (Test-JsonAssemblySequence @() $managed.Labels) { throw 'JSON assembly label negative control accepted missing labels.' }
    if ($row.Version -eq 2) {
      $code = Invoke-Oracle $case @('--json-input', '--decrypt', "$case/lib.json", "$case/created.pdf")
      if ($code -ne 0) { throw "JSON assembly/$($row.Name): native JSON creation failed ($code)." }
      Assert-Trio ('json-page-assembly/' + $row.Name) "$case/created.pdf" "$case/selected.pdf" $row.Tags.Count
    }
    Write-Output "[json-page-assembly/$($row.Name)] pages, labels and named positions agree; negative controls reject."
  }
}
