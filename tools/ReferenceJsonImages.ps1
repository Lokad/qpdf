# Development-only image effects. JPEG bytes are encoder-specific.
function Get-JsonImageProjection {
  param($Json)
  $table = $Json.qpdf[1]
  $catalog = $table['obj:' + $table.trailer.value['/Root']].value
  $pages = $table['obj:' + $catalog['/Pages']].value
  $page = $table['obj:' + $pages['/Kids'][0]].value
  $resources = $page['/Resources']
  $owner = $page
  for ($depth=0; $null -eq $resources -and $depth -lt 8 -and $owner.ContainsKey('/Parent'); $depth++) {
    $owner = $table['obj:' + $owner['/Parent']].value
    $resources = $owner['/Resources']
  }
  if ($null -eq $resources) { throw 'Expected an effective image resource dictionary.' }
  if ($resources -is [string]) { $resources = $table['obj:' + $resources].value }
  $objects = $resources['/XObject']
  if ($objects -is [string]) { $objects = $table['obj:' + $objects].value }
  $images = [Collections.Generic.List[object]]::new()
  if ($null -ne $objects) {
    foreach ($entry in $objects.GetEnumerator()) {
      $reference = $entry.Value
      $stream = $table['obj:' + $reference].stream
      $bytes = [Convert]::FromBase64String($stream.data)
      $images.Add([pscustomobject]@{ Name=$entry.Key; Pair=$reference; Filter=$stream.dict['/Filter']; Bytes=$bytes
        Width=$stream.dict['/Width']; Height=$stream.dict['/Height'] })
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
  Assert-BoundJsonImages $case $pixels $pixelHash $optimize
}

function Read-PaintedImages([string]$Case, [string]$Pdf) {
  $result = Invoke-ReferenceProbeRequest -WorkingDirectory $Case -Arguments @('page-images', '--input', $Pdf)
  if ($result.ExitCode -ne 0) { throw "Independent image inspection failed: $Pdf. See $($result.StandardError)." }
  [IO.File]::ReadAllText($result.StandardOutput) | ConvertFrom-Json
}

function Read-ReferenceImageBytes([string]$Case, [string]$Pdf, [string]$Pair, [bool]$Decode) {
  $id = $Pair -split ' '
  $mode = if ($Decode) { '--filtered-stream-data' } else { '--raw-stream-data' }
  $job = Start-ReferenceProcess -FilePath $QpdfExe -WorkingDirectory $Case -NoNewWindow -Wait -PassThru `
    -ArgumentList @($Pdf, "--show-object=$($id[0]),$($id[1])", '--decode-level=all', $mode) `
    -RedirectStandardOutput "$Case/image-samples.bin" -RedirectStandardError "$Case/image-samples.txt"
  if ($job.ExitCode -ne 0) { throw "Native image stream inspection failed: $Pdf $Pair." }
  return ,([IO.File]::ReadAllBytes("$Case/image-samples.bin"))
}

function Assert-BoundImagePainting([string]$Case, [string]$Pdf, $Image, [bool]$Encoded, [byte[]]$Pixels) {
  $drawn = @(Read-PaintedImages $Case $Pdf)
  $raw = Read-ReferenceImageBytes $Case $Pdf $Image.Pair $false
  $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
  if ($drawn.Count -ne 1 -or $drawn[0].Page -ne 1 -or $drawn[0].Inline -or
      $drawn[0].Width -ne $Image.Width -or $drawn[0].Height -ne $Image.Height -or $drawn[0].EncodedSha256 -cne $hash) {
    throw "The expected image resource was not painted on page 1: $Pdf $($Image.Name)."
  }
  if (!$Encoded) { return }
  $decoded = Read-ReferenceImageBytes $Case $Pdf $Image.Pair $true
  if ($decoded.Length -ne $Pixels.Length) { throw "Decoded image sample count differs: $Pdf." }
  for ($i = 0; $i -lt $decoded.Length; $i++) {
    if ([Math]::Abs([int]$decoded[$i] - [int]$Pixels[$i]) -gt 4) {
      throw "Decoded JPEG sample $i differs from the original solid color: $Pdf."
    }
  }
}

function Assert-CommentedImageIsNotPainted([string]$Case, $View, [string]$Pair, [string]$Operator) {
  $stream = $View.qpdf[1]['obj:' + $Pair].stream
  $program = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($stream.data))
  $mutated = $program.Replace($Operator, "% $Operator`n", [StringComparison]::Ordinal)
  if ($mutated -ceq $program) { throw 'Comment control did not replace an image draw.' }
  $stream.data = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($mutated))
  $View | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$Case/commented-image.json"
  $pdf = "$Case/commented-image.pdf"
  if ((Invoke-Oracle $Case @('--json-input', "$Case/commented-image.json", '--stream-data=preserve', $pdf)) -ne 0 -or
      (Invoke-Oracle $Case @('--check', $pdf)) -ne 0) {
    throw 'Cannot create a valid commented-image control.'
  }
  if (@(Read-PaintedImages $Case $pdf).Count -ne 0) { throw 'A commented image draw was reported as painted.' }
  Write-Output '[json-bound-images/commented-image] valid PDF keeps the image resource and paints no image.'
}

