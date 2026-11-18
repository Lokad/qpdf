function Invoke-ReferenceJsonQdf {
  $case = New-Case 'json-qdf'
  $payload = "q`r`n0 0 30 40 re f`r`nQ`r`n"
  $changed = "q`n0 0 43 29 re f`nQ`n"
  $ascii = [Text.Encoding]::ASCII
  $objects = @{
    'obj:1 3 R' = @{ value=@{ '/Type'='/Catalog'; '/Pages'='2 7 R'; '/Alias'='4 4 R' } }
    'obj:2 7 R' = @{ value=@{ '/Type'='/Pages'; '/Kids'=@('3 12 R'); '/Count'=1 } }
    'obj:3 12 R' = @{ value=@{ '/Type'='/Page'; '/Parent'='2 7 R'; '/MediaBox'=@(0,0,100,100); '/Resources'=@{}; '/Contents'='4 4 R' } }
    'obj:4 4 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($payload)) } }
    trailer = @{ value=@{ '/Root'='1 3 R'; '/Size'=5 } }
  }
  @{ qpdf=@(@{ jsonversion=2; pdfversion='1.4' },$objects) } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$case/inputs/model.json"
  if ((Invoke-Oracle $case @('inputs/model.json','--json-input','--stream-data=preserve',"$case/inputs/source.pdf")) -ne 0) {
    throw 'Unable to create the reference update source.'
  }
  $sourceReport = "$case/inputs/source-objects.json"
  if ((Invoke-Oracle $case @('inputs/source.pdf','--json=2','--json-key=pages','--json-key=qpdf',$sourceReport)) -ne 0) {
    throw 'Unable to inspect reference update source pairs.'
  }
  $source = Get-Content -LiteralPath $sourceReport -Raw | ConvertFrom-Json -AsHashtable
  $content = 'obj:' + $source.pages[0].contents[0]
  $patch = @{ $content=@{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($ascii.GetBytes($changed)) } } }
  @{ qpdf=@(@{ jsonversion=2 },$patch) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/inputs/patch.json"

  foreach ($row in @(
    @{ Name='create'; Input='inputs/model.json'; Mode=@('--json-input'); Body=$payload; Pairs=$objects.Keys; Comments=$true },
    @{ Name='update'; Input='inputs/source.pdf'; Mode=@('--update-from-json=inputs/patch.json'); Body=$changed; Pairs=$source.qpdf[1].Keys; Comments=$true },
    @{ Name='no-ids'; Input='inputs/source.pdf'; Mode=@('--update-from-json=inputs/patch.json','--no-original-object-ids'); Body=$changed; Pairs=@(); Comments=$false }
  )) {
    $tag = 'json-qdf/' + $row.Name
    $lib = "$case/lib-$($row.Name).pdf"; $oracle = "$case/ora-$($row.Name).pdf"
    $args = @($row.Input,'--qdf','--normalize-content=n') + $row.Mode
    if ((Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report=$lib",'--')+$args+@('report'))) -ne 0) { throw "$tag managed QDF failed." }
    if ((Invoke-Oracle $case ($args+@($oracle))) -ne 0) { throw "$tag native QDF failed." }
    Assert-Trio $tag $lib $oracle 1
    foreach ($pdf in @($lib,$oracle)) {
      $text = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($pdf))
      if (!$text.Contains('%QDF-1.0') -or ($text.Contains('%% Original object ID: ') -ne $row.Comments)) { throw "$tag QDF markers differ." }
      foreach ($key in $row.Pairs) {
        if ($key.StartsWith('obj:') -and !$text.Contains("%% Original object ID: $($key.Substring(4).Replace(' R',''))`n")) {
          throw "$tag is missing original pair $key."
        }
      }
      $projection = "$pdf.json"
      if ((Invoke-Oracle $case @($pdf,'--json=2','--json-key=pages','--json-key=qpdf','--json-stream-data=inline',$projection)) -ne 0) {
        throw "$tag could not read published contents."
      }
      $decoded = $ascii.GetString([Convert]::FromBase64String((Get-JsonImportContent $projection).data))
      if ($decoded -cne $row.Body) { throw "$tag changed the requested opaque content." }
    }
    Write-Output "[$tag] editable layout, original pairs and referenced contents agree."
  }
}
