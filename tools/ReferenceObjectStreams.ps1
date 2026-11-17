# Physical writer checks share native inspection with the performance validator.
function Assert-ReferenceObjectStreams {
  param([string] $File)
  $validator = Join-Path $repoRoot 'tools/object_stream_layout.py'
  $output = & python $validator --qpdf-exe $QpdfExe --input $File 2>&1 | Out-String
  $code = $LASTEXITCODE
  [IO.File]::WriteAllText($File + '.layout.json', $output)
  if ($code -ne 0) { throw "Object-stream layout validation failed ($code): $File; $output" }
}

function Invoke-ReferenceObjectStreamControls {
  $case = New-Case 'object-stream-layout-controls'
  $program = "% /Type /ObjStm /Type /XRef`nq Q`n"
  $payload = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($program))
  $model = '{"qpdf":[{"jsonversion":2,"pdfversion":"1.5"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Audit":"5 0 R"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,10,20],"/Resources":{},"/Contents":"4 0 R"}},"obj:4 0 R":{"stream":{"dict":{},"data":"PAYLOAD"}},"obj:5 0 R":{"value":{"/Private":"u:kept"}},"trailer":{"value":{"/Root":"1 0 R","/Size":6}}}]}'
  [IO.File]::WriteAllText("$case/inputs/model.json", $model.Replace('PAYLOAD', $payload), [Text.UTF8Encoding]::new($false))
  foreach ($row in @(
    @{ Name = 'markers'; Flags = @('--object-streams=disable', '--stream-data=uncompress', '--linearize') },
    @{ Name = 'raw'; Flags = @('--object-streams=generate', '--stream-data=uncompress') },
    @{ Name = 'flate-linear'; Flags = @('--object-streams=generate', '--stream-data=compress', '--linearize') }
  )) {
    $file = "$case/$($row.Name).pdf"
    if ((Invoke-Oracle $case (@('--json-input', 'inputs/model.json', '--static-id') + $row.Flags + @($file))) -ne 0) { throw 'Object-stream control preparation failed.' }
    if ((Invoke-Oracle $case @('--check', $file)) -ne 0 -or (Get-PigNpages $file) -ne 1) { throw 'Object-stream control validity/page count failed.' }
    if ($row.Flags -contains '--linearize') {
      if ((Invoke-Oracle $case @('--check-linearization', $file)) -ne 0 -or
          (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors') { throw 'Object-stream control is not physically linearized.' }
    }
    if ((Read-ReferenceContent $QpdfExe $file).PageText -cne $program) { throw 'Object-stream control content changed.' }
    if ($row.Name -eq 'markers') {
      $raw = [IO.File]::ReadAllText($file)
      if (-not $raw.Contains('/Type /ObjStm') -or -not $raw.Contains('/Type /XRef')) { throw 'Marker-only negative control lost its exact old predicate.' }
      $validator = Join-Path $repoRoot 'tools/object_stream_layout.py'
      $rejection = & python $validator --qpdf-exe $QpdfExe --input $file 2>&1 | Out-String
      if ($LASTEXITCODE -ne 2 -or $rejection -notmatch 'no compressed xref members') { throw 'Marker-only classic linearization passed the packing validator.' }
      [IO.File]::WriteAllText($file + '.rejection.txt', $rejection)
    } else {
      Assert-ReferenceObjectStreams $file
      $facts = Get-Content -LiteralPath ($file + '.layout.json') -Raw | ConvertFrom-Json
      $expected = if ($row.Name -eq 'raw') { 0 } else { 1 }
      if (($facts.filteredContainers -gt 0) -ne ($expected -eq 1)) { throw 'Object-stream control did not exercise its actual raw/filtered payload.' }
    }
  }
  Write-Output '[object-stream-layout-controls] valid marker-only linearization rejects; raw/Flate packing and physical xrefs pass'
}