function Assert-BoundJsonImages {
  param([string]$Case, [byte[]]$Pixels, [string]$PixelHash, [string[]]$Optimize)
  foreach ($shape in 'inline','xobject') {
    $model = Get-Content -LiteralPath "$Case/$shape.json" -Raw | ConvertFrom-Json -AsHashtable
    $table = $model.qpdf[1]
    $leaf = $table['obj:3 0 R'].value
    $table['obj:2 0 R'].value['/Resources'] = $leaf['/Resources']
    $null = $leaf.Remove('/Resources')
    $model | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$Case/inputs/bound-$shape.json"
  }
  if ((Invoke-Oracle $Case @('inputs/bound-inline.json','--json-input','--stream-data=preserve','inputs/bound-source.pdf')) -ne 0 -or
      (Invoke-Oracle $Case @('inputs/bound-source.pdf','--json=2','--json-key=pages',"$Case/bound-pages.json")) -ne 0) {
    throw 'Cannot prepare the bound image patch source.'
  }
  $source = Get-Content -LiteralPath "$Case/bound-pages.json" -Raw | ConvertFrom-Json
  $key = 'obj:' + $source.pages[0].contents[0]
  $program = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String(
    (Get-Content -LiteralPath "$Case/inputs/bound-inline.json" -Raw | ConvertFrom-Json -AsHashtable).qpdf[1]['obj:4 0 R'].stream.data))
  $patch = "% patched inline image`n" + $program
  $updated = @{ stream=@{ dict=@{}; data=[Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($patch)) } }
  @{ qpdf=@(@{ jsonversion=2 },@{ $key=$updated }) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$Case/inputs/image-patch.json"
  $inputs = @(Get-ChildItem -LiteralPath "$Case/inputs" -File | Get-FileHash)
  foreach ($row in @(
    @{ Name='externalize'; Args=@('inputs/bound-inline.json','--json-input','--externalize-inline-images','--ii-min-bytes=0','--stream-data=uncompress'); Encoded=$false },
    @{ Name='inline-optimize'; Args=@('inputs/bound-inline.json','--json-input','--ii-min-bytes=0')+$Optimize; Encoded=$true },
    @{ Name='xobject-optimize'; Args=@('inputs/bound-xobject.json','--json-input','--keep-inline-images')+$Optimize; Encoded=$true },
    @{ Name='patched-optimize'; Args=@('inputs/bound-source.pdf','--update-from-json=inputs/image-patch.json','--ii-min-bytes=0')+$Optimize; Encoded=$true }
  )) {
    $tag = "json-bound-images/$($row.Name)"
    $lib = "$Case/lib-bound-$($row.Name).pdf"; $ora = "$Case/ora-bound-$($row.Name).pdf"
    if ((Invoke-Emit $Case (@('emit','--workdir',$Case,'--out',"output.pdf=$lib",'--')+$row.Args+@('output.pdf'))) -ne 0 -or
        (Invoke-Oracle $Case ($row.Args+@($ora))) -ne 0) { throw "$tag failed to publish." }
    Assert-Trio $tag $lib $ora 1
    foreach ($pdf in @($lib,$ora)) {
      $report = "$pdf.reference.json"
      if ((Invoke-Oracle $Case @($pdf,'--json=2','--json-key=qpdf','--json-key=pages',
          '--json-stream-data=inline','--decode-level=generalized',$report)) -ne 0) { throw "$tag graph is unavailable." }
      $view = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
      $projection = Get-JsonImageProjection $view
      if (-not (Test-JsonImageProjection $projection 1 $row.Encoded $true $PixelHash)) {
        throw "$tag/$pdf image filters, samples or private payload differ."
      }
      $pair = $view.pages[0].contents[0]
      $name = if ($row.Name -eq 'xobject-optimize') { '/Im' } else { '/IIm1' }
      if ($projection.Images[0].Name -cne $name) { throw "$tag changed the expected resource name." }
      Assert-BoundImagePainting $Case $pdf $projection.Images[0] $row.Encoded $Pixels
      if ($row.Name -eq 'patched-optimize') {
        $body = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($view.qpdf[1]['obj:'+$pair].stream.data))
        if (!$body.Contains('% patched inline image', [StringComparison]::Ordinal)) { throw "$tag discarded the patch marker." }
      }
      if ($row.Name -eq 'inline-optimize' -and $pdf -eq $lib) {
        Assert-CommentedImageIsNotPainted $Case $view $pair "$name Do"
      }
    }
    Write-Output "[$tag] painted resource, image samples and independent private payload agree."
  }
  Assert-BoundImageReports $Case $PixelHash $Optimize
  if (Compare-Object $inputs @(Get-ChildItem -LiteralPath "$Case/inputs" -File | Get-FileHash) -Property Path,Hash) {
    throw 'Bound image processing changed an input.'
  }
}

