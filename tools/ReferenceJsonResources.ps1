# Development-only selected-resource effects, independent of serializer success.
function Get-JsonResourceValue {
  param($Table, $Value)
  for ($depth=0; $depth -lt 10; $depth++) {
    if ($Value -isnot [string] -or $Value -notmatch '^\d+ \d+ R$') { return $Value }
    $entry=$Table['obj:'+$Value]
    $Value=if ($entry.ContainsKey('value')) { $entry.value } else { $entry.stream.dict }
  }
  throw 'Resource fixture has an unexpected reference cycle.'
}

function Get-JsonResourceProjection {
  param($Json)
  $table=$Json.qpdf[1]
  $catalog=Get-JsonResourceValue $table $table.trailer.value['/Root']
  $root=Get-JsonResourceValue $table $catalog['/Pages']
  $fonts=[Collections.Generic.List[string]]::new()
  $maps=[Collections.Generic.List[object]]::new()
  foreach ($reference in $root['/Kids']) {
    $page=Get-JsonResourceValue $table $reference
    for ($depth=0; -not $page.ContainsKey('/Resources') -and $depth -lt 4; $depth++) {
      $page=Get-JsonResourceValue $table $page['/Parent']
    }
    $resources=Get-JsonResourceValue $table $page['/Resources']
    $bindings=Get-JsonResourceValue $table $resources['/Font']
    $maps.Add($bindings)
    $names=foreach ($key in ($bindings.Keys | Sort-Object -CaseSensitive)) {
      $font=Get-JsonResourceValue $table $bindings[$key]
      $key+'='+$font['/BaseFont']
    }
    $fonts.Add(($names -join ','))
  }
  $private=''
  if ($catalog.ContainsKey('/PrivateResources')) {
    $resources=Get-JsonResourceValue $table $catalog['/PrivateResources']
    $bindings=Get-JsonResourceValue $table $resources['/Font']
    $names=foreach ($key in ($bindings.Keys | Sort-Object -CaseSensitive)) {
      $font=Get-JsonResourceValue $table $bindings[$key]
      $key+'='+$font['/BaseFont']
    }
    $private=$names -join ','
  }
  return [pscustomobject]@{ Fonts=$fonts.ToArray(); FontMaps=$maps.ToArray(); Private=$private
    Called=$Json.qpdf[0].calledgetallpages; Pushed=$Json.qpdf[0].pushedinheritedpageresources }
}

function Test-JsonResourceProjection {
  param($Projection, [string[]]$Expected, [bool]$Called, [bool]$Pushed, [string]$Private)
  if ($Projection.Fonts.Count -ne $Expected.Count -or $Projection.Called -ne $Called -or
      $Projection.Pushed -ne $Pushed -or $Projection.Private -cne $Private) { return $false }
  for ($index=0; $index -lt $Expected.Count; $index++) {
    if ($Projection.Fonts[$index] -cne $Expected[$index]) { return $false }
  }
  return $true
}

