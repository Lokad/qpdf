function Get-JsonImportContent {
  param([string]$Path)
  $json=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
  $table=$json.qpdf[1]
  $page=$table['obj:'+$json.pages[0].object].value
  return $table['obj:'+$page['/Contents']].stream
}

function Invoke-ReferenceJsonImportCompression {
  $case=New-Case 'json-import-compression'
  $plain=[Text.Encoding]::ASCII.GetBytes("q 1 0 0 1 2 3 cm Q`n"*128)
  $sink=[IO.MemoryStream]::new()
  try {
    $encoder=[IO.Compression.ZLibStream]::new($sink,[IO.Compression.CompressionLevel]::Fastest,$true)
    try { $encoder.Write($plain,0,$plain.Length) }
    finally { $encoder.Dispose() }
    $encoded=$sink.ToArray()
  } finally { $sink.Dispose() }
  $payload=[Convert]::ToBase64String($plain)
  $original=[Convert]::ToBase64String($encoded)
  $objects=@{
    'obj:1 0 R'=@{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R' } }
    'obj:2 0 R'=@{ value=@{ '/Type'='/Pages'; '/Kids'=@('3 0 R'); '/Count'=1 } }
    'obj:3 0 R'=@{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,100); '/Contents'='4 0 R'; '/Resources'=@{} } }
    'obj:4 0 R'=@{ stream=@{ dict=@{}; data=$payload } }
    trailer=@{ value=@{ '/Root'='1 0 R'; '/Size'=5 } }
  }
  $model=@{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) }
  $model | ConvertTo-Json -Depth 12 | Set-Content "$case/inputs/plain.json"
  $objects['obj:4 0 R'].stream=@{ dict=@{ '/Filter'='/FlateDecode' }; data=$original }
  $model | ConvertTo-Json -Depth 12 | Set-Content "$case/inputs/encoded.json"
  $code=Invoke-Oracle $case @('--json-input','inputs/plain.json','--stream-data=preserve',"$case/inputs/source.pdf")
  if ($code -ne 0) { throw "JSON import fixture creation failed ($code)." }
  '{"qpdf":[{"jsonversion":2},{}]}' | Set-Content "$case/inputs/patch.json"
  $flate='/FlateDecode'
  foreach ($row in @(
    @{ Name='default'; Flags=@('--json-input','inputs/plain.json'); Filter=$flate },
    @{ Name='preserve'; Flags=@('--json-input','inputs/encoded.json','--stream-data=preserve'); Filter=$flate; Exact=$original },
    @{ Name='uncompress'; Flags=@('--json-input','inputs/encoded.json','--stream-data=uncompress'); Filter=$null },
    @{ Name='compress'; Flags=@('--json-input','inputs/plain.json','--compress-streams=y'); Filter=$flate },
    @{ Name='recompress'; Flags=@('--json-input','inputs/encoded.json','--recompress-flate','--compression-level=9'); Filter=$flate },
    @{ Name='update-level'; Job=@{ inputFile='inputs/source.pdf'; updateFromJson='inputs/patch.json'; compressionLevel='9' }; Filter=$flate }
  )) {
    $name=$row.Name
    $lib="$case/lib-$name.pdf"; $ora="$case/ora-$name.pdf"
    if ($row.Job) {
      $job=$row.Job.Clone(); $job.outputFile='output'
      $job | ConvertTo-Json -Depth 12 | Set-Content "$case/lib-$name-job.json"
      $code=Invoke-Emit $case @('emit-job','--workdir',$case,'--job',"$case/lib-$name-job.json",'--out',"output=$lib")
      if ($code -ne 0) { throw "JSON import $name managed job failed ($code)." }
      $job.outputFile=$ora
      $job | ConvertTo-Json -Depth 12 | Set-Content "$case/ora-$name-job.json"
      $code=Invoke-Oracle $case @("--job-json-file=$case/ora-$name-job.json")
    } else {
      $code=Invoke-Emit $case (@('emit','--workdir',$case,'--out',"output=$lib",'--')+$row.Flags+@('output'))
      if ($code -ne 0) { throw "JSON import $name managed command failed ($code)." }
      $code=Invoke-Oracle $case ($row.Flags+@($ora))
    }
    if ($code -ne 0) { throw "JSON import $name native command failed ($code)." }
    Assert-Trio "json-import-compression/$name" $lib $ora 1
    foreach ($pdf in @($lib,$ora)) {
      foreach ($level in @('none','all')) {
        $report="$pdf.$level.json"
        $code=Invoke-Oracle $case @($pdf,'--json=2','--json-key=pages','--json-key=qpdf',
          '--json-stream-data=inline',"--decode-level=$level",$report)
        if ($code -ne 0) { throw "JSON import $name/$level inspection failed ($code)." }
      }
      $raw=Get-JsonImportContent "$pdf.none.json"
      if ($raw.dict['/Filter'] -cne $row.Filter -or ($row.Exact -and $raw.data -cne $row.Exact)) {
        throw "JSON import $name raw stream policy differs."
      }
      $decoded=Get-JsonImportContent "$pdf.all.json"
      if ($decoded.data -cne $payload) { throw "JSON import $name decoded content differs." }
      $damaged=[Convert]::FromBase64String($decoded.data)
      $damaged[0]=$damaged[0] -bxor 1
      $decoded.data=[Convert]::ToBase64String($damaged)
      if ($decoded.data -ceq $payload) { throw "JSON import $name altered content was accepted." }
    }
    Write-Output "[json-import-compression/$name] raw filters and decoded bytes agree. Altered content is rejected."
  }
}
