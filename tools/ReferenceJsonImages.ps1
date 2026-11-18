# Development-only image effects. JPEG bytes are encoder-specific.
function Get-JsonImageProjection {
  param($Json)
  $table = $Json.qpdf[1]
  $catalog = $table['obj:' + $table.trailer.value['/Root']].value
  $pages = $table['obj:' + $catalog['/Pages']].value
  $page = $table['obj:' + $pages['/Kids'][0]].value
  $resources = $page['/Resources']
  if ($resources -is [string]) { $resources = $table['obj:' + $resources].value }
  $objects = $resources['/XObject']
  if ($objects -is [string]) { $objects = $table['obj:' + $objects].value }
  $images = [Collections.Generic.List[object]]::new()
  if ($null -ne $objects) {
    foreach ($reference in $objects.Values) {
      $stream = $table['obj:' + $reference].stream
      $bytes = [Convert]::FromBase64String($stream.data)
      $images.Add([pscustomobject]@{ Filter=$stream.dict['/Filter']; Bytes=$bytes; Width=$stream.dict['/Width']; Height=$stream.dict['/Height'] })
    }
  }
  $private = [Convert]::FromBase64String($table['obj:' + $catalog['/PrivateImage']].stream.data)
  return [pscustomobject]@{ Images=$images.ToArray(); PrivateHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($private))
    Called=$Json.qpdf[0].calledgetallpages; Pushed=$Json.qpdf[0].pushedinheritedpageresources }
}

function Test-JsonImageProjection {
  param($Projection, [int]$Count, [bool]$Encoded, [bool]$Called, [string]$PixelHash)
  if ($Projection.Images.Count -ne $Count -or $Projection.PrivateHash -cne $PixelHash -or $Projection.Called -ne $Called -or $Projection.Pushed) { return $false }
  foreach ($image in $Projection.Images) {
    if ($image.Width -ne 128 -or $image.Height -ne 128) { return $false }
    if ($Encoded) {
      if ($image.Filter -cne '/DCTDecode' -or $image.Bytes.Length -ge 49152 -or $image.Bytes.Length -lt 4 -or
          $image.Bytes[0] -ne 255 -or $image.Bytes[1] -ne 216 -or $image.Bytes[-2] -ne 255 -or $image.Bytes[-1] -ne 217) { return $false }
    } elseif ($null -ne $image.Filter -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($image.Bytes)) -cne $PixelHash) { return $false }
  }
  return $true
}

