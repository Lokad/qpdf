# Explicit signature removal uses the same publication policy as ordinary copying.
function Read-SecuritySignatureMarks {
  param([string] $File)
  $json = Get-Content -LiteralPath ($File + '.reference.json') -Raw | ConvertFrom-Json -AsHashtable
  $objects = $json.qpdf[1]
  $marks = foreach ($page in $json.pages) {
    $dictionary = $objects['obj:' + $page.object].value
    $annotations = $dictionary['/Annots']
    if ($annotations -is [string]) { $annotations = $objects['obj:' + $annotations].value }
    foreach ($reference in $annotations) {
      $widget = $objects['obj:' + $reference].value
      $appearance = $widget['/AP']
      if ($appearance -is [string]) { $appearance = $objects['obj:' + $appearance].value }
      $normal = $objects['obj:' + $appearance['/N']].stream
      $resources = $normal.dict['/Resources']
      if ($resources -is [string]) { $resources = $objects['obj:' + $resources].value }
      $xobjects = $resources['/XObject']
      if ($xobjects -is [string]) { $xobjects = $objects['obj:' + $xobjects].value }
      $mark = $objects['obj:' + $xobjects['/Mark']].stream
      if ([Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($normal.data)) -cne 'q /Mark Do Q') {
        throw 'Missing stored signature appearance'
      }
      [ordered]@{ rect = $widget['/Rect']; bbox = $normal.dict['/BBox']; matrix = $normal.dict['/Matrix'];
        normal = $normal.data; mark = $mark.data; markBox = $mark.dict['/BBox'] }
    }
  }
  return ConvertTo-Json -InputObject @($marks) -Depth 8 -Compress
}
function Invoke-ReferenceSecurityRestrictions {
  foreach ($fixture in @('signature-visible', 'signature-incremental')) {
    foreach ($mode in @('plain', 'preserve', 'decrypt')) {
      foreach ($route in @('argv', 'job')) {
        $name = "security-restrictions-$fixture-$mode-$route"
        $case = New-Case $name
        Copy-Item -LiteralPath "$fixtureDir/$fixture.pdf" -Destination "$case/inputs/source.pdf"
        if ($mode -eq 'plain') { Copy-Item -LiteralPath "$case/inputs/source.pdf" -Destination "$case/inputs/a.pdf" }
        else {
          $code = Invoke-Oracle $case @('inputs/source.pdf', 'inputs/a.pdf', '--encrypt', 'reader', 'owner', '256', '--')
          if ($code -ne 0) { throw "$name native signed encryption failed" }
          $code = Invoke-Oracle $case @('inputs/a.pdf', '--password=reader', '--json', '--json-key=qpdf')
          if ($code -ne 0 -or (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -notmatch '"/FT": "/Sig"') {
            throw "$name input control lost active signature fields"
          }
        }
        $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; removeRestrictions = '';
          pages = @(@{ file = '.'; range = '3,1,3' }) }
        $options = @('--remove-restrictions', '--pages', '.', '3,1,3', '--')
        if ($mode -ne 'plain') { $fields.password = 'reader'; $options += '--password=reader' }
        if ($mode -eq 'decrypt') { $fields.decrypt = ''; $options += '--decrypt' }
        if ($route -eq 'argv') {
          $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', 'outputs/a.pdf') + $options)
          $native = Invoke-Oracle $case (@('inputs/a.pdf', "$case/ora.pdf") + $options)
        }
        else {
          $fields | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
          $managed = Invoke-SecurityManaged $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
          $fields.outputFile = "$case/ora.pdf"
          $fields | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$case/native-job.json" -Encoding utf8NoBOM
          $native = Invoke-Oracle $case @('--job-json-file=native-job.json')
        }
        if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0 -or $native -ne 0) { throw "$name restriction removal failed" }
        $revision = if ($mode -eq 'preserve') { 6 } else { 0 }
        $bits = if ($revision -gt 0) { 256 } else { 0 }
        Assert-SecurityProtection $case $name 'reader' 'owner' $revision $bits -4 $true
        Assert-Trio $name "$case/lib-plain.pdf" "$case/ora-plain.pdf" 3
        $managedContent = Read-ReferenceContent $QpdfExe "$case/lib-plain.pdf"
        $nativeContent = Read-ReferenceContent $QpdfExe "$case/ora-plain.pdf"
        # Native retains unselected field appearances and duplicates some streams.
        # Compare the marks reached from each selected page, not orphan storage.
        if ($managedContent.PageText -cne $nativeContent.PageText -or
            (Read-SecuritySignatureMarks "$case/lib-plain.pdf") -cne (Read-SecuritySignatureMarks "$case/ora-plain.pdf")) {
          throw "$name selected page content or stored signature marks differ"
        }
        Assert-ReferencePageText $probeDll "$case/lib-plain.pdf" "$case/ora-plain.pdf"
        $code = Invoke-Oracle $case @('--json', '--json-key=qpdf', "$case/lib-plain.pdf")
        if ($code -ne 0 -or (Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw) -match '"/ByteRange"|"/FT": "/Sig"|"/Perms"') {
          throw "$name managed publication retained active signature semantics"
        }
        Write-Output "[$name] marks/content and protection policy agree; managed fields are disabled"
      }
    }
  }
}
