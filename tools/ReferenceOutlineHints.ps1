function Invoke-ReferenceOutlineHints {
  foreach ($row in @(
    @{ Name = 'empty-closed'; Count = 0; Open = $false; Shared = $false },
    @{ Name = 'empty-open'; Count = 0; Open = $true; Shared = $false },
    @{ Name = 'item-closed'; Count = 1; Open = $false; Shared = $false },
    @{ Name = 'item-open'; Count = 1; Open = $true; Shared = $false },
    @{ Name = 'item-shared'; Count = 1; Open = $false; Shared = $true },
    @{ Name = 'large-shared'; Count = 110; Open = $false; Shared = $true },
    @{ Name = 'protected-job'; Count = 1; Open = $true; Shared = $true; Protected = $true }
  )) {
    $case = New-Case ('outline-hint-' + $row.Name)
    $catalog = @{ '/Type' = '/Catalog'; '/Pages' = '2 0 R'; '/Outlines' = '5 0 R'; '/Audit' = '7 0 R' }
    if ($row.Open) { $catalog['/PageMode'] = '/UseOutlines' }
    $outlines = @{ '/Type' = '/Outlines'; '/Probe' = 'u:KEEP'; '/Count' = $row.Count }
    if ($row.Count -gt 0) { $outlines['/First'] = '8 0 R'; $outlines['/Last'] = "$($row.Count + 7) 0 R" }
    $resources = if ($row.Shared) { @{ '/Font' = @{ '/F1' = '6 0 R' } } } else { @{} }
    $program = if ($row.Shared) { "BT /F1 12 Tf 1 1 Td (Hi) Tj ET`n" } else { "q Q`n" }
    $objects = @{
      'obj:1 0 R' = @{ value = $catalog }
      'obj:2 0 R' = @{ value = @{ '/Type' = '/Pages'; '/Count' = 1; '/Kids' = @('3 0 R') } }
      'obj:3 0 R' = @{ value = @{ '/Type' = '/Page'; '/Parent' = '2 0 R'; '/MediaBox' = @(0, 0, 100, 100); '/Contents' = '4 0 R'; '/Resources' = $resources } }
      'obj:4 0 R' = @{ stream = @{ dict = @{}; data = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($program)) } }
      'obj:5 0 R' = @{ value = $outlines }
      'obj:6 0 R' = @{ value = @{ '/Type' = '/Font'; '/Subtype' = '/Type1'; '/BaseFont' = '/Helvetica' } }
      'obj:7 0 R' = @{ value = 17 }
      trailer = @{ value = @{ '/Root' = '1 0 R'; '/Size' = 8 + $row.Count } }
    }
    for ($index = 0; $index -lt $row.Count; $index++) {
      $item = @{ '/Title' = 'u:Ch' + ($index + 1); '/Parent' = '5 0 R'; '/Dest' = @('3 0 R', '/Fit') }
      if ($index -gt 0) { $item['/Prev'] = "$($index + 7) 0 R" }
      if ($index + 1 -lt $row.Count) { $item['/Next'] = "$($index + 9) 0 R" }
      $objects["obj:$($index + 8) 0 R"] = @{ value = $item }
    }
    @{ qpdf = @(@{ jsonversion = 2; pdfversion = '1.7' }, $objects) } | ConvertTo-Json -Depth 20 |
      Set-Content -LiteralPath "$case/inputs/model.json" -Encoding utf8NoBOM
    if ((Invoke-Oracle $case @('--json-input', 'inputs/model.json', '--object-streams=disable', "$case/inputs/a.pdf")) -ne 0) { throw 'Outline hint source preparation failed.' }
    $flags = @('inputs/a.pdf', '--linearize', '--object-streams=generate')
    if ($row.Protected) {
      $job = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; linearize = $true; objectStreams = 'generate'; encrypt = @{ userPassword = 'reader'; ownerPassword = 'owner'; '256bit' = @{} } }
      $job | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
      $managed = Invoke-SecurityManaged $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
      $flags += @('--encrypt', 'reader', 'owner', '256', '--')
    } else {
      $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--') + $flags + @('outputs/a.pdf'))
    }
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw 'Managed outline hint publication failed.' }
    if ((Invoke-Oracle $case ($flags + @("$case/ora.pdf"))) -ne 0) { throw 'Native outline hint publication failed.' }
    if ($row.Name -eq 'large-shared') {
      # Pinned native packing can split this outline root from the advertised
      # physical group. Retain its exact warning and compare a valid classic layout.
      $code = Invoke-Oracle $case @('--check-linearization', "$case/ora.pdf")
      if ($code -ne 3 -or (Get-Content -LiteralPath "$case/ora-stderr.txt" -Raw) -notmatch 'incorrect (offset|length) in outlines table') { throw 'Native mixed-container warning control changed.' }
      Copy-Item -LiteralPath "$case/ora.pdf" -Destination "$case/ora-packed-warning.pdf"
      Copy-Item -LiteralPath "$case/ora-stderr.txt" -Destination "$case/ora-packed-warning.txt"
      if ((Invoke-Oracle $case ($flags + @('--object-streams=disable', "$case/ora.pdf"))) -ne 0) { throw 'Native classic outline equivalent failed.' }
    }
    $password = if ($row.Protected) { 'reader' } else { '' }
    foreach ($side in @('lib', 'ora')) {
      $file = "$case/$side.pdf"
      if ((Invoke-Oracle $case @("--password=$password", '--check', $file)) -ne 0) { throw 'Outline output structure check failed.' }
      if ((Invoke-Oracle $case @("--password=$password", '--check-linearization', $file)) -ne 0 -or
          (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors') { throw 'Outline physical hints did not validate.' }
      if ((Invoke-Oracle $case @("--password=$password", '--json=2', '--json-key=qpdf', '--json-key=outlines', $file)) -ne 0) { throw 'Outline inspection failed.' }
      $report = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
      $table = $report.qpdf[1]; $catalogValue = $table['obj:' + $table.trailer.value['/Root']].value
      if ($table['obj:' + $catalogValue['/Outlines']].value['/Probe'] -cne 'u:KEEP' -or $report.outlines.Count -ne $row.Count) { throw 'Outline marker or item count changed.' }
      for ($index = 0; $index -lt $row.Count; $index++) { if ($report.outlines[$index].title -cne ('Ch' + ($index + 1))) { throw 'Outline title/order changed.' } }
      if ($row.Protected) {
        if ((Invoke-Oracle $case @('--password=reader', '--decrypt', $file, "$case/$side-clear.pdf")) -ne 0) { throw 'Protected outline decryption failed.' }
      }
      if ($row.Name -ne 'large-shared' -or $side -ne 'ora') {
        Assert-ReferenceObjectStreams $(if ($row.Protected) { "$case/$side-clear.pdf" } else { $file })
      }
    }
    if ($row.Protected) { Assert-Trio $row.Name "$case/lib-clear.pdf" "$case/ora-clear.pdf" 1 }
    else { Assert-Trio $row.Name "$case/lib.pdf" "$case/ora.pdf" 1 }
    Write-Output "[outline-hint-$($row.Name)] outline values, packing and native physical hints validate"
  }
  foreach ($row in @(
    @{ Name = 'linearized-outline-missing-hint.pdf'; Native = 3; Managed = 2 },
    @{ Name = 'linearized-outline-split-group.pdf'; Native = 3; Managed = 2 },
    @{ Name = 'linearized-outline-native-omission.pdf'; Native = 0; Managed = 0 }
  )) {
    $case = New-Case ('outline-hint-control-' + $row.Name)
    Copy-Item -LiteralPath (Join-Path $fixtureDir $row.Name) -Destination "$case/inputs/a.pdf"
    if ((Invoke-Oracle $case @('--check-linearization', "$case/inputs/a.pdf")) -ne $row.Native) { throw 'Independent outline hint control changed.' }
    $managed = Invoke-SecurityManaged $case @('emit', '--workdir', $case, '--', 'inputs/a.pdf', '--check')
    if ($managed.ExitCode -ne $row.Managed) { throw 'Managed outline hint control changed.' }
  }
}
