function Write-ReferenceDirectOutlinePdf {
  param([string] $Path, [bool] $Populated, [bool] $Open, [bool] $Alias)
  $outline = '<< /Type /Outlines /Probe (KEEP) ' + $(if ($Populated) { '/Count 1 /First 8 0 R /Last 8 0 R' } else { '/Count 0' }) + ' >>'
  $program = "BT /F1 12 Tf 1 1 Td (Hi) Tj ET`n"
  $bodies = @(
    ('<< /Type /Catalog /Pages 2 0 R /Outlines ' + $outline + ' /Audit 7 0 R' + $(if ($Open) { ' /PageMode /UseOutlines' } else { '' }) + $(if ($Alias) { ' /OutlineAlias 5 0 R' } else { '' }) + ' >>'),
    '<< /Type /Pages /Count 1 /Kids [3 0 R] >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 6 0 R >> >> /Contents 4 0 R >>',
    "<< /Length $($program.Length) >>`nstream`n${program}endstream",
    $outline, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', '17',
    $(if ($Populated) { '<< /Title (Chapter) /Dest [3 0 R /Fit] >>' } else { 'null' })
  )
  # Independent authored framing keeps the physical input outline dictionary
  # direct; a native preparation rewrite would promote it before the test.
  $text = [Text.StringBuilder]::new("%PDF-1.7`n")
  $offsets = [Collections.Generic.List[int]]::new()
  for ($index = 0; $index -lt $bodies.Count; $index++) {
    $offsets.Add($text.Length)
    [void]$text.Append("$($index + 1) 0 obj`n$($bodies[$index])`nendobj`n")
  }
  $xref = $text.Length
  [void]$text.Append("xref`n0 $($bodies.Count + 1)`n0000000000 65535 f `n")
  foreach ($offset in $offsets) { [void]$text.Append($offset.ToString('D10', [Globalization.CultureInfo]::InvariantCulture) + " 00000 n `n") }
  [void]$text.Append("trailer`n<< /Size $($bodies.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
  [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes($text.ToString()))
}

function Invoke-ReferenceDirectOutlines {
  $rows = [Collections.Generic.List[object]]::new()
  foreach ($populated in @($false, $true)) {
    foreach ($open in @($false, $true)) {
      foreach ($packed in @($false, $true)) {
        $rows.Add(@{ Name = "pdf-$populated-$open-$packed"; Kind = 'pdf'; Populated = $populated; Open = $open; Packed = $packed; Linear = $true; Alias = $false; Protected = $false })
      }
    }
  }
  foreach ($kind in @('json-create', 'json-update', 'json-flat', 'protected-job', 'json-protected-job', 'private-alias')) {
    $rows.Add(@{ Name = $kind; Kind = $kind; Populated = $true; Open = $false; Packed = $true; Linear = $kind -ne 'json-flat'; Alias = $kind -eq 'private-alias'; Protected = $kind -in @('protected-job', 'json-protected-job') })
  }
  foreach ($row in $rows) {
    $case = New-Case ('direct-outline-' + $row.Name)
    Write-ReferenceDirectOutlinePdf "$case/inputs/a.pdf" $row.Populated $row.Open $row.Alias
    if ((Invoke-Oracle $case @('--check', "$case/inputs/a.pdf")) -ne 0) { throw 'Direct outline source is not clean.' }
    $flags = @('inputs/a.pdf')
    if ($row.Kind -like 'json-*') {
      if ((Invoke-Oracle $case @('--json-output=2', '--json-stream-data=inline', 'inputs/a.pdf')) -ne 0) { throw 'Direct outline JSON source export failed.' }
      $model = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
      $table = $model.qpdf[1]; $catalog = $table['obj:' + $table.trailer.value['/Root']].value
      $outline = $catalog['/Outlines']
      if ($outline -is [string]) { $outline = $table['obj:' + $outline].value }
      $catalog['/Outlines'] = $outline; $catalog['/Private'] = $outline
      $model | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$case/inputs/direct.json" -Encoding utf8NoBOM
      $flags = if ($row.Kind -eq 'json-update') { @('inputs/a.pdf', '--update-from-json=inputs/direct.json') } else { @('inputs/direct.json', '--json-input') }
    }
    if ($row.Linear) { $flags += '--linearize' }
    $flags += '--object-streams=' + $(if ($row.Packed) { 'generate' } else { 'disable' })
    if ($row.Protected) {
      $job = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; linearize = $true; objectStreams = 'generate'; encrypt = @{ userPassword = 'reader'; ownerPassword = 'owner'; '256bit' = @{} } }
      if ($row.Kind -eq 'json-protected-job') { $job.inputFile = 'inputs/direct.json'; $job.jsonInput = $true }
      $job | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
      $managed = Invoke-SecurityManaged $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
      $flags += @('--encrypt', 'reader', 'owner', '256', '--')
    } else {
      $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--') + $flags + @('outputs/a.pdf'))
    }
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw 'Managed direct outline publication failed.' }
    if ((Invoke-Oracle $case ($flags + @("$case/ora.pdf"))) -ne 0) { throw 'Native direct outline publication failed.' }
    $password = if ($row.Protected) { 'reader' } else { '' }
    foreach ($side in @('lib', 'ora')) {
      $file = "$case/$side.pdf"
      if ((Invoke-Oracle $case @("--password=$password", '--check', $file)) -ne 0) { throw 'Direct outline output check failed.' }
      if ($row.Linear -and ((Invoke-Oracle $case @("--password=$password", '--check-linearization', $file)) -ne 0 -or
          (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors')) { throw 'Direct outline output has invalid hints.' }
      if ((Invoke-Oracle $case @("--password=$password", '--json=2', '--json-key=qpdf', '--json-key=outlines', $file)) -ne 0) { throw 'Direct outline output inspection failed.' }
      $report = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
      $table = $report.qpdf[1]; $catalog = $table['obj:' + $table.trailer.value['/Root']].value
      $outline = $catalog['/Outlines']
      if ($outline -is [string]) { $outline = $table['obj:' + $outline].value }
      elseif ($row.Linear) { throw 'Linearized outline root must be indirect.' }
      # A native flat JSON writer may retain the direct dictionary. Its values,
      # actual packing and reader checks remain independent of that representation.
      if ($outline -isnot [Collections.IDictionary] -or $outline['/Probe'] -cne 'u:KEEP') { throw 'Direct outline root was lost.' }
      if ($report.outlines.Count -ne $(if ($row.Populated) { 1 } else { 0 })) { throw 'Direct outline item count changed.' }
      if ($row.Populated -and ($report.outlines[0].title -cne 'Chapter' -or $report.outlines[0].destpageposfrom1 -ne 1)) { throw 'Direct outline navigation changed.' }
      if ($row.Kind -like 'json-*' -and $catalog['/Private']['/Probe'] -cne 'u:KEEP') { throw 'Private direct dictionary changed.' }
      if ($row.Alias -and ($catalog['/OutlineAlias'] -ceq $catalog['/Outlines'] -or $table['obj:' + $catalog['/OutlineAlias']].value['/Probe'] -cne 'u:KEEP')) { throw 'Private indirect alias changed identity.' }
      if ($row.Protected) {
        if ((Invoke-Oracle $case @('--password=reader', '--decrypt', $file, "$case/$side-clear.pdf")) -ne 0) { throw 'Protected direct outline decryption failed.' }
      }
      if ($row.Packed) { Assert-ReferenceObjectStreams $(if ($row.Protected) { "$case/$side-clear.pdf" } else { $file }) }
    }
    if ($row.Protected) { Assert-Trio $row.Name "$case/lib-clear.pdf" "$case/ora-clear.pdf" 1 }
    else { Assert-Trio $row.Name "$case/lib.pdf" "$case/ora.pdf" 1 }
    Write-Output "[direct-outline-$($row.Name)] direct source, outline values, navigation, layout and independent reader agree"
  }
}
