# Development-only security evidence, called by the existing differential lane.
# All passwords below belong to public synthetic fixtures. Each process uses the
# bounded reference transport; decrypted copies are independent-reader controls.
function Invoke-SecurityManaged {
  param([string] $Case, [string[]] $Command)
  $result = Invoke-ReferenceProbeRequest -Arguments $Command -WorkingDirectory $Case
  $lines = Get-Content -LiteralPath $result.StandardOutput
  $status = @($lines | Select-String -Pattern '^emit: status=(\w+) exit=(\d+)$')
  if ($status.Count -ne 1) { throw "Security probe returned no unique status: $($result.StandardOutput)" }
  $textLine = @($lines | Where-Object { $_.StartsWith('emit: text ') })
  $text = if ($textLine.Count -eq 1) { ConvertFrom-Json -InputObject $textLine[0].Substring(11) } else { $null }
  $report = @($lines | Select-String -Pattern '^emit: diagnostic EncryptionReport \w+ (.*)$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
  $identity = @($lines | Select-String -Pattern '^emit: diagnostic HelpText Info (.*)$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
  return [pscustomobject]@{ Identity = $identity; Report = $report; PageCount = @($lines | Select-String -Pattern '^emit: diagnostic PageCount Info inputs/a.pdf: 1$').Count; Status = $status[0].Matches[0].Groups[1].Value; ExitCode = [int]$status[0].Matches[0].Groups[2].Value; Text = $text }
}

function Assert-SecurityProtection {
  param([string] $Case, [string] $Name, [string] $User, [string] $Owner, [int] $Revision, [int] $Bits, [int] $Permissions, [bool] $Metadata)
  foreach ($side in @('lib', 'ora')) {
    $file = "$Case/$side.pdf"
    $protected = $Revision -gt 0
    $credentials = if ($protected) { @($User, $Owner) } else { @('') }
    foreach ($credential in $credentials) {
      $code = Invoke-Oracle $Case @("--password=$credential", '--check', $file)
      if ($code -ne 0) { throw "$Name/${side}: native encrypted structure check failed ($code)" }
    }
    $code = Invoke-Oracle $Case @('--password=deliberately-wrong', '--requires-password', $file)
    $expected = if ($protected) { 0 } else { 2 }
    if ($code -ne $expected) { throw "$Name/${side}: password predicate returned $code, expected $expected" }
    $code = Invoke-Oracle $Case @("--password=$User", '--json', '--json-key=encrypt', '--json-key=qpdf', $file)
    if ($code -ne 0) { throw "$Name/${side}: encryption inspection failed" }
    $json = Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
    if ($json.encrypt.encrypted -ne $protected) { throw "$Name/${side}: protection policy differs" }
    if ($protected) {
      $parameters = $json.encrypt.parameters
      if ($parameters.R -ne $Revision -or $parameters.bits -ne $Bits -or $parameters.P -ne $Permissions) { throw "$Name/${side}: encryption parameters differ" }
      $cipher = if ($Revision -lt 4) { 'RC4' } elseif ($Revision -eq 4) { 'AESv2' } else { 'AESv3' }
      foreach ($field in @('filemethod', 'streammethod', 'stringmethod')) {
        if ($parameters[$field] -cne $cipher) { throw "$Name/${side}: $field differs" }
      }
      $trailer = $json.qpdf[1].trailer.value
      $dictionary = $json.qpdf[1]['obj:' + $trailer['/Encrypt']].value
      $actualMetadata = -not $dictionary.ContainsKey('/EncryptMetadata') -or $dictionary['/EncryptMetadata']
      if ($actualMetadata -ne $Metadata) { throw "$Name/${side}: metadata policy differs" }
    }
    $code = Invoke-Oracle $Case @("--password=$User", '--decrypt', '--object-streams=disable', $file, "$Case/$side-plain.pdf")
    if ($code -ne 0) { throw "$Name/${side}: native decryption failed" }
  }
}

function Assert-SecurityPair {
  param([string] $Case, [string] $Name, [string] $User, [string] $Owner, [int] $Revision, [int] $Bits, [int] $Permissions, [bool] $Metadata)
  Assert-SecurityProtection $Case $Name $User $Owner $Revision $Bits $Permissions $Metadata
  Assert-Trio $Name "$Case/lib-plain.pdf" "$Case/ora-plain.pdf" 1
  $managed = Read-ReferenceContent $QpdfExe "$Case/lib-plain.pdf"
  $native = Read-ReferenceContent $QpdfExe "$Case/ora-plain.pdf"
  if ($managed.PageText -cne 'BT (Page) Tj ET' -or $managed.Signature -cne $native.Signature) {
    throw "${Name}: decrypted page or metadata payloads differ"
  }
}

function Invoke-ReferenceSecurity {
  foreach ($profile in @(
    @('r2', 2, 40, -64), @('r3', 3, 128, -3388), @('r4', 4, 128, -3388),
    @('r4-rc4', 4, 128, -3388), @('r5', 5, 256, -3388), @('r6', 6, 256, -3388)
  )) {
    $prefix = $profile[0]
    # Query exit codes come from the public result, not the transport exit code.
    foreach ($kind in @('ascii', 'empty', 'plain')) {
      $case = New-Case "security-query-$prefix-$kind"
      $fixture = if ($kind -eq 'plain') { 'meta-1page.pdf' } else { "security-$prefix-$kind.pdf" }
      Copy-Item -LiteralPath "$fixtureDir/$fixture" -Destination "$case/inputs/a.pdf"
      foreach ($query in @(@('--is-encrypted', 'isEncrypted'), @('--requires-password', 'requiresPassword'), @('--show-npages', 'showNpages'))) {
        foreach ($credential in @($null, 'reader', 'fixture-owner', 'deliberately-wrong')) {
          $password = if ($null -eq $credential) { @() } else { @("--password=$credential") }
          $matched = $kind -eq 'plain' -or $credential -eq 'fixture-owner' -or ($kind -eq 'empty' -and $null -eq $credential) -or ($kind -eq 'ascii' -and $credential -eq 'reader')
          $expected = if ($query[0] -eq '--is-encrypted') { if ($kind -eq 'plain') { 2 } else { 0 } }
            elseif ($query[0] -eq '--requires-password') { if ($kind -eq 'plain') { 2 } elseif ($matched) { 3 } else { 0 } }
            else { if ($matched) { 0 } else { 2 } }
          $native = Invoke-Oracle $case (@('inputs/a.pdf', $query[0]) + $password)
          if ($native -ne $expected) { throw "security-query-$prefix-${kind}: native $($query[0]) returned $native, expected $expected" }
          foreach ($route in @('argv', 'job')) {
            if ($route -eq 'argv') { $command = @('emit', '--workdir', $case, '--', 'inputs/a.pdf', $query[0]) + $password }
            else {
              $fields = @{ inputFile = 'inputs/a.pdf'; $query[1] = '' }
              if ($null -ne $credential) { $fields.password = $credential }
              $fields | ConvertTo-Json | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
              $command = @('emit-job', '--workdir', $case, '--job', "$case/job.json")
            }
            $managed = Invoke-SecurityManaged $case $command
            if ($managed.ExitCode -ne $expected) { throw "security-query-$prefix-$kind/${route}: $($query[0]) returned $($managed.ExitCode), expected $expected" }
            if ($query[0] -eq '--show-npages' -and $expected -eq 0 -and $managed.PageCount -ne 1) { throw 'Security query page count differs.' }
            if ($query[0] -ne '--show-npages' -and $managed.Status -cne 'Completed') { throw 'Security predicate did not complete.' }
          }
        }
      }
      Write-Output "[security-query-$prefix-$kind] argv/job and native query results agree"
    }
    foreach ($mode in @('classic', 'objects', 'qdf', 'linearized', 'decrypt')) {
      foreach ($route in @('argv', 'job')) {
        $name = "security-preserve-$prefix-$mode-$route"
        $case = New-Case $name
        Copy-Item -LiteralPath "$fixtureDir/security-$prefix-ascii.pdf" -Destination "$case/inputs/a.pdf"
        $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; password = 'reader' }
        $options = @('--password=reader')
        switch ($mode) {
          'classic' { $options += '--object-streams=disable'; $fields.objectStreams = 'disable' }
          'objects' { $options += '--object-streams=generate'; $fields.objectStreams = 'generate' }
          'qdf' { $options += '--qdf'; $fields.qdf = '' }
          'linearized' { $options += @('--linearize', '--object-streams=generate'); $fields.linearize = ''; $fields.objectStreams = 'generate' }
          'decrypt' { $options += '--decrypt'; $fields.decrypt = '' }
        }
        $mapping = @('--out', "outputs/a.pdf=$case/lib.pdf")
        if ($route -eq 'argv') { $command = @('emit', '--workdir', $case) + $mapping + @('--', 'inputs/a.pdf', 'outputs/a.pdf') + $options }
        else {
          $fields | ConvertTo-Json | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
          $command = @('emit-job', '--workdir', $case, '--job', "$case/job.json") + $mapping
        }
        $managed = Invoke-SecurityManaged $case $command
        if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0) { throw "$name managed rewrite failed" }
        # Native QDF clears default encryption; explicitly request the policy that
        # the managed preserve-by-default contract applies to every writer.
        $nativeOptions = $options
        if ($mode -eq 'qdf') { $nativeOptions += @('--copy-encryption=inputs/a.pdf', '--encryption-file-password=reader') }
        $native = Invoke-Oracle $case (@('inputs/a.pdf', "$case/ora.pdf") + $nativeOptions)
        if ($native -ne 0) { throw "$name native rewrite failed ($native)" }
        $revision = if ($mode -eq 'decrypt') { 0 } else { $profile[1] }
        Assert-SecurityPair $case $name 'reader' 'fixture-owner' $revision $profile[2] $profile[3] $true
      }
    }
  }
  Write-Output '[security] R2–R6 queries, password failures, preservation and removal agree across both routes and writers'
}
