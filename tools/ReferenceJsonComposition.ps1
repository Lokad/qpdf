function Invoke-ReferenceJsonComposition {
  function Assert-Patch($table) {
    $catalog = $table['obj:' + $table.trailer.value['/Root']].value
    $payload = $table['obj:' + $catalog['/Keep']].stream
    if ($catalog.Contains('/Stale') -or $table['obj:' + $catalog['/Late']].value -cne 'u:late' -or
        $payload.dict['/Tag'] -cne 'u:patched' -or $payload.data -cne 'AP7/Cg==') {
      throw 'Patched graph lost the late declaration or exact borrowed payload.'
    }
  }

  function Invoke-PhysicalPatchProjection {
    if ((Invoke-Oracle $case @('inputs/native.pdf','--json-output')) -ne 0) { throw 'Source pair lookup failed.' }
    $table = (Get-Content -Raw -LiteralPath "$case/ora-stdout.txt" | ConvertFrom-Json -AsHashtable).qpdf[1]
    $rootKey = 'obj:' + $table.trailer.value['/Root']
    $catalog = $table[$rootKey].value
    $catalog['/Marker'] = 'u:physical-update'
    @{ qpdf=@(@{jsonversion=2},@{ $rootKey=@{value=$catalog} }) } |
      ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$case/inputs/physical-patch.json"
    $job = @{ inputFile='inputs/native.pdf'; updateFromJson='inputs/physical-patch.json'; jsonOutput='2'; outputFile='report.json' }
    $job | ConvertTo-Json | Set-Content -LiteralPath "$case/job.json"
    if ((Invoke-Oracle $case @('--job-json-file=job.json')) -ne 0 -or
        (Invoke-Emit $case @('emit-job','--workdir',$case,'--job',"$case/job.json",'--out',"report.json=$case/physical-managed.json")) -ne 0) {
      throw 'Physical update projection failed.'
    }
    foreach ($file in @('report.json','physical-managed.json')) {
      $projected=(Get-Content -Raw -LiteralPath "$case/$file" | ConvertFrom-Json -AsHashtable).qpdf[1]
      if ($projected[$rootKey].value['/Marker'] -cne 'u:physical-update') { throw 'Projection used a stale PDF catalog.' }
    }
    Write-Output '[json-composition/physical-update] projected patch uses the physical source pair.'
  }
  $case = New-Case 'json-composition'
  @'
{"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
  "obj:5 2 R":{"value":{"/Type":"/Catalog","/Pages":"8 0 R","/Late":"41 7 R","/Stale":"23 1 R","/Keep":"23 4 R"}},
  "obj:8 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["19 7 R"]}},
  "obj:19 7 R":{"value":{"/Type":"/Page","/Parent":"8 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},
  "obj:23 4 R":{"stream":{"dict":{"/Tag":"u:first"},"data":"AP7/Cg=="}},
  "trailer":{"value":{"/Root":"5 2 R","/Size":42}}
}]}
'@ | Set-Content -LiteralPath "$case/inputs/create.json"
  @'
{"qpdf":[{"jsonversion":2},{
  "obj:41 7 R":{"value":"u:late"},
  "obj:23 4 R":{"stream":{"dict":{"/Tag":"u:patched"}}}
}]}
'@ | Set-Content -LiteralPath "$case/inputs/patch.json"
  $binding = @('inputs/create.json','--json-input','--update-from-json=inputs/patch.json')
  $create = $binding[0..1]
  $export = @('--json-output')
  if ((Invoke-Oracle $case ($binding + @('--stream-data=preserve', 'inputs/native.pdf'))) -ne 0 -or
      (Invoke-Emit $case (@('emit','--workdir',$case,'--out',"lib.pdf=$case/lib.pdf",'--') +
        $binding + @('--stream-data=preserve','lib.pdf'))) -ne 0) {
    throw 'Create-and-patch publication failed.'
  }
  Assert-Trio 'json-composition/pdf' "$case/lib.pdf" "$case/inputs/native.pdf" 1
  foreach ($pdf in @('lib.pdf','inputs/native.pdf')) {
    if ((Invoke-Oracle $case @($pdf,'--json-output')) -ne 0) { throw "Could not inspect composed PDF $pdf." }
    $table = (Get-Content -Raw -LiteralPath "$case/ora-stdout.txt" | ConvertFrom-Json -AsHashtable).qpdf[1]
    Assert-Patch $table
  }
  foreach ($row in @(
      @{ Args=$create; Tag='create'; Patched=$false },
      @{ Args=$binding; Tag='create-update'; Patched=$true })) {
    $nativeReport = "$case/$($row.Tag)-native.json"
    $managedReport = "$case/$($row.Tag)-managed.json"
    if ((Invoke-Oracle $case ($row.Args + $export + @($nativeReport))) -ne 0 -or
        (Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report.json=$managedReport",'--') +
          $row.Args + $export + @('report.json'))) -ne 0) {
      throw "$($row.Tag) direct JSON projection failed."
    }
    foreach ($file in @($nativeReport,$managedReport)) {
      $table = (Get-Content -Raw -LiteralPath $file | ConvertFrom-Json -AsHashtable).qpdf[1]
      $catalog = $table['obj:5 2 R'].value
      if ($catalog['/Pages'] -cne '8 0 R' -or $catalog['/Keep'] -cne '23 4 R' -or
          $catalog.Contains('/Stale') -or $catalog.Contains('/Late') -ne $row.Patched) {
        throw "$($row.Tag) changed an exact declared pair."
      }
      if ($row.Patched) { Assert-Patch $table }
      elseif ($table['obj:23 4 R'].stream.dict['/Tag'] -cne 'u:first') { throw 'Unpatched payload dictionary changed.' }
    }
    Write-Output "[json-composition/$($row.Tag)] declared pairs and effective values agree."
  }
  if ((Invoke-Oracle $case @("$case/create-update-managed.json",'--json-input',"$case/recreated.pdf")) -ne 0) {
    throw 'Direct report does not recreate as an independent PDF.'
  }
  Assert-Trio 'json-composition/recreated' "$case/recreated.pdf" "$case/inputs/native.pdf" 1
  Invoke-PhysicalPatchProjection
}