function Invoke-ReferenceJsonImages {
  $case = New-Case 'json-image-optimization'
  $pixels = [byte[]]::new(49152)
  for ($index=0; $index -lt $pixels.Length; $index+=3) { $pixels[$index]=32; $pixels[$index+1]=96; $pixels[$index+2]=160 }
  $pixelHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels))
  foreach ($shape in @('xobject','inline')) {
    $content = if ($shape -eq 'inline') { [Text.Encoding]::ASCII.GetBytes('q BI /W 128 /H 128 /BPC 8 /CS /RGB /F /AHx ID ' + [Convert]::ToHexString($pixels) + '> EI Q') }
      else { [Text.Encoding]::ASCII.GetBytes('q /Im Do Q') }
    $resources = if ($shape -eq 'inline') { @{} } else { @{ '/XObject'=@{ '/Im'='5 0 R' } } }
    $objects = @{
      'obj:1 0 R'=@{ value=@{ '/Type'='/Catalog'; '/Pages'='2 0 R'; '/PrivateImage'='5 0 R' } }
      'obj:2 0 R'=@{ value=@{ '/Type'='/Pages'; '/Count'=1; '/Kids'=@('3 0 R') } }
      'obj:3 0 R'=@{ value=@{ '/Type'='/Page'; '/Parent'='2 0 R'; '/MediaBox'=@(0,0,128,128); '/Contents'='4 0 R'; '/Resources'=$resources } }
      'obj:4 0 R'=@{ stream=@{ dict=@{}; data=[Convert]::ToBase64String($content) } }
      'obj:5 0 R'=@{ stream=@{ dict=@{ '/Type'='/XObject'; '/Subtype'='/Image'; '/Width'=128; '/Height'=128; '/BitsPerComponent'=8; '/ColorSpace'='/DeviceRGB' }; data=[Convert]::ToBase64String($pixels) } }
      trailer=@{ value=@{ '/Root'='1 0 R'; '/Size'=6 } }
    }
    @{ qpdf=@(@{ jsonversion=2; pdfversion='1.7' },$objects) } | ConvertTo-Json -Depth 12 | Set-Content "$case/$shape.json"
    $code = Invoke-Oracle $case @('--json-input', "$case/$shape.json", '--stream-data=preserve', "$case/inputs/$shape.pdf")
    if ($code -ne 0) { throw "JSON image/$shape fixture creation failed ($code)." }
  }
  $optimize = @('--optimize-images','--oi-min-width=1','--oi-min-height=1','--oi-min-area=1','--jpeg-quality=75')
  foreach ($row in @(
    @{ Name='baseline'; Source='xobject'; Flags=@(); Count=1; Encoded=$false; Called=$false },
    @{ Name='quality-only'; Source='xobject'; Flags=@('--jpeg-quality=25'); Count=1; Encoded=$false; Called=$false },
    @{ Name='optimize'; Source='xobject'; Flags=$optimize; Count=1; Encoded=$true; Called=$true },
    @{ Name='quality'; Source='xobject'; Flags=$optimize+@('--jpeg-quality=25'); Count=1; Encoded=$true; Called=$true },
    @{ Name='width'; Source='xobject'; Flags=$optimize+@('--oi-min-width=128'); Count=1; Encoded=$false; Called=$true },
    @{ Name='height'; Source='xobject'; Flags=$optimize+@('--oi-min-height=128'); Count=1; Encoded=$false; Called=$true },
    @{ Name='area'; Source='xobject'; Flags=$optimize+@('--oi-min-area=16384'); Count=1; Encoded=$false; Called=$true },
    @{ Name='inline'; Source='inline'; Flags=$optimize+@('--ii-min-bytes=0'); Count=1; Encoded=$true; Called=$true },
    @{ Name='keep'; Source='inline'; Flags=$optimize+@('--ii-min-bytes=0','--keep-inline-images'); Count=0; Encoded=$false; Called=$true },
    @{ Name='explicit'; Source='inline'; Flags=$optimize+@('--ii-min-bytes=0','--keep-inline-images','--externalize-inline-images'); Count=1; Encoded=$true; Called=$true },
    @{ Name='inline-threshold'; Source='inline'; Flags=$optimize+@('--ii-min-bytes=1000000'); Count=0; Encoded=$false; Called=$true }
  )) {
    $name=$row.Name
    $arguments=@("inputs/$($row.Source).pdf",'--json-output=2','--decode-level=none')+$row.Flags
    $code=Invoke-Emit $case (@('emit','--workdir',$case,'--out',"report=$case/lib-$name.json",'--')+$arguments+@('report'))
    if ($code -ne 0) { throw "JSON image/$name managed export failed ($code)." }
    $code=Invoke-Oracle $case ($arguments+@("$case/ora-$name.json"))
    if ($code -ne 0) { throw "JSON image/$name native export failed ($code)." }
    foreach ($engine in @('lib','ora')) {
      $json=Get-Content -LiteralPath "$case/$engine-$name.json" -Raw | ConvertFrom-Json -AsHashtable
      $projection=Get-JsonImageProjection $json
      if (-not (Test-JsonImageProjection $projection $row.Count $row.Encoded $row.Called $pixelHash)) { throw "JSON image/$name/$engine effects or private bytes differ." }
      $code=Invoke-Oracle $case @('--json-input', "$case/$engine-$name.json", "$case/$engine-$name.pdf")
      if ($code -ne 0) { throw "JSON image/$name/$engine recreation failed ($code)." }
      if ($row.Count -eq 1) {
        $wrong=Get-JsonImageProjection $json; $wrong.Images[0].Bytes[0]=$wrong.Images[0].Bytes[0] -bxor 1
        if (Test-JsonImageProjection $wrong $row.Count $row.Encoded $row.Called $pixelHash) { throw "JSON image/$name/$engine altered-image control was accepted." }
      }
    }
    Assert-Trio "json-image-optimization/$name" "$case/lib-$name.pdf" "$case/ora-$name.pdf" 1
    $control = if ($row.Count -gt 0) { 'altered image payloads reject' } else { 'no image payload to alter' }
    Write-Output "[json-image-optimization/$name] image effects, private payload and flags agree; recreation passes; $control."
  }
}
