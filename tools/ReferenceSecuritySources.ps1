# Independently generated encrypted text sources distinguish page/placement roles.
function New-SecuritySources {
  $case = New-Case 'security-source-fixtures'
  Copy-Item -LiteralPath "$fixtureDir/minimal-1page.pdf" -Destination "$case/inputs/base.pdf"
  $code = Invoke-Oracle $case @('inputs/base.pdf', '--json-output=2', '--json-stream-data=inline', "$case/base.json")
  if ($code -ne 0) { throw 'Native source JSON export failed.' }
  [IO.File]::WriteAllText("$case/payload.bin", "Independent attachment payload.`n", [Text.UTF8Encoding]::new($false))
  foreach ($source in @(
    @('primary', 'Primary', 700, 256, ''), @('secondary', 'Secondary', 700, 128, 'n'),
    @('overlay', 'Overlay', 620, 128, 'y'), @('attachments', 'Attachment', 540, 128, 'y')
  )) {
    $name = $source[0]
    $json = Get-Content -LiteralPath "$case/base.json" -Raw | ConvertFrom-Json -AsHashtable
    $page = @($json.qpdf[1].Values | Where-Object { $_.value -and $_.value['/Type'] -eq '/Page' })[0]
    $contents = 'obj:' + $page.value['/Contents']
    $json.qpdf[1][$contents].stream.data = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("BT /F1 24 Tf 100 $($source[2]) Td ($($source[1])) Tj ET`n"))
    $json | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$case/$name.json" -Encoding utf8NoBOM
    $options = @('--allow-weak-crypto', '--encrypt', "$name-user", "$name-owner", [string]$source[3])
    if ($source[4]) { $options += "--use-aes=$($source[4])" }
    $options += @('--', '--object-streams=generate')
    if ($name -eq 'attachments') { $options += @('--add-attachment', "$case/payload.bin", '--key=private.bin', '--filename=private.bin', '--') }
    $code = Invoke-Oracle $case (@('--json-input', "$case/$name.json", "$case/inputs/$name.pdf") + $options)
    if ($code -ne 0) { throw "Native encrypted source generation failed: $name" }
    $code = Invoke-Oracle $case @("--password=$name-owner", '--check', "$case/inputs/$name.pdf")
    if ($code -ne 0) { throw "Native encrypted source check failed: $name" }
  }
  return $case
}

function Assert-SecuritySourceOutput {
  param([string] $Case, [string] $Name, [string[]] $Markers, [bool] $Overlay, [bool] $Attachment, [bool] $Template, [bool] $NativeAttachment)
  $user = if ($Template) { '' } else { 'primary-user' }
  $owner = if ($Template) { 'fixture-owner' } else { 'primary-owner' }
  $revision = if ($Template) { 5 } else { 6 }
  $permissions = if ($Template) { -3388 } else { -4 }
  Assert-SecurityProtection $Case $Name $user $owner $revision 256 $permissions (-not $Template)
  Assert-Trio $Name "$Case/lib-plain.pdf" "$Case/ora-plain.pdf" $Markers.Count
  Assert-ReferencePageText $probeDll "$Case/lib-plain.pdf" "$Case/ora-plain.pdf"
  foreach ($side in @('lib', 'ora')) {
    $text = Invoke-ReferenceProbe -Arguments @($probeDll, 'page-text', '--input', "$Case/$side-plain.pdf") | Out-String
    if ($LASTEXITCODE -ne 0) { throw "$Name independent page text failed" }
    $pages = ConvertFrom-Json -InputObject $text -NoEnumerate
    for ($index = 0; $index -lt $Markers.Count; $index++) {
      if (-not $pages[$index].Contains($Markers[$index]) -or ($Overlay -and -not $pages[$index].Contains('Overlay'))) {
        throw "$Name independent text lost a source marker"
      }
    }
    if ($Attachment) {
      $capture = "$Case/$side-attachment.bin"
      $process = Start-ReferenceProcess -FilePath $QpdfExe -ArgumentList @("--password=$owner", '--show-attachment=copy-private.bin', "$Case/$side.pdf") -WorkingDirectory $Case -NoNewWindow -Wait -PassThru -RedirectStandardOutput $capture -RedirectStandardError "$Case/$side-attachment-error.txt"
      if ($side -eq 'ora' -and -not $NativeAttachment) {
        if ($process.ExitCode -ne 2) { throw "$Name native split unexpectedly retained its document attachment collection" }
        continue
      }
      if ($process.ExitCode -ne 0 -or [IO.File]::ReadAllText($capture) -cne "Independent attachment payload.`n") { throw "$Name attachment payload differs" }
    }
  }
}

