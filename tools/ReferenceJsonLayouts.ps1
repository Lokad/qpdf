function Get-JsonLayoutFacts {
  param([string] $Case, [string] $File, [string] $Password)
  $code = Invoke-Oracle $Case @("--password=$Password", '--json=2', '--json-key=qpdf', '--json-stream-data=inline', '--decode-level=generalized', $File)
  if ($code -ne 0) { throw 'JSON layout semantic inspection failed.' }
  $report = Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
  $table = $report.qpdf[1]; $trailer = $table.trailer.value
  $catalog = $table['obj:' + $trailer['/Root']].value
  $pages = $table['obj:' + $catalog['/Pages']].value
  $page = $table['obj:' + $pages['/Kids'][0]].value
  return [ordered]@{
    Marker = $catalog['/Marker']; Probe = $table['obj:' + $catalog['/Probe']].value
    Scalar = $table['obj:' + $catalog['/Scalar']].value
    Title = $table['obj:' + $trailer['/Info']].value['/Title']
    Contents = $table['obj:' + $page['/Contents']].stream.data
  }
}

function Invoke-ReferenceJsonLayouts {
  $source = New-Case 'json-layout-source'
  $model = '{"qpdf":[{"jsonversion":2,"pdfversion":"1.3"},{"obj:5 0 R":{"value":{"/Type":"/Catalog","/Pages":"7 0 R","/Probe":"1 0 R","/Scalar":"6 0 R","/Marker":"u:patched"}},"obj:1 0 R":{"value":"u:private-value"},"obj:2 0 R":{"value":{"/Title":"u:kept-info"}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"7 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"4 0 R"}},"obj:4 0 R":{"stream":{"dict":{},"data":"cQpRCg=="}},"obj:6 0 R":{"value":17},"obj:7 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"trailer":{"value":{"/Root":"5 0 R","/Info":"2 0 R","/Size":8}}}]}'
  [IO.File]::WriteAllText("$source/inputs/create.json", $model, [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText("$source/inputs/source.json", $model.Replace('u:patched', 'u:old'), [Text.UTF8Encoding]::new($false))
  foreach ($encrypted in @($false, $true)) {
    $name = if ($encrypted) { 'encrypted' } else { 'packed' }
    $mode = if ($encrypted) { 'disable' } else { 'generate' }
    $security = if ($encrypted) { @('--encrypt', 'reader', 'owner', '256', '--') } else { @() }
    if ((Invoke-Oracle $source (@('--json-input', 'inputs/source.json', "--object-streams=$mode") + $security + @("$source/$name.pdf"))) -ne 0) { throw 'JSON layout source preparation failed.' }
    $password = if ($encrypted) { 'reader' } else { '' }
    if ((Invoke-Oracle $source @("--password=$password", '--json=2', '--json-key=qpdf', '--json-stream-data=inline', '--decode-level=none', "$source/$name.pdf")) -ne 0) { throw 'JSON layout source export failed.' }
    $patch = Get-Content -LiteralPath "$source/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
    $root = $patch.qpdf[1].trailer.value['/Root']
    $patch.qpdf[1]['obj:' + $root].value['/Marker'] = 'u:patched'
    $patch | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath "$source/$name.json" -Encoding utf8NoBOM
  }
  $cases = @(
    @{ Name = 'create-generate'; Update = $false; Flags = @('--object-streams=generate'); Packed = $true; Version = '1.5' },
    @{ Name = 'create-linear'; Update = $false; Flags = @('--linearize'); Capture = $true; Packed = $false; Version = '1.3' },
    @{ Name = 'create-combined'; Update = $false; Flags = @('--object-streams=generate', '--linearize'); Packed = $true; Version = '1.5' },
    @{ Name = 'update-generate'; Update = $true; Flags = @('--object-streams=generate'); Packed = $true; Version = '1.5' },
    @{ Name = 'update-linear'; Update = $true; Flags = @('--linearize', '--object-streams=disable'); Capture = $true; Packed = $false; Version = '1.5' },
    @{ Name = 'update-preserve'; Update = $true; Flags = @('--object-streams=preserve'); Packed = $true; Version = '1.5' },
    @{ Name = 'update-combined'; Update = $true; Flags = @('--linearize', '--object-streams=generate'); Packed = $true; Version = '1.5' },
    @{ Name = 'update-default'; Update = $true; Flags = @(); Packed = $true; Version = '1.5' },
    @{ Name = 'create-force13'; Update = $false; Flags = @('--force-version=1.3', '--object-streams=generate'); Packed = $false; Version = '1.3' },
    @{ Name = 'create-min13'; Update = $false; Flags = @('--min-version=1.3', '--object-streams=generate'); Packed = $true; Version = '1.5' },
    @{ Name = 'update-force14'; Update = $true; Flags = @('--force-version=1.4', '--object-streams=generate'); Packed = $false; Version = '1.4' },
    @{ Name = 'create-protected'; Update = $false; Flags = @('--object-streams=generate', '--linearize', '--encrypt', 'reader', 'owner', '256', '--'); Packed = $true; Version = '1.7'; Protected = $true },
    @{ Name = 'update-protected'; Update = $true; Flags = @('--password=reader', '--object-streams=generate', '--linearize'); Packed = $true; Version = '1.7'; Protected = $true }
  )
  foreach ($row in $cases) {
    $name = 'json-layout-' + $row.Name; $case = New-Case $name
    $sourceName = if ($row.Protected) { 'encrypted' } else { 'packed' }
    if ($row.Update) {
      Copy-Item -LiteralPath "$source/$sourceName.pdf" -Destination "$case/inputs/a.pdf"
      Copy-Item -LiteralPath "$source/$sourceName.json" -Destination "$case/inputs/model.json"
      $operation = @('inputs/a.pdf', '--update-from-json=inputs/model.json')
    } else {
      Copy-Item -LiteralPath "$source/inputs/create.json" -Destination "$case/inputs/model.json"
      $operation = @('inputs/model.json', '--json-input')
    }
    $before = @{}; Get-ChildItem -LiteralPath "$case/inputs" -File | ForEach-Object { $before[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $mappings = @('--out', "outputs/a.pdf=$case/lib.pdf")
    $managedFlags = $row.Flags; $nativeFlags = $row.Flags
    if ($row.Capture) {
      $mappings += @('--out', "outputs/pass1.bin=$case/lib.pass1")
      $managedFlags += '--linearize-pass1=outputs/pass1.bin'
      $nativeFlags += "--linearize-pass1=$case/ora.pass1"
    }
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case) + $mappings + @('--') + $operation + $managedFlags + @('outputs/a.pdf'))
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw "$name managed import failed." }
    if ((Invoke-Oracle $case ($operation + $nativeFlags + @("$case/ora.pdf"))) -ne 0) { throw "$name native import failed." }
    $password = if ($row.Protected) { 'reader' } else { '' }
    foreach ($side in @('lib', 'ora')) {
      $file = "$case/$side.pdf"
      if ((Invoke-Oracle $case @("--password=$password", '--check', $file)) -ne 0) { throw "$name/$side native structure check failed." }
      if (([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($file)[0..7])) -cne ('%PDF-' + $row.Version)) { throw "$name/$side header version differs." }
      if ((Invoke-Oracle $case @("--password=$password", '--show-xref', $file)) -ne 0) { throw "$name/$side xref inspection failed." }
      $packed = (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -match ': compressed;'
      if ($packed -ne $row.Packed) { throw "$name/$side compressed membership differs." }
      if ($row.Flags -contains '--linearize') {
        $code = Invoke-Oracle $case @("--password=$password", '--check-linearization', $file)
        if ($code -ne 0 -or (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors') { throw "$name/$side linearization did not validate." }
      }
      if ($row.Capture) {
        $first = [IO.File]::ReadAllText("$case/$side.pass1", [Text.Encoding]::Latin1)
        if ($first -notmatch '(?m)^0000000000 00000 n ' -or
            (Get-FileHash -LiteralPath "$case/$side.pass1").Hash -eq (Get-FileHash -LiteralPath $file).Hash) {
          throw "$name/$side did not capture the first-pass offsets."
        }
        Write-Output "[$name/$side] captured first-pass placeholder offsets."
      }
      $facts = Get-JsonLayoutFacts $case $file $password
      if ($facts.Marker -cne 'u:patched' -or $facts.Probe -cne 'u:private-value' -or $facts.Scalar -ne 17 -or $facts.Title -cne 'u:kept-info' -or $facts.Contents -cne 'cQpRCg==') { throw "$name/$side imported values differ." }
      $facts | ConvertTo-Json | Set-Content -LiteralPath "$case/$side.facts.json" -Encoding utf8NoBOM
    }
    if ($row.Protected) {
      foreach ($side in @('lib', 'ora')) {
        if ((Invoke-Oracle $case @('--password=reader', '--decrypt', "$case/$side.pdf", "$case/$side-clear.pdf")) -ne 0) { throw "$name/$side decryption failed." }
      }
      Assert-Trio $name "$case/lib-clear.pdf" "$case/ora-clear.pdf" 1
    } else { Assert-Trio $name "$case/lib.pdf" "$case/ora.pdf" 1 }
    foreach ($entry in $before.GetEnumerator()) { if ((Get-FileHash -LiteralPath "$case/inputs/$($entry.Key)" -Algorithm SHA256).Hash -cne $entry.Value) { throw "$name changed an input." } }
    Write-Output "[$name] imported values, actual packing, version, protection and physical checks pass"
  }
  foreach ($linear in @($false, $true)) {
    $name = if ($linear) { 'json-layout-source-pairs-linear' } else { 'json-layout-source-pairs' }
    $case = New-Case $name
    Copy-Item -LiteralPath (Join-Path $fixtureDir 'json-layout-sparse.pdf') -Destination "$case/inputs/a.pdf"
    $patch = '{"qpdf":[{"jsonversion":2},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Probe":"6 0 R","/Decoy":"7 0 R","/Marker":"u:patched"}}}]}'
    [IO.File]::WriteAllText("$case/inputs/patch.json", $patch, [Text.UTF8Encoding]::new($false))
    $before = (Get-FileHash -LiteralPath "$case/inputs/a.pdf" -Algorithm SHA256).Hash
    $flags = @('inputs/a.pdf', '--suppress-recovery', '--update-from-json=inputs/patch.json', '--object-streams=preserve')
    if ($linear) { $flags += '--linearize' }
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--') + $flags + @('outputs/a.pdf'))
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw "$name managed update failed." }
    if ((Invoke-Oracle $case ($flags + @("$case/ora.pdf"))) -ne 0) { throw "$name native update failed." }
    foreach ($side in @('lib', 'ora')) {
      $file = "$case/$side.pdf"
      if ((Invoke-Oracle $case @('--show-xref', $file)) -ne 0) { throw "$name/$side xref inspection failed." }
      $xref = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw
      if ((Invoke-Oracle $case @('--json=2', '--json-key=qpdf', $file)) -ne 0) { throw "$name/$side object inspection failed." }
      $objects = (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable).qpdf[1]
      $catalog = $objects['obj:' + $objects.trailer.value['/Root']].value
      if ($catalog['/Marker'] -cne 'u:patched') { throw "$name/$side catalog patch was lost." }
      foreach ($role in @('Probe', 'Decoy')) {
        $reference = $catalog['/' + $role]; $number = $reference.Split(' ')[0]
        $packed = $xref -match ('(?m)^' + $number + '/0: compressed;')
        $value = if ($role -eq 'Probe') { 'u:kept' } else { 'u:decoy' }
        if ($packed -ne ($role -eq 'Probe') -or $objects['obj:' + $reference].value['/Private'] -cne $value) { throw "$name/$side preserved the wrong member identity." }
      }
      if ($linear) {
        if ((Invoke-Oracle $case @('--check-linearization', $file)) -ne 0 -or
            (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors') { throw "$name/$side physical linearization failed." }
      }
    }
    Assert-Trio $name "$case/lib.pdf" "$case/ora.pdf" 1
    if ((Get-FileHash -LiteralPath "$case/inputs/a.pdf" -Algorithm SHA256).Hash -cne $before) { throw "$name changed its source." }
    Write-Output "[$name] exact original member and ordinary decoy retain their roles across sparse numbering"
  }
}
