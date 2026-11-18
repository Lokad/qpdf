function Invoke-ReferenceJsonReplacement {
  $case = New-Case 'json-update-replace'
  $source = "$case/inputs/source.pdf"
  Copy-Item -LiteralPath (Join-Path $fixtureDir 'minimal-1page.pdf') -Destination $source
  $sourceHash = (Get-FileHash -LiteralPath $source).Hash
  $report = "$case/original.json"
  $readPage = @('--json=2','--json-key=pages','--json-key=qpdf')
  if ((Invoke-Oracle $case (@($source) + $readPage + @($report))) -ne 0) {
    throw 'Unable to read the source object pair for replacement.'
  }
  $view = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
  $pageKey = 'obj:' + $view.pages[0].object
  $page = $view.qpdf[1][$pageKey].value
  $page['/ReviewMarker'] = 'u:patched in place'
  $patch = 'inputs/update.json'
  @{ qpdf=@(@{ jsonversion=2 },@{ $pageKey=@{ value=$page } }) } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$case/$patch"
  $patchHash = (Get-FileHash -LiteralPath "$case/$patch").Hash

  function Assert-EditedPage([string] $Pdf) {
    $projection = "$Pdf.json"
    if ((Invoke-Oracle $case (@($Pdf) + $readPage + @($projection))) -ne 0) {
      throw "Cannot inspect the edited page in $Pdf."
    }
    $result = Get-Content -LiteralPath $projection -Raw | ConvertFrom-Json -AsHashtable
    $edited = $result.qpdf[1]['obj:' + $result.pages[0].object].value
    if ($edited['/ReviewMarker'] -cne 'u:patched in place') { throw "The edited page marker is missing from $Pdf." }
  }

  $native = "$case/native.pdf"
  Copy-Item -LiteralPath $source -Destination $native
  if ((Invoke-Oracle $case @($native,"--update-from-json=$patch",'--replace-input')) -ne 0 -or
      (Get-FileHash -LiteralPath $native).Hash -ceq $sourceHash) {
    throw 'Native JSON replacement did not update its source.'
  }
  Assert-EditedPage $native
  Assert-ReferenceContent $QpdfExe $probeDll $source $native

  foreach ($route in @('argv','job')) {
    $tag = "json-update-replace/$route"
    $output = "$case/lib-$route.pdf"
    $mapping = "inputs/source.pdf=$output"
    if ($route -eq 'job') {
      $job = "$case/job.json"
      @{ inputFile='inputs/source.pdf'; updateFromJson=$patch; replaceInput='' } |
        ConvertTo-Json | Set-Content -LiteralPath $job
      $code = Invoke-Emit $case @('emit-job','--workdir',$case,'--job',$job,'--out',$mapping)
    } else {
      $code = Invoke-Emit $case @('emit','--workdir',$case,'--out',$mapping,'--',
        'inputs/source.pdf',"--update-from-json=$patch",'--replace-input')
    }
    if ($code -ne 0) { throw "$tag failed to publish to its source key." }
    Assert-Trio $tag $output $native 1
    Assert-ReferenceContent $QpdfExe $probeDll $output $native
    Assert-EditedPage $output
    Write-Output "[$tag] replaced the source key and retained the exact referenced content."
  }

  $broken = 'inputs/broken.json'
  [IO.File]::WriteAllText("$case/$broken", '{')
  $prior = "$case/native-prior.pdf"
  Copy-Item -LiteralPath $source -Destination $prior
  if ((Invoke-Oracle $case @($prior,"--update-from-json=$broken",'--replace-input')) -ne 2 -or
      (Get-FileHash -LiteralPath $prior).Hash -cne $sourceHash) {
    throw 'Malformed JSON changed the native replacement target.'
  }
  $absent = "$case/rejected-managed.pdf"
  if ((Invoke-Emit $case @('emit','--workdir',$case,'--out',"inputs/source.pdf=$absent",'--',
      'inputs/source.pdf',"--update-from-json=$broken",'--replace-input')) -ne 2 -or (Test-Path -LiteralPath $absent)) {
    throw 'Managed JSON replacement did not reject a malformed patch.'
  }
  if ((Get-FileHash -LiteralPath $source).Hash -cne $sourceHash -or
      (Get-FileHash -LiteralPath "$case/$patch").Hash -cne $patchHash) { throw 'A mapped reference input changed.' }
  Write-Output '[json-update-replace/negative] malformed patch preserves the native target and yields no managed output.'
}