function Invoke-ReferenceSecuritySources {
  $sources = New-SecuritySources
  foreach ($mode in @('merge', 'overlay', 'attachment', 'combined', 'split', 'empty', 'bad-page', 'bad-overlay', 'bad-attachment', 'bad-template')) {
    foreach ($route in @('argv', 'job')) {
      $name = "security-sources-$mode-$route"
      $case = New-Case $name
      foreach ($source in @('primary', 'secondary', 'overlay', 'attachments')) {
        Copy-Item -LiteralPath "$sources/inputs/$source.pdf" -Destination "$case/inputs/$source.pdf"
      }
      Copy-Item -LiteralPath "$fixtureDir/security-r5-empty.pdf" -Destination "$case/inputs/template.pdf"
      $bad = $mode.StartsWith('bad-')
      $combine = $mode -eq 'combined' -or $bad
      $merge = $mode -in @('merge', 'split', 'empty') -or $combine
      $overlay = $mode -eq 'overlay' -or $combine
      $attachment = $mode -in @('attachment', 'split') -or $combine
      $template = $mode -eq 'empty' -or $combine
      $fields = @{ outputFile = 'outputs/a.pdf'; objectStreams = 'generate' }
      $options = @('--object-streams=generate')
      if ($mode -eq 'empty') { $fields.empty = ''; $inputTokens = @('--empty') }
      else { $fields.inputFile = 'inputs/primary.pdf'; $fields.password = 'primary-user'; $inputTokens = @('inputs/primary.pdf'); $options += '--password=primary-user' }
      $markers = @('Primary')
      if ($merge) {
        $password = if ($mode -eq 'bad-page') { 'source-wrong' } else { 'secondary-user' }
        $fields.pages = @(); $options += '--pages'
        if ($mode -ne 'empty') { $fields.pages += @{ file = '.'; range = '1' }; $options += @('.', '1') }
        $fields.pages += @{ file = 'inputs/secondary.pdf'; password = $password; range = '1' }
        $options += @('inputs/secondary.pdf', "--password=$password", '1', '--')
        $markers = if ($mode -eq 'empty') { @('Secondary') } else { @('Primary', 'Secondary') }
      }
      if ($overlay) {
        $password = if ($mode -eq 'bad-overlay') { 'source-wrong' } else { 'overlay-owner' }
        $fields.overlay = @{ file = 'inputs/overlay.pdf'; password = $password; repeat = '1' }
        $options += @('--overlay', 'inputs/overlay.pdf', "--password=$password", '--repeat=1', '--')
      }
      if ($attachment) {
        $password = if ($mode -eq 'bad-attachment') { 'source-wrong' } else { 'attachments-user' }
        $fields.copyAttachmentsFrom = @(@{ file = 'inputs/attachments.pdf'; password = $password; prefix = 'copy-' })
        $options += @('--copy-attachments-from', 'inputs/attachments.pdf', "--password=$password", '--prefix=copy-', '--')
      }
      if ($template) {
        $password = if ($mode -eq 'bad-template') { 'source-wrong' } else { 'fixture-owner' }
        $fields.copyEncryption = 'inputs/template.pdf'; $fields.encryptionFilePassword = $password
        $options += @('--copy-encryption=inputs/template.pdf', "--encryption-file-password=$password")
      }
      if ($mode -eq 'split') {
        $fields.splitPages = '1'; $options += '--split-pages=1'
        $mapping = @('--out', "outputs/a-1.pdf=$case/lib-1.pdf", '--out', "outputs/a-2.pdf=$case/lib-2.pdf")
      } else { $mapping = @('--out', "outputs/a.pdf=$case/lib.pdf") }
      if ($route -eq 'argv') { $command = @('emit', '--workdir', $case) + $mapping + @('--') + $inputTokens + @('outputs/a.pdf') + $options }
      else {
        $fields | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
        $command = @('emit-job', '--workdir', $case, '--job', "$case/job.json") + $mapping
      }
      $managed = Invoke-SecurityManaged $case $command
      $nativeOptions = $options
      # Native split output clears default protection, just like QDF; compare
      # the explicitly preserved policy required by the managed output contract.
      if ($mode -eq 'split') { $nativeOptions += @('--copy-encryption=inputs/primary.pdf', '--encryption-file-password=primary-owner') }
      $native = Invoke-Oracle $case ($inputTokens + @("$case/ora.pdf") + $nativeOptions)
      if ($bad) {
        if ($managed.Status -cne 'Failed' -or $managed.ExitCode -ne 2 -or $native -ne 2 -or (Test-Path -LiteralPath "$case/lib.pdf")) { throw "$name failed to reject the independent wrong password" }
        Write-Output "[$name] wrong secondary credential rejects before managed publication"
        continue
      }
      if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0 -or $native -ne 0) { throw "$name source composition failed" }
      if ($mode -eq 'split') {
        foreach ($index in @(1, 2)) {
          Move-Item -LiteralPath "$case/lib-$index.pdf" -Destination "$case/lib.pdf"
          Move-Item -LiteralPath "$case/ora-$index.pdf" -Destination "$case/ora.pdf"
          Assert-SecuritySourceOutput $case "$name-$index" @($markers[$index - 1]) $false $true $false $false
          foreach ($side in @('lib', 'ora')) {
            Move-Item -LiteralPath "$case/$side.pdf" -Destination "$case/$side-verified-$index.pdf"
            Move-Item -LiteralPath "$case/$side-plain.pdf" -Destination "$case/$side-plain-$index.pdf"
          }
        }
      } else { Assert-SecuritySourceOutput $case $name $markers $overlay $attachment $template $true }
    }
  }
}
