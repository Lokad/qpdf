function Invoke-ReferenceJsonSplit {
  $case = New-Case 'json-split'
  $source = 'inputs/source.pdf'
  $fixture = Join-Path $fixtureDir 'minimal-2page.pdf'
  if ((Invoke-Oracle $case @('--empty','--pages',$fixture,'1-2,1','--',$source)) -ne 0 -or
      (Invoke-Oracle $case @($source,'--json-output','--decode-level=generalized',"$case/export.json")) -ne 0) {
    throw 'Could not prepare the JSON split source.'
  }
  $json = Get-Content -LiteralPath "$case/export.json" -Raw | ConvertFrom-Json -AsHashtable
  $header = $json.qpdf[0]; $objects = $json.qpdf[1]
  $catalog = $objects['obj:' + $objects.trailer.value['/Root']].value
  $rootPair = $catalog['/Pages']
  $root = $objects['obj:' + $rootPair].value
  $kids = @($root['/Kids'])
  if ($kids.Count -ne 3) { throw 'The source needs three page occurrences.' }
  $header.maxobjectid++
  $nested = "$($header.maxobjectid) 0 R"
  $objects['obj:' + $nested] = @{ value=@{ '/Type'='/Pages'; '/Parent'=$rootPair; '/Count'=2;
    '/Kids'=@($kids[0],$kids[1]); '/MediaBox'=@(0,0,612,792) } }
  $root['/Kids'] = @($nested,$kids[2])
  $catalog['/PageLabels'] = @{ '/Nums'=@(0,@{ '/S'='/D'; '/St'=7; '/P'='u:Audit-' }) }
  foreach ($pair in $kids[0..1]) {
    $page = $objects['obj:' + $pair].value
    $null = $page.Remove('/MediaBox')
    $page['/Parent'] = $nested
  }
  $third = $objects['obj:' + $kids[2]].value
  $stream = $objects['obj:' + $third['/Contents']].stream
  $original = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($stream.data))
  if (!$original.Contains('(First)', [StringComparison]::Ordinal)) { throw 'Missing original third-page content marker.' }
  $header.maxobjectid++
  $third['/Contents'] = "$($header.maxobjectid) 0 R"
  $objects['obj:' + $third['/Contents']] = @{stream=@{dict=$stream.dict;
    data=[Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($original.Replace('(First)','(Third)')))}}
  $objects.trailer.value['/Size'] = $header.maxobjectid + 1
  $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$case/inputs/create.json" -Encoding utf8NoBOM
  $root['/Kids'] = @($kids[2],$nested)
  $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$case/inputs/update.json" -Encoding utf8NoBOM

  foreach ($mode in @('create','update')) {
    $tag = "json-split/$mode"
    $native = "$case/native-$mode.pdf"
    $managed = "managed-$mode"
    $mappings = @('--out',"$managed-1-2.pdf=$case/$managed-1-2.pdf",'--out',"$managed-3.pdf=$case/$managed-3.pdf")
    if ($mode -eq 'create') {
      $code = Invoke-Emit $case (@('emit','--workdir',$case) + $mappings + @('--','inputs/create.json','--json-input','--split-pages=2',"$managed.pdf"))
      $oracle = Invoke-Oracle $case @('inputs/create.json','--json-input','--split-pages=2',$native)
      $expected = @(@('First','Second'),@('Third'))
      $style = '/D'; $prefix = 'u:Audit-'
    } else {
      $job = "$case/update-job.json"
      @{ inputFile=$source; updateFromJson='inputs/update.json'; splitPages='2'; outputFile="$managed.pdf";
         setPageLabels=@('1:r/7/NEW-') } |
        ConvertTo-Json | Set-Content -LiteralPath $job -Encoding utf8NoBOM
      $code = Invoke-Emit $case (@('emit-job','--workdir',$case,'--job',$job) + $mappings)
      $oracle = Invoke-Oracle $case @($source,'--update-from-json=inputs/update.json','--split-pages=2',
          '--set-page-labels','1:r/7/NEW-','--',$native)
      $expected = @(@('Third','First'),@('Second'))
      $style = '/r'; $prefix = 'u:NEW-'
    }
    if ($code -ne 0 -or $oracle -ne 0) { throw "$tag could not publish both parts." }
    for ($i = 0; $i -lt 2; $i++) {
      $managedSuffix = @('1-2','3')[$i]; $nativeSuffix = @('1-2','3-3')[$i]
      $lib = "$case/$managed-$managedSuffix.pdf"; $ora = "$case/native-$mode-$nativeSuffix.pdf"
      Assert-Trio "$tag/$managedSuffix" $lib $ora $expected[$i].Count
      Assert-ReferenceContent $QpdfExe $probeDll $lib $ora
      foreach ($file in @($lib,$ora)) {
        $viewPath = "$file.reference.json"
        if ((Invoke-Oracle $case @($file,'--json','--json-key=pagelabels','--json-key=pages','--json-key=qpdf',$viewPath)) -ne 0) {
          throw "$tag page and label report unavailable."
        }
        $view = Get-Content -LiteralPath $viewPath -Raw | ConvertFrom-Json -AsHashtable
        $labels = $view.pagelabels
        if ($labels.Count -ne 1 -or $labels[0].index -ne 0 -or $labels[0].label['/St'] -ne @(7,9)[$i] -or
            $labels[0].label['/S'] -cne $style -or $labels[0].label['/P'] -cne $prefix) {
          throw "$tag labels do not continue through $managedSuffix."
        }
        foreach ($page in $view.pages) {
          $node = $view.qpdf[1]['obj:' + $page.object].value
          $box = $node['/MediaBox']
          if ($box -is [string]) { $box = $view.qpdf[1]['obj:' + $box].value }
          if (($box -join ',') -cne '0,0,612,792') { throw "$tag dropped an inherited MediaBox." }
        }
      }
      $text = @(Invoke-ReferenceProbe -Arguments @($probeDll,'page-text','--input',$lib)) -join "`n"
      if ($LASTEXITCODE -ne 0 -or ((ConvertFrom-Json $text) -join '|') -cne ($expected[$i] -join '|')) {
        throw "$tag output order differs for $managedSuffix."
      }
    }
    Write-Output "[$tag] bound page order, inherited frame, continued labels and split names verified."
  }
}
