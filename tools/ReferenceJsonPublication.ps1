function Test-JsonPublicationRoles {
  param($Json, $Expected)
  $table=$Json.qpdf[1]
  $trailer=$table.trailer.value
  $root=$table['obj:'+$trailer['/Root']].value
  $info=$table['obj:'+$trailer['/Info']].value
  $date=$info['/ModDate']
  if ($date -match '^\d+ \d+ R$') { $date=$table['obj:'+$date].value }
  $labels=$root['/PageLabels']
  if ($labels -is [string]) { $labels=$table['obj:'+$labels].value }
  $prefix=if ($null -ne $labels) { $labels['/Nums'][1]['/P'] } else { '' }
  $start=if ($null -ne $labels) { $labels['/Nums'][1]['/St'] } else { 0 }
  $private=$root['/Private']
  $priorInfo=$table['obj:'+$private['/Info']].value
  $priorLabels=$table['obj:'+$private['/Labels']].value
  $priorMeta=$table['obj:'+$private['/Meta']].stream
  $priorForm=$table['obj:'+$private['/Form']].value
  $priorStruct=$table['obj:'+$private['/Struct']].value
  if ($null -eq $priorInfo -or $null -eq $priorLabels -or $null -eq $priorMeta -or
      $null -eq $priorForm -or $null -eq $priorStruct) { return $false }
  return ($root.Contains('/Metadata') -eq $Expected.Meta -and
    $root.Contains('/AcroForm') -eq $Expected.Form -and
    $root.Contains('/StructTreeRoot') -eq $Expected.Struct -and
    $root.Contains('/MarkInfo') -eq $Expected.Struct -and
    (($info.Keys | Sort-Object -CaseSensitive) -join ',') -ceq $Expected.Info -and
    $date -ceq 'u:D:20260101000000Z' -and
    $prefix -ceq $Expected.Prefix -and $start -eq $Expected.Start -and
    $priorInfo['/Title'] -ceq 'u:PRIVATE-TITLE' -and
    $priorLabels['/Nums'][1]['/P'] -ceq 'u:OLD-' -and
    [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($priorMeta.data)) -ceq '<meta>PRIVATE</meta>' -and
    $priorForm.Contains('/Fields') -and $priorStruct['/Type'] -ceq '/StructTreeRoot')
}

function Invoke-ReferenceJsonPublication {
  $case=New-Case 'json-publication-roles'
  $objects=@{
    'obj:1 0 R'=@{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R'; '/AcroForm'='4 0 R';
      '/StructTreeRoot'='5 0 R'; '/MarkInfo'=@{ '/Marked'=$true }; '/PageLabels'='6 0 R'; '/Metadata'='7 0 R';
      '/Private'=@{ '/Form'='4 0 R'; '/Struct'='5 0 R'; '/Labels'='6 0 R'; '/Meta'='7 0 R'; '/Info'='8 0 R' } } }
    'obj:2 0 R'=@{ value=@{ '/Type'='/Pages'; '/Count'=1; '/Kids'=@('3 0 R') } }
    'obj:3 0 R'=@{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,100); '/Resources'=@{} } }
    'obj:4 0 R'=@{ value=@{ '/Fields'=@() } }
    'obj:5 0 R'=@{ value=@{ '/Type'='/StructTreeRoot'; '/K'=@() } }
    'obj:6 0 R'=@{ value=@{ '/Nums'=@(0,@{ '/S'='/D'; '/St'=1; '/P'='u:OLD-' }) } }
    'obj:7 0 R'=@{ stream=@{ dict=@{ '/Type'='/Metadata'; '/Subtype'='/XML' };
      data=[Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('<meta>PRIVATE</meta>')) } }
    'obj:8 0 R'=@{ value=@{ '/Title'='u:PRIVATE-TITLE'; '/ModDate'='9 0 R' } }
    'obj:9 0 R'=@{ value='u:D:20260101000000Z' }
    trailer=@{ value=@{ '/Root'='1 0 R'; '/Info'='8 0 R'; '/Size'=10 } }
  }
  @{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) } | ConvertTo-Json -Depth 12 | Set-Content "$case/input.json"
  $code=Invoke-Oracle $case @('--json-input',"$case/input.json",'--stream-data=preserve','--object-streams=disable',"$case/inputs/source.pdf")
  if ($code -ne 0) { throw "JSON publication fixture creation failed ($code)." }
  $kept='/ModDate,/Title'
  foreach ($row in @(
    @{ Name='baseline'; Flags=@(); Meta=$true; Form=$true; Struct=$true; Info=$kept; Prefix='u:OLD-'; Start=1; Pages=1 },
    @{ Name='info'; Flags=@('--remove-info'); Meta=$false; Form=$true; Struct=$true; Info='/ModDate'; Prefix='u:OLD-'; Start=1; Pages=1 },
    @{ Name='metadata'; Flags=@('--remove-metadata'); Meta=$false; Form=$true; Struct=$true; Info=$kept; Prefix='u:OLD-'; Start=1; Pages=1 },
    @{ Name='form'; Flags=@('--remove-acroform'); Meta=$true; Form=$false; Struct=$true; Info=$kept; Prefix='u:OLD-'; Start=1; Pages=1 },
    @{ Name='structure'; Flags=@('--remove-structure'); Meta=$true; Form=$true; Struct=$false; Info=$kept; Prefix='u:OLD-'; Start=1; Pages=1 },
    @{ Name='remove-labels'; Flags=@('--remove-page-labels'); Meta=$true; Form=$true; Struct=$true; Info=$kept; Prefix=''; Start=0; Pages=1 },
    @{ Name='labels'; Flags=@('--set-page-labels','1:D/5/NEW-','--'); Meta=$true; Form=$true; Struct=$true; Info=$kept; Prefix='u:NEW-'; Start=5; Pages=1 },
    @{ Name='composed-selection'; Flags=@('--pages','.','1,1','--','--remove-info','--remove-acroform','--remove-structure',
        '--remove-page-labels','--set-page-labels','1:D/5/NEW-','--');
      Meta=$false; Form=$false; Struct=$false; Info='/ModDate'; Prefix='u:NEW-'; Start=5; Pages=2 }
  )) {
    $name=$row.Name
    $arguments=@('inputs/source.pdf','--json-output=2','--decode-level=none')+$row.Flags
    $code=Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report=$case/lib-$name.json",'--')+$arguments+@('report'))
    if ($code -ne 0) { throw "JSON publication/$name managed export failed ($code)." }
    $code=Invoke-Oracle $case ($arguments+@("$case/ora-$name.json"))
    if ($code -ne 0) { throw "JSON publication/$name native export failed ($code)." }
    foreach ($engine in @('lib','ora')) {
      $path="$case/$engine-$name"
      $json=Get-Content -LiteralPath "$path.json" -Raw | ConvertFrom-Json -AsHashtable
      if (-not (Test-JsonPublicationRoles $json $row)) { throw "JSON publication/$name/$engine roles differ." }
      $code=Invoke-Oracle $case @('--json-input',"$path.json","$path.pdf")
      if ($code -ne 0) { throw "JSON publication/$name/$engine recreation failed ($code)." }
      $table=$json.qpdf[1]
      $table['obj:'+$table.trailer.value['/Info']].value['/ModDate']='u:D:19990101000000Z'
      if (Test-JsonPublicationRoles $json $row) { throw "JSON publication/$name/$engine altered date was accepted." }
    }
    Assert-Trio "json-publication-roles/$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" $row.Pages
    Write-Output "[json-publication-roles/$name] active and private roles agree, including rejection of the altered date."
  }
}
