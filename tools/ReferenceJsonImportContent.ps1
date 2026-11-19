function Invoke-ReferenceJsonImportContent {
  $case = New-Case 'json-import-content'
  $ascii = [Text.Encoding]::ASCII
  $first = "q`n0 0 10 12 re`nf`n"
  $changed = "q`n0 0 24 29 re`nf`n"
  $last = "Q`n"
  $page = @{
    '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,100)
    '/Resources'=@{}; '/Contents'='11 0 R'
  }
  $objects = @{
    'obj:1 0 R' = @{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R'; '/Archive'='11 0 R' } }
    'obj:2 0 R' = @{ value=@{ '/Type'='/Pages'; '/Count'=2; '/Kids'=@('3 0 R',$page) } }
    'obj:3 0 R' = @{ value=$page }
    'obj:7 0 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($first)) } }
    'obj:8 0 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($last)) } }
    'obj:11 0 R' = @{ value=@('7 0 R','8 0 R') }
    trailer = @{ value=@{ '/Root'='1 0 R'; '/Size'=12 } }
  }
  @{ qpdf=@(@{ jsonversion=2; pdfversion='1.4' },$objects) } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$case/inputs/model.json"
  if ((Invoke-Oracle $case @('inputs/model.json','--json-input','--stream-data=preserve','inputs/source.pdf')) -ne 0) {
    throw 'Unable to create the coalescing update source.'
  }
  $code = Invoke-Oracle $case @('inputs/source.pdf','--json=2','--json-key=pages','inputs/source-pages.json')
  if ($code -ne 3 -or (Get-Content -LiteralPath "$case/ora-stderr.txt" -Raw) -notmatch 'is direct; converting to indirect') {
    throw 'Native coalescing source did not expose the direct page control.'
  }
  $source = Get-Content -LiteralPath "$case/inputs/source-pages.json" -Raw | ConvertFrom-Json
  $contentKey = 'obj:' + $source.pages[0].contents[0]
  $replacement = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($changed)) } }
  @{ qpdf=@(@{ jsonversion=2 },@{ $contentKey=$replacement }) } |
    ConvertTo-Json -Depth 9 | Set-Content -LiteralPath "$case/inputs/patch.json"
  $inputs = @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash)

  foreach ($row in @(@{ Name='create'; First=$first },@{ Name='update'; First=$changed })) {
    $tag = "json-import-content/$($row.Name)"
    $lib = "$case/lib-$($row.Name).pdf"; $oracle = "$case/ora-$($row.Name).pdf"
    if ($row.Name -eq 'create') {
      $flags = @('inputs/model.json','--json-input','--coalesce-contents','--stream-data=preserve')
      $managed = Invoke-Emit $case (@('emit','--workdir',$case,'--out',"output=$lib",'--') + $flags + @('output'))
      $native = Invoke-Oracle $case ($flags + @($oracle))
    } else {
      $job = @{ inputFile='inputs/source.pdf'; updateFromJson='inputs/patch.json'; coalesceContents='';
        streamData='preserve'; outputFile='output' }
      $managedJob = "$case/managed-update.json"
      $job | ConvertTo-Json | Set-Content -LiteralPath $managedJob
      $managed = Invoke-Emit $case @('emit-job','--workdir',$case,'--job',$managedJob,'--out',"output=$lib")
      $job.outputFile = $oracle
      $nativeJob = "$case/native-update.json"
      $job | ConvertTo-Json | Set-Content -LiteralPath $nativeJob
      $native = Invoke-Oracle $case @("--job-json-file=$nativeJob")
    }
    if ($managed -ne 0 -or $native -ne 3 -or
        (Get-Content -LiteralPath "$case/ora-stderr.txt" -Raw) -notmatch 'is direct; converting to indirect') {
      throw "$tag failed to publish the coalesced graph with the expected direct-page handling."
    }
    Assert-Trio $tag $lib $oracle 2
    foreach ($pdf in @($lib,$oracle)) {
      $report = "$pdf.json"
      if ((Invoke-Oracle $case @($pdf,'--json=2','--json-key=pages','--json-key=qpdf','--json-stream-data=inline',
          '--decode-level=generalized',$report)) -ne 0) { throw "$tag cannot inspect the coalesced graph." }
      $view = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
      $table = $view.qpdf[1]
      foreach ($pageInfo in $view.pages) {
        if ($pageInfo.contents.Count -ne 1) { throw "$tag did not replace the visible contents array." }
        $body = $table['obj:'+$pageInfo.contents[0]].stream.data
        if ($ascii.GetString([Convert]::FromBase64String($body)) -cne ($row.First + $last)) {
          throw "$tag altered the referenced joined program."
        }
      }
      $catalog = $table['obj:'+$table.trailer.value['/Root']].value
      $archive = $table['obj:'+$catalog['/Archive']].value
      if ($archive.Count -ne 2) { throw "$tag lost the private two-stream array." }
      for ($i = 0; $i -lt 2; $i++) {
        $body = $table['obj:'+$archive[$i]].stream.data
        $expected = if ($i -eq 0) { $row.First } else { $last }
        if ($ascii.GetString([Convert]::FromBase64String($body)) -cne $expected) {
          throw "$tag changed a privately referenced stream."
        }
      }
    }
    Write-Output "[$tag] joined programs and both private chunks agree."
  }
  if (Compare-Object $inputs @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash) -Property Path,Hash) {
    throw 'JSON content coalescing changed an input.'
  }
}

