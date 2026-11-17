# Full authoring grammar is distinct from tolerant metadata reading and ISO formatting.
function Get-ReferenceAuthoredDate {
  param([string] $Case, [string] $File, [string] $Field)
  $code = Invoke-Oracle $Case @('--json=2', '--json-key=qpdf', $File)
  if ($code -ne 0) { throw "Attachment timestamp object inspection failed ($code)." }
  $report = Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
  $dates = @(
    foreach ($entry in $report.qpdf[1].Values) {
      if (-not $entry.Contains('stream')) { continue }
      $parameters = $entry.stream.dict['/Params']
      if ($parameters -is [Collections.IDictionary] -and $parameters.Contains($Field)) { $parameters[$Field] }
    }
  )
  if ($dates.Count -ne 1) { throw 'Expected one authored attachment timestamp.' }
  return $dates[0]
}

function Invoke-ReferenceAttachmentDates {
  $vectors = Get-Content -LiteralPath (Join-Path $repoRoot 'tests/Lokad.Qpdf.Tests/Writing/AttachmentDateCases.json') -Raw | ConvertFrom-Json
  $cases = @($vectors.cases | ForEach-Object { @{ Name = $_.name; Value = $_.value; Valid = $_.valid; Field = 'creationdate'; Route = 'job'; Repeat = $false } })
  $cases += @(
    @{ Name = 'argv-moddate'; Value = 'D:20240102030405Z'; Valid = $true; Field = 'moddate'; Route = 'argv'; Repeat = $false },
    @{ Name = 'argv-moddate-invalid'; Value = 'bad'; Valid = $false; Field = 'moddate'; Route = 'argv'; Repeat = $false },
    @{ Name = 'repeated-creationdate'; Value = 'bad'; Valid = $false; Field = 'creationdate'; Route = 'argv'; Repeat = $true },
    @{ Name = 'repeated-moddate'; Value = 'bad'; Valid = $false; Field = 'moddate'; Route = 'argv'; Repeat = $true }
  )
  foreach ($row in $cases) {
    $name = "attachment-date-syntax-$($row.Name)"
    $case = New-Case $name
    Copy-Item -LiteralPath "$fixtureDir/minimal-1page.pdf" -Destination "$case/inputs/a.pdf"
    [IO.File]::WriteAllBytes("$case/inputs/note.bin", [byte[]]@(65))
    [IO.File]::WriteAllBytes("$case/lib.pdf", [Text.Encoding]::ASCII.GetBytes('prior'))
    [IO.File]::WriteAllBytes("$case/ora.pdf", [Text.Encoding]::ASCII.GetBytes('prior'))
    $prior = (Get-FileHash -LiteralPath "$case/lib.pdf" -Algorithm SHA256).Hash
    if ($row.Route -eq 'job') {
      $add = @{ file = 'inputs/note.bin'; key = 'K' }; $add[$row.Field] = $row.Value
      $job = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; addAttachment = @($add) }
      $job | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
      $managed = Invoke-Emit $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
      $job.outputFile = "$case/ora.pdf"
      $job | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$case/native.json" -Encoding utf8NoBOM
      $native = Invoke-Oracle $case @("--job-json-file=$case/native.json")
    }
    else {
      $options = @('--add-attachment', 'inputs/note.bin', '--key=K', "--$($row.Field)=$($row.Value)")
      if ($row.Repeat) { $options += "--$($row.Field)=D:20240102030405Z" }
      $options += '--'
      $managed = Invoke-Emit $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf') + $options + @('--', 'outputs/a.pdf'))
      $native = Invoke-Oracle $case (@('inputs/a.pdf') + $options + @("$case/ora.pdf"))
    }
    $expected = if ($row.Valid) { 0 } else { 2 }
    if ($managed -ne $expected -or $native -ne $expected) { throw "$name exit mismatch: managed $managed, native $native, expected $expected." }
    if ($row.Valid) {
      Assert-Trio $name "$case/lib.pdf" "$case/ora.pdf" 1
      $field = if ($row.Field -eq 'creationdate') { '/CreationDate' } else { '/ModDate' }
      foreach ($file in @("$case/lib.pdf", "$case/ora.pdf")) {
        if ((Get-ReferenceAuthoredDate $case $file $field) -cne ('u:' + $row.Value)) { throw "$name changed the stored timestamp." }
      }
    }
    else {
      foreach ($file in @("$case/lib.pdf", "$case/ora.pdf")) {
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $prior) { throw "$name replaced a prior target on refusal." }
      }
    }
    Write-Output "[$name] authoring syntax, stored values and prior-target policy agree"
  }
}
