function Invoke-ReferenceInlineContexts {
  $samples = [byte[]]::new(65536)
  for ($index = 0; $index -lt $samples.Length; $index++) { $samples[$index] = $index -band 255 }
  $program = [Text.Encoding]::ASCII.GetBytes('q BI /W 256 /H 256 /BPC 8 /CS /Local ID ') + $samples + [Text.Encoding]::ASCII.GetBytes(" EI Q`n")
  $buffer = [IO.MemoryStream]::new()
  $encoder = [IO.Compression.ZLibStream]::new($buffer, [IO.Compression.CompressionLevel]::SmallestSize, $true)
  try { $encoder.Write($program, 0, $program.Length) } finally { $encoder.Dispose() }
  $encoded = $buffer.ToArray(); $buffer.Dispose()
  $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]($samples + @(32))))
  foreach ($row in @(
    @{ Name = 'shared'; Pages = 16; Contexts = 1; Resources = $false; Arrays = $false; Distinct = $false; Protected = $false },
    @{ Name = 'resources'; Pages = 16; Contexts = 2; Resources = $true; Arrays = $false; Distinct = $false; Protected = $false },
    @{ Name = 'arrays'; Pages = 16; Contexts = 1; Resources = $false; Arrays = $true; Distinct = $false; Protected = $false },
    @{ Name = 'distinct'; Pages = 2; Contexts = 2; Resources = $false; Arrays = $false; Distinct = $true; Protected = $false },
    @{ Name = 'protected-linear-job'; Pages = 3; Contexts = 1; Resources = $false; Arrays = $false; Distinct = $false; Protected = $true }
  )) {
    $case = New-Case ('inline-context-' + $row.Name)
    $palette = [byte[]]::new(768)
    for ($index = 0; $index -lt 256; $index++) { $palette[$index * 3] = $index }
    $objects = @{
      'obj:1 0 R' = @{ value = @{ '/Type' = '/Catalog'; '/Pages' = '2 0 R'; '/AuditStream' = '3 0 R'; '/AuditResources' = '4 0 R' } }
      'obj:2 0 R' = @{ value = @{ '/Type' = '/Pages'; '/Count' = $row.Pages; '/Kids' = @(0..($row.Pages - 1) | ForEach-Object { "$($_ + 100) 0 R" }) } }
      'obj:3 0 R' = @{ stream = @{ dict = @{ '/Filter' = '/FlateDecode' }; data = [Convert]::ToBase64String($encoded) } }
      'obj:4 0 R' = @{ value = @{ '/ColorSpace' = @{ '/Local' = '/DeviceGray' } } }
      'obj:5 0 R' = @{ value = @{ '/ColorSpace' = @{ '/Local' = @('/Indexed', '/DeviceRGB', 255, ('b:' + [Convert]::ToHexString($palette))) }; '/XObject' = @{ '/IIm1' = '6 0 R' } } }
      'obj:6 0 R' = @{ stream = @{ dict = @{ '/Type' = '/XObject'; '/Subtype' = '/Image'; '/Width' = 1; '/Height' = 1; '/BitsPerComponent' = 8; '/ColorSpace' = '/DeviceGray' }; data = 'Wg==' } }
      'obj:7 0 R' = @{ value = @('3 0 R', '8 0 R') }
      'obj:8 0 R' = @{ stream = @{ dict = @{}; data = '' } }
      trailer = @{ value = @{ '/Root' = '1 0 R'; '/Size' = 100 + $row.Pages } }
    }
    for ($index = 0; $index -lt $row.Pages; $index++) {
      $contents = if ($row.Arrays) { '7 0 R' } elseif ($row.Distinct) { "$($index + 20) 0 R" } else { '3 0 R' }
      $resources = if ($row.Resources -and $index % 2 -eq 1) { '5 0 R' } else { '4 0 R' }
      if ($row.Distinct) { $objects["obj:$($index + 20) 0 R"] = @{ stream = @{ dict = @{ '/Filter' = '/FlateDecode' }; data = [Convert]::ToBase64String($encoded) } } }
      $objects["obj:$($index + 100) 0 R"] = @{ value = @{ '/Type' = '/Page'; '/Parent' = '2 0 R'; '/MediaBox' = @(0, 0, 256, 256); '/Resources' = $resources; '/Contents' = $contents } }
    }
    @{ qpdf = @(@{ jsonversion = 2; pdfversion = '1.7' }, $objects) } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$case/inputs/model.json" -Encoding utf8NoBOM
    if ((Invoke-Oracle $case @('--json-input', 'inputs/model.json', '--object-streams=disable', '--stream-data=preserve', "$case/inputs/a.pdf")) -ne 0) { throw 'Inline context source creation failed.' }
    $flags = @('inputs/a.pdf', '--externalize-inline-images', '--ii-min-bytes=0', '--decode-level=none', '--compress-streams=n', '--remove-unreferenced-resources=no')
    if ($row.Protected) {
      @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; externalizeInlineImages = $true; iiMinBytes = '0'; decodeLevel = 'none'; compressStreams = 'n'; removeUnreferencedResources = 'no'; linearize = $true; objectStreams = 'generate'; encrypt = @{ userPassword = 'reader'; ownerPassword = 'owner'; '256bit' = @{} } } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
      $managed = Invoke-SecurityManaged $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
      $flags += @('--linearize', '--object-streams=generate', '--encrypt', 'reader', 'owner', '256', '--')
    } else {
      $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--') + $flags + @('outputs/a.pdf'))
    }
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw 'Inline context managed publication failed.' }
    if ($managed.DecodedBytes -ne $program.Length * $row.Contexts) { throw 'Inline context repeated decoding was not bounded by distinct contexts.' }
    if ((Invoke-Oracle $case ($flags + @("$case/ora.pdf"))) -ne 0) { throw 'Inline context native publication failed.' }
    $password = if ($row.Protected) { 'reader' } else { '' }
    foreach ($side in @('lib', 'ora')) {
      $file = "$case/$side.pdf"
      if ((Invoke-Oracle $case @("--password=$password", '--check', $file)) -ne 0) { throw 'Inline context output failed native checking.' }
      if ($row.Protected -and ((Invoke-Oracle $case @('--password=reader', '--check-linearization', $file)) -ne 0 -or
          (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors')) { throw 'Inline context protected hints failed.' }
      if ((Invoke-Oracle $case @("--password=$password", '--json=2', '--json-key=qpdf', '--json-key=pages', '--json-stream-data=inline', '--decode-level=none', $file)) -ne 0) { throw 'Inline context output inspection failed.' }
      $report = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
      $table = $report.qpdf[1]; $catalog = $table['obj:' + $table.trailer.value['/Root']].value
      $programs = [Collections.Generic.HashSet[string]]::new(); $images = [Collections.Generic.HashSet[string]]::new()
      for ($index = 0; $index -lt $row.Pages; $index++) {
        foreach ($reference in $report.pages[$index].contents) { [void]$programs.Add($reference) }
        $image = @($report.pages[$index].images | Where-Object { $_.width -eq 256 })
        if ($image.Count -ne 1) { throw 'Inline context image binding was lost.' }
        $indexed = $row.Resources -and $index % 2 -eq 1
        if ($image[0].name -cne $(if ($indexed) { '/IIm2' } else { '/IIm1' })) { throw 'Inline context resource-name collision changed.' }
        if ($indexed) { if ($image[0].colorspace[0] -cne '/Indexed') { throw 'Indexed context became gray.' } }
        elseif ($image[0].colorspace -cne '/DeviceGray') { throw 'Gray context changed.' }
        [void]$images.Add($image[0].object)
        $payload = [Convert]::FromBase64String($table['obj:' + $image[0].object].stream.data)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)) -cne $expectedHash) { throw 'Inline context sample bytes changed.' }
      }
      $expected = if ($side -eq 'lib') { $row.Contexts } else { $row.Pages }
      if ($programs.Count -ne $expected -or $images.Count -ne $expected) { throw 'Inline context sharing/independent identity changed.' }
      if ($table['obj:' + $catalog['/AuditResources']].value.ContainsKey('/XObject') -or
          $table['obj:' + $catalog['/AuditStream']].stream.data -cne [Convert]::ToBase64String($encoded)) { throw 'Private inline source aliases changed.' }
      if ($row.Protected -and (Invoke-Oracle $case @('--password=reader', '--decrypt', $file, "$case/$side-clear.pdf")) -ne 0) { throw 'Inline context output decryption failed.' }
    }
    if ($row.Protected) { Assert-Trio $row.Name "$case/lib-clear.pdf" "$case/ora-clear.pdf" $row.Pages }
    else { Assert-Trio $row.Name "$case/lib.pdf" "$case/ora.pdf" $row.Pages }
    Write-Output "[inline-context-$($row.Name)] distinct work, identities, bindings, exact samples and private aliases validate; native duplication remains explicit"
  }
}
