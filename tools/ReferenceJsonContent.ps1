# Development-only JSON content effects, independent of serializer success.
function Get-JsonContentProjection {
  param($Json)
  $table = $Json.qpdf[1]
  $catalog = $table['obj:' + $table.trailer.value['/Root']].value
  $pages = $table['obj:' + $catalog['/Pages']].value
  $page = $table['obj:' + $pages['/Kids'][0]].value
  $contents = $page['/Contents']
  if ($contents -is [string]) {
    $entry = $table['obj:' + $contents]
    $contents = if ($entry.ContainsKey('value')) { $entry.value } else { @($contents) }
  }
  $hashes = [Collections.Generic.List[string]]::new()
  foreach ($reference in $contents) {
    $data = [Convert]::FromBase64String($table['obj:' + $reference].stream.data)
    $hashes.Add([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)))
  }
  $resources = $page['/Resources']
  if ($resources -is [string]) { $resources = $table['obj:' + $resources].value }
  $objects = $resources['/XObject']
  if ($objects -is [string]) { $objects = $table['obj:' + $objects].value }
  $images = [Collections.Generic.List[string]]::new()
  if ($null -ne $objects) {
    foreach ($reference in $objects.Values) {
      $stream = $table['obj:' + $reference].stream
      if ($stream.dict['/Subtype'] -ne '/Image') { throw 'JSON content control has a non-image XObject.' }
      $images.Add([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($stream.data))))
    }
  }
  $signature = [ordered]@{ contents=$hashes.ToArray(); images=@($images.ToArray() | Sort-Object)
    called=$Json.qpdf[0].calledgetallpages; pushed=$Json.qpdf[0].pushedinheritedpageresources } | ConvertTo-Json -Depth 5 -Compress
  return [pscustomobject]@{ Signature=$signature; Contents=@($contents); ImageHashes=$images.ToArray(); Table=$table }
}

function Invoke-ReferenceJsonContent {
  $case = New-Case 'json-content'
  $first = [byte[]]([Text.Encoding]::ASCII.GetBytes("q % FIRST`nBI /W 1 /H 1 /BPC 8 /CS /Pixel ID ") + @(255,0,0) + [Text.Encoding]::ASCII.GetBytes(' EI'))
  $second = [Text.Encoding]::ASCII.GetBytes('Q % SECOND')
  $imageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]@(255,0,0,32)))
  $objects = @{
    'obj:1 0 R' = @{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R' } }
    'obj:2 0 R' = @{ value=@{ '/Type'='/Pages'; '/Count'=1; '/Kids'=@('3 0 R') } }
    'obj:3 0 R' = @{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,100,200); '/Contents'='6 0 R'; '/Resources'='7 0 R' } }
    'obj:4 0 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($first) } }
    'obj:5 0 R' = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($second) } }
    'obj:6 0 R' = @{ value=@('4 0 R','5 0 R') }
    'obj:7 0 R' = @{ value=@{ '/ColorSpace'=@{ '/Pixel'='/DeviceRGB' } } }
    trailer = @{ value=@{ '/Root'='1 0 R'; '/Size'=8 } }
  }
  @{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) } | ConvertTo-Json -Depth 12 | Set-Content "$case/source.json"
  $code = Invoke-Oracle $case @('--json-input', "$case/source.json", '--stream-data=preserve', '--object-streams=disable', "$case/inputs/a.pdf")
  if ($code -ne 0) { throw "JSON content fixture creation failed ($code)." }
  foreach ($row in @(
    @{ Name='baseline'; Flags=@(); Fragments=2; Images=0; Called=$false },
    @{ Name='coalesce'; Flags=@('--coalesce-contents'); Fragments=1; Images=0; Called=$true },
    @{ Name='externalize'; Flags=@('--externalize-inline-images','--ii-min-bytes=0'); Fragments=1; Images=1; Called=$true },
    @{ Name='threshold'; Flags=@('--externalize-inline-images','--ii-min-bytes=1024'); Fragments=2; Images=0; Called=$true },
    @{ Name='both'; Flags=@('--coalesce-contents','--externalize-inline-images','--ii-min-bytes=0'); Fragments=1; Images=1; Called=$true },
    @{ Name='normalize-ignore'; Flags=@('--normalize-content=y'); Fragments=2; Images=0; Called=$false }
  )) {
    $name = $row.Name
    $arguments = @('inputs/a.pdf','--json-output=2','--decode-level=none') + $row.Flags
    $code = Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report=$case/lib-$name.json",'--') + $arguments + @('report'))
    if ($code -ne 0) { throw "JSON content/$name managed export failed ($code)." }
    $code = Invoke-Oracle $case ($arguments + @("$case/ora-$name.json"))
    if ($code -ne 0) { throw "JSON content/$name native export failed ($code)." }
    $managed = Get-Content -LiteralPath "$case/lib-$name.json" -Raw | ConvertFrom-Json -AsHashtable
    $native = Get-Content -LiteralPath "$case/ora-$name.json" -Raw | ConvertFrom-Json -AsHashtable
    $actual = Get-JsonContentProjection $managed; $expected = Get-JsonContentProjection $native
    if ($actual.Signature -cne $expected.Signature -or $actual.Contents.Count -ne $row.Fragments -or $actual.ImageHashes.Count -ne $row.Images) {
      throw "JSON content/$name content, image payload or preparation metadata differs."
    }
    if ($managed.qpdf[0].calledgetallpages -ne $row.Called -or $managed.qpdf[0].pushedinheritedpageresources) { throw "JSON content/$name preparation flags are incorrect." }
    if ($row.Images -eq 1 -and $actual.ImageHashes[0] -cne $imageHash) { throw "JSON content/$name image samples changed." }
    foreach ($engine in @('lib','ora')) {
      $code = Invoke-Oracle $case @('--json-input', "$case/$engine-$name.json", "$case/$engine-$name.pdf")
      if ($code -ne 0) { throw "JSON content/$name/$engine recreation failed ($code)." }
    }
    Assert-Trio "json-content/$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 1
    $stream = $actual.Table['obj:' + $actual.Contents[0]].stream
    $data = [Convert]::FromBase64String($stream.data); $data[0] = $data[0] -bxor 1
    $stream.data = [Convert]::ToBase64String($data)
    if ((Get-JsonContentProjection $managed).Signature -ceq $expected.Signature) { throw "JSON content/$name altered-payload control was accepted." }
    Write-Output "[json-content/$name] payloads, image samples and flags agree; recreation is valid and altered content rejects."
  }
}