function Invoke-ReferenceJsonResources {
  $case=New-Case 'json-resource-pruning'
  $content=[Text.Encoding]::ASCII.GetBytes('BT /F1 12 Tf 10 10 Td (USED) Tj ET')
  $fonts=@{ '/Font'=@{ '/F1'='8 0 R'; '/F2'='9 0 R' } }
  foreach ($shape in @('shared','independent')) {
    $objects=@{
      'obj:1 0 R'=@{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R'; '/PrivateResources'='5 0 R' } }
      'obj:2 0 R'=@{ value=@{ '/Type'='/Pages'; '/Count'=2; '/Kids'=@('3 0 R','4 0 R') } }
      'obj:3 0 R'=@{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,100); '/Contents'='6 0 R' } }
      'obj:4 0 R'=@{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,100); '/Contents'='7 0 R' } }
      'obj:5 0 R'=@{ value=$fonts }
      'obj:6 0 R'=@{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($content) } }
      'obj:7 0 R'=@{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($content) } }
      'obj:8 0 R'=@{ value=@{ '/Type'='/Font'; '/Subtype'='/Type1'; '/BaseFont'='/Helvetica' } }
      'obj:9 0 R'=@{ value=@{ '/Type'='/Font'; '/Subtype'='/Type1'; '/BaseFont'='/Courier' } }
      trailer=@{ value=@{ '/Root'='1 0 R'; '/Size'=10 } }
    }
    if ($shape -eq 'shared') { $objects['obj:2 0 R'].value['/Resources']='5 0 R' }
    else {
      $objects['obj:3 0 R'].value['/Resources']=$fonts
      $objects['obj:4 0 R'].value['/Resources']=$fonts
    }
    @{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) } | ConvertTo-Json -Depth 12 | Set-Content "$case/inputs/$shape.json"
    $code=Invoke-Oracle $case @('--json-input',"inputs/$shape.json",'--stream-data=preserve','--object-streams=disable',"$case/inputs/$shape.pdf")
    if ($code -ne 0) { throw "Resource/$shape fixture creation failed ($code)." }
  }
  $both='/F1=/Helvetica,/F2=/Courier'; $used='/F1=/Helvetica'
  $select=@('--pages','.','1','--')
  foreach ($row in @(
    @{ Name='baseline'; Source='shared'; Flags=@(); Expected=@($both,$both); Called=$false; Pushed=$false; Private=$both },
    @{ Name='standalone-yes'; Source='shared'; Flags=@('--remove-unreferenced-resources=yes'); Expected=@($both,$both); Called=$false; Pushed=$false; Private=$both },
    @{ Name='coalesce-alone'; Source='shared'; Flags=@('--coalesce-contents','--remove-unreferenced-resources=yes'); Expected=@($both,$both); Called=$true; Pushed=$false; Private=$both },
    @{ Name='default'; Source='shared'; Flags=$select; Expected=@($used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='auto'; Source='shared'; Flags=$select+@('--remove-unreferenced-resources=auto'); Expected=@($used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='yes'; Source='shared'; Flags=$select+@('--remove-unreferenced-resources=yes'); Expected=@($used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='no'; Source='shared'; Flags=$select+@('--remove-unreferenced-resources=no'); Expected=@($both); Called=$true; Pushed=$true; Private=$both },
    @{ Name='independent-default'; Source='independent'; Flags=$select; Expected=@($both); Called=$true; Pushed=$true; Private=$both },
    @{ Name='independent-auto'; Source='independent'; Flags=$select+@('--remove-unreferenced-resources=auto'); Expected=@($both); Called=$true; Pushed=$true; Private=$both },
    @{ Name='independent-yes'; Source='independent'; Flags=$select+@('--remove-unreferenced-resources=yes'); Expected=@($used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='coalesce-auto'; Source='shared'; Flags=$select+@('--coalesce-contents'); Expected=@($used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='repeated'; Source='shared'; Flags=@('--pages','.','1,1','--','--remove-unreferenced-resources=yes'); Expected=@($used,$used); Called=$true; Pushed=$true; Private=$both },
    @{ Name='empty'; Source=$null; Flags=@('--pages','inputs/shared.pdf','1','--'); Expected=@($used); Called=$true; Pushed=$true; Private='' },
    @{ Name='mixed'; Source='independent'; Flags=@('--pages','.','1','inputs/shared.pdf','1','--'); Expected=@($both,$used); Called=$true; Pushed=$true; Private=$both }
  )) {
    $name=$row.Name
    $inputArgument=if ($null -eq $row.Source) { '--empty' } else { "inputs/$($row.Source).pdf" }
    $arguments=@($inputArgument,'--json-output=2','--decode-level=none')+$row.Flags
    $code=Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report=$case/lib-$name.json",'--')+$arguments+@('report'))
    if ($code -ne 0) { throw "JSON resources/$name managed export failed ($code)." }
    $code=Invoke-Oracle $case ($arguments+@("$case/ora-$name.json"))
    if ($code -ne 0) { throw "JSON resources/$name native export failed ($code)." }
    foreach ($engine in @('lib','ora')) {
      $json=Get-Content -LiteralPath "$case/$engine-$name.json" -Raw | ConvertFrom-Json -AsHashtable
      $projection=Get-JsonResourceProjection $json
      if (-not (Test-JsonResourceProjection $projection $row.Expected $row.Called $row.Pushed $row.Private)) { throw "JSON resources/$name/$engine bindings or flags differ." }
      $code=Invoke-Oracle $case @('--json-input',"$case/$engine-$name.json","$case/$engine-$name.pdf")
      if ($code -ne 0) { throw "JSON resources/$name/$engine recreation failed ($code)." }
      $null=$projection.FontMaps[0].Remove('/F1')
      if (Test-JsonResourceProjection (Get-JsonResourceProjection $json) $row.Expected $row.Called $row.Pushed $row.Private) {
        throw "JSON resources/$name/$engine missing used-font control was accepted."
      }
    }
    Assert-Trio "json-resource-pruning/$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" $row.Expected.Count
    Write-Output "[json-resource-pruning/$name] active/private bindings and flags agree; recreation passes and altered resource bindings reject."
  }
  Assert-BoundJsonResourcePruning $case $both $used
}

function Assert-BoundJsonResourcePruning {
  param([string]$Case, [string]$Both, [string]$Used)
  if ((Invoke-Oracle $Case @('inputs/shared.pdf','--json=2','--json-key=pages',"$Case/bound-pages.json")) -ne 0) {
    throw 'Unable to locate the bound resource fixture stream.'
  }
  $source=Get-Content -LiteralPath "$Case/bound-pages.json" -Raw | ConvertFrom-Json
  $contentKey='obj:'+$source.pages[0].contents[0]
  $changed=[Text.Encoding]::ASCII.GetBytes('BT /F2 12 Tf 10 10 Td (PATCHED) Tj ET')
  $replacement=@{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($changed) } }
  @{ qpdf=@(@{ jsonversion=2 },@{ $contentKey=$replacement }) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$Case/inputs/resource-patch.json"
  $inputs=@(Get-ChildItem -LiteralPath "$Case/inputs" -File | Get-FileHash)
  foreach ($row in @(
    @{ Name='shared-default'; Args=@('inputs/shared.json','--json-input'); Split=$true; Expected=@($Used,$Used) },
    @{ Name='independent-auto'; Args=@('inputs/independent.json','--json-input','--remove-unreferenced-resources=auto'); Split=$true; Expected=@($Both,$Both) },
    @{ Name='shared-no'; Args=@('inputs/shared.json','--json-input','--remove-unreferenced-resources=no'); Split=$true; Expected=@($Both,$Both) },
    @{ Name='updated-yes'; Args=@('inputs/shared.pdf','--update-from-json=inputs/resource-patch.json','--remove-unreferenced-resources=yes'); Split=$true; Expected=@('/F2=/Courier',$Used) },
    @{ Name='whole-yes'; Args=@('inputs/shared.json','--json-input','--remove-unreferenced-resources=yes'); Split=$false; Expected=@($Both,$Both) }
  )) {
    $tag="json-bound-resource-pruning/$($row.Name)"
    $prefix="bound-$($row.Name)"
    $arguments=$row.Args+@('--stream-data=preserve')
    $parts=@(if ($row.Split) { '-1','-2' } else { '' })
    if ($row.Split) { $arguments+=@('--split-pages=1') }
    $mappings=foreach ($suffix in $parts) {
      '--out'; "$prefix$suffix.pdf=$Case/lib-$prefix$suffix.pdf"
    }
    $managed=Invoke-Emit $Case (@('emit','--workdir',$Case)+$mappings+@('--')+$arguments+@("$prefix.pdf"))
    $native=Invoke-Oracle $Case ($arguments+@("$Case/ora-$prefix.pdf"))
    if ($managed -ne 0 -or $native -ne 0) { throw "$tag did not publish both outputs." }
    for ($index=0; $index -lt $parts.Count; $index++) {
      $suffix=$parts[$index]
      $lib="$Case/lib-$prefix$suffix.pdf"; $ora="$Case/ora-$prefix$suffix.pdf"
      $expected=if ($row.Split) { $row.Expected[$index] } else { $row.Expected -join '|' }
      Assert-Trio "$tag$suffix" $lib $ora $(if ($row.Split) { 1 } else { 2 })
      foreach ($pdf in @($lib,$ora)) {
        $report="$pdf.reference.json"
        if ((Invoke-Oracle $Case @($pdf,'--json=2','--json-key=qpdf',$report)) -ne 0) {
          throw "$tag could not inspect the output resource graph."
        }
        $view=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
        $projection=Get-JsonResourceProjection $view
        if (($projection.Fonts -join '|') -cne $expected -or ($pdf -eq $lib -and $projection.Private -cne $Both)) {
          throw "$tag changed an active or managed private resource binding."
        }
      }
    }
    Write-Output "[$tag] active font bindings agree and managed private resources survive."
  }
  if (Compare-Object $inputs @(Get-ChildItem -LiteralPath "$Case/inputs" -File | Get-FileHash) -Property Path,Hash) {
    throw 'Bound resource pruning changed an input.'
  }
}