function Invoke-ReferenceBoundNormalization {
  $case = New-Case 'json-import-normalize'
  $ascii = [Text.Encoding]::ASCII
  $first = "q`r`n0 0 10 12 re f`r`nQ`r`n"
  $changed = "q`r`n0 0 24 29 re f`r`nQ`r`n"
  $objects = @{
    'obj:1 0 R' = @{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R'; '/Audit'='4 7 R' } }
    'obj:2 0 R' = @{ value=@{ '/Type'='/Pages'; '/Count'=1; '/Kids'=@('3 0 R'); '/MediaBox'=@(0,0,100,100); '/Resources'=@{} } }
    'obj:3 0 R' = @{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/Contents'='4 7 R' } }
    'obj:4 7 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($first)) } }
    trailer = @{ value=@{ '/Root'='1 0 R'; '/Size'=5 } }
  }
  @{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$case/inputs/model.json"
  if ((Invoke-Oracle $case @('inputs/model.json','--json-input','--stream-data=preserve','inputs/source.pdf')) -ne 0 -or
      (Invoke-Oracle $case @('inputs/source.pdf','--json=2','--json-key=pages','source-pages.json')) -ne 0) {
    throw 'Unable to prepare the normalization update source.'
  }
  $source = Get-Content -LiteralPath "$case/source-pages.json" -Raw | ConvertFrom-Json
  $contentKey = 'obj:' + $source.pages[0].contents[0]
  $replacement = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($changed)) } }
  @{ qpdf=@(@{ jsonversion=2 },@{ $contentKey=$replacement }) } |
    ConvertTo-Json -Depth 9 | Set-Content -LiteralPath "$case/inputs/patch.json"
  $inputs = @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash)

  foreach ($row in @(
    @{ Name='create'; Original=$first; Args=@('inputs/model.json','--json-input') },
    @{ Name='update'; Original=$changed; Args=@('inputs/source.pdf','--update-from-json=inputs/patch.json') }
  )) {
    $tag = "json-import-normalize/$($row.Name)"
    $lib = "$case/lib-$($row.Name).pdf"; $oracle = "$case/ora-$($row.Name).pdf"
    $flags = $row.Args + @('--normalize-content=y','--stream-data=preserve')
    if ((Invoke-Emit $case (@('emit','--workdir',$case,'--out',"output=$lib",'--') + $flags + @('output'))) -ne 0 -or
        (Invoke-Oracle $case ($flags + @($oracle))) -ne 0) {
      throw "$tag failed to publish normalized content."
    }
    Assert-Trio $tag $lib $oracle 1
    $normalized = $row.Original.Replace("`r`n", "`n")
    foreach ($pdf in @($lib,$oracle)) {
      $report = "$pdf.json"
      if ((Invoke-Oracle $case @($pdf,'--json=2','--json-key=pages','--json-key=qpdf','--json-stream-data=inline',
          '--decode-level=none',$report)) -ne 0) { throw "$tag cannot inspect the normalized graph." }
      $view = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
      $table = $view.qpdf[1]
      $catalog = $table['obj:'+$table.trailer.value['/Root']].value
      $active = $view.pages[0].contents[0]; $alias = $catalog['/Audit']
      $same = $pdf -eq $oracle
      if (($active -ceq $alias) -ne $same) { throw "$tag unexpected active stream identity." }
      $body = $ascii.GetString([Convert]::FromBase64String($table['obj:'+$active].stream.data))
      $saved = $ascii.GetString([Convert]::FromBase64String($table['obj:'+$alias].stream.data))
      $expectedAlias = if ($same) { $normalized } else { $row.Original }
      if ($body -cne $normalized -or $saved -cne $expectedAlias) { throw "$tag unexpected program bytes." }
    }
    Write-Output "[$tag] active program agrees and managed output retains the private original."
  }
  if (Compare-Object $inputs @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash) -Property Path,Hash) {
    throw 'JSON content normalization changed an input.'
  }
}