function Assert-BoundImageReports([string]$Case, [string]$PixelHash, [string[]]$Optimize) {
  $reportArgs = @('inputs/bound-inline.json', '--json-input', '--externalize-inline-images', '--ii-min-bytes=0',
    '--json-output=2', '--json-key=pages', '--decode-level=generalized')
  if ((Invoke-Emit $Case (@('emit','--workdir',$Case,'--out',"report=$Case/lib-bound-report.json",'--')+$reportArgs+@('report'))) -ne 0 -or
      (Invoke-Oracle $Case ($reportArgs+@("$Case/ora-bound-report.json"))) -ne 0) {
    throw 'Bound image JSON report failed.'
  }
  foreach ($engine in 'lib','ora') {
    $view = Get-Content -LiteralPath "$Case/${engine}-bound-report.json" -Raw | ConvertFrom-Json -AsHashtable
    $projection = Get-JsonImageProjection $view
    if (-not (Test-JsonImageProjection $projection 1 $false $true $PixelHash) -or
        $view.pages[0].images[0].name -cne '/IIm1' -or $view.pages[0].images[0].object -cne $projection.Images[0].Pair) {
      throw "Bound JSON report has a different active image: $engine."
    }
  }
  $listingArgs = @('inputs/bound-source.pdf','--update-from-json=inputs/image-patch.json','--show-pages','--with-images')+$Optimize
  $request = Invoke-ReferenceProbeRequest -WorkingDirectory $Case -Arguments (@('emit','--workdir',$Case,'--')+$listingArgs)
  if ($request.ExitCode -ne 0 -or (Invoke-Oracle $Case $listingArgs) -ne 0) {
    throw 'Patched image listing failed.'
  }
  foreach ($file in $request.StandardOutput, "$Case/ora-stdout.txt") {
    if ([IO.File]::ReadAllText($file) -cnotmatch '(?m)^(?:emit: diagnostic PageListing Info )? +/IIm1: [0-9]+ [0-9]+ R, 128 x 128\r?$') {
      throw "Patched image listing omitted the optimized image: $file."
    }
  }
  Write-Output '[json-bound-images/reports] authored JSON and patched text report the active image.'
}
