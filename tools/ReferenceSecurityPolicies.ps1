# Additional public-policy comparisons using the common security/reference helpers.
function Invoke-SecurityPolicyCase {
  param([string] $Case, [string] $Name, [string] $Route, [hashtable] $Fields, [string[]] $Options,
    [string] $User, [string] $Owner, [int] $Revision, [int] $Bits, [int] $Permissions, [bool] $Metadata)
  $mapping = @('--out', "$($Fields.outputFile)=$Case/lib.pdf")
  if ($Route -eq 'argv') { $command = @('emit', '--workdir', $Case) + $mapping + @('--', $Fields.inputFile, $Fields.outputFile) + $Options }
  else {
    $Fields | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$Case/job.json" -Encoding utf8NoBOM
    $command = @('emit-job', '--workdir', $Case, '--job', "$Case/job.json") + $mapping
  }
  $managed = Invoke-SecurityManaged $Case $command
  if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0) { throw "$Name managed policy failed" }
  $native = Invoke-Oracle $Case (@($Fields.inputFile, "$Case/ora.pdf") + $Options)
  if ($native -ne 0) { throw "$Name native policy failed ($native)" }
  Assert-SecurityPair $Case $Name $User $Owner $Revision $Bits $Permissions $Metadata
}

function Invoke-ReferenceSecurityPolicies {
  Invoke-ReferenceIdentityCrypt
  $case = New-Case 'security-create-named'
  Copy-Item -LiteralPath "$fixtureDir/meta-1page.pdf" -Destination "$case/inputs/a.pdf"
  Invoke-SecurityPolicyCase $case 'security-create-named' 'argv' @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf' } @(
    '--encrypt', '--user-password=reader', '--owner-password=owner', '--bits=256', '--'
  ) 'reader' 'owner' 6 256 -4 $true
  foreach ($mode in @('auto', 'bytes', 'unicode', 'hex-bytes')) {
    foreach ($route in @('argv', 'job')) {
      $name = "security-output-password-$mode-$route"
      $case = New-Case $name
      Copy-Item -LiteralPath "$fixtureDir/meta-1page.pdf" -Destination "$case/inputs/a.pdf"
      $userHex = if ($mode -eq 'hex-bytes') { '636166E980' } else { '636166C3A9E280A2' }
      $ownerHex = if ($mode -eq 'hex-bytes') { '6F776EE97280' } else { '6F776EC3A972E280A2' }
      $user = if ($mode -eq 'hex-bytes') { $userHex } else { 'café•' }
      $owner = if ($mode -eq 'hex-bytes') { $ownerHex } else { 'ownér•' }
      $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; passwordMode = $mode;
        encrypt = @{ userPassword = $user; ownerPassword = $owner; '256bit' = @{} } }
      Invoke-SecurityPolicyCase $case $name $route $fields @("--password-mode=$mode", '--encrypt', $user, $owner, '256', '--') 'café•' 'ownér•' 6 256 -4 $true
      foreach ($side in @('lib', 'ora')) {
        foreach ($hex in @($userHex, $ownerHex)) {
          $code = Invoke-Oracle $case @('--password-mode=hex-bytes', "--password=$hex", '--suppress-password-recovery', '--check', "$case/$side.pdf")
          if ($code -ne 0) { throw "$name/$side output changed the requested password bytes" }
        }
      }
    }
  }
  foreach ($route in @('argv', 'job')) {
    $name = "security-template-recovery-$route"
    $case = New-Case $name
    Copy-Item -LiteralPath "$fixtureDir/security-r6-ascii.pdf" -Destination "$case/inputs/a.pdf"
    Copy-Item -LiteralPath "$fixtureDir/security-recovery-r6-macroman.pdf" -Destination "$case/inputs/template.pdf"
    $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; password = 'reader';
      copyEncryption = 'inputs/template.pdf'; encryptionFilePassword = 'ownér•' }
    Invoke-SecurityPolicyCase $case $name $route $fields @('--password=reader', '--copy-encryption=inputs/template.pdf', '--encryption-file-password=ownér•') 'café•' 'ownér•' 6 256 -4 $true
  }
  foreach ($mode in @('classic', 'objects', 'qdf', 'linearized')) {
    foreach ($route in @('argv', 'job')) {
      $name = "security-create-$mode-$route"
      $case = New-Case $name
      Copy-Item -LiteralPath "$fixtureDir/security-r4-ascii.pdf" -Destination "$case/inputs/a.pdf"
      $user = if ($mode -eq 'objects') { '' } else { 'new-reader-€' }
      $owner = if ($mode -eq 'objects') { '' } else { 'new-owner-漢' }
      $metadata = $mode -eq 'classic'
      $permissions = @{ print = 'low'; modify = 'none'; extract = 'n' }
      $options = @('--password=reader', '--encrypt', $user, $owner, '256', '--print=low', '--modify=none', '--extract=n')
      if (-not $metadata) { $permissions.cleartextMetadata = ''; $options += '--cleartext-metadata' }
      $options += '--'
      $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; password = 'reader';
        encrypt = @{ userPassword = $user; ownerPassword = $owner; '256bit' = $permissions } }
      switch ($mode) {
        'classic' { $options += '--object-streams=disable'; $fields.objectStreams = 'disable' }
        'objects' { $options += '--object-streams=generate'; $fields.objectStreams = 'generate' }
        'qdf' { $options += '--qdf'; $fields.qdf = '' }
        'linearized' { $options += @('--linearize', '--object-streams=generate'); $fields.linearize = ''; $fields.objectStreams = 'generate' }
      }
      Invoke-SecurityPolicyCase $case $name $route $fields $options $user $owner 6 256 -3388 $metadata
    }
  }
  foreach ($profile in @(
    @('r2', 2, 40, -64), @('r3', 3, 128, -3388), @('r4', 4, 128, -3388),
    @('r4-rc4', 4, 128, -3388), @('r5', 5, 256, -3388), @('r6', 6, 256, -3388)
  )) {
    foreach ($kind in @('ascii', 'empty')) {
      foreach ($route in @('argv', 'job')) {
        $name = "security-template-$($profile[0])-$kind-$route"
        $case = New-Case $name
        Copy-Item -LiteralPath "$fixtureDir/security-r6-ascii.pdf" -Destination "$case/inputs/a.pdf"
        Copy-Item -LiteralPath "$fixtureDir/security-$($profile[0])-$kind.pdf" -Destination "$case/inputs/template.pdf"
        # Authenticate the independent template with its owner, retaining both entries.
        $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; password = 'reader';
          copyEncryption = 'inputs/template.pdf'; encryptionFilePassword = 'fixture-owner' }
        $options = @('--password=reader', '--copy-encryption=inputs/template.pdf', '--encryption-file-password=fixture-owner')
        if ($kind -eq 'empty') { $fields.qdf = ''; $options += '--qdf' }
        else { $fields.linearize = ''; $fields.objectStreams = 'generate'; $options += @('--linearize', '--object-streams=generate') }
        $user = if ($kind -eq 'empty') { '' } else { 'reader' }
        $metadata = $kind -ne 'empty' -or $profile[1] -lt 4
        Invoke-SecurityPolicyCase $case $name $route $fields $options $user 'fixture-owner' $profile[1] $profile[2] $profile[3] $metadata
      }
    }
  }
  Write-Output '[security-policy] fresh AES-256 and independently authenticated templates preserve their requested policies'
}

function Invoke-ReferenceIdentityCrypt {
  foreach ($mode in @('classic', 'qdf', 'objects', 'linearized', 'json-create', 'json-update')) {
    $name = "security-identity-crypt-$mode"
    $case = New-Case $name
    Copy-Item -LiteralPath "$fixtureDir/identity-crypt-1page.pdf" -Destination "$case/inputs/a.pdf"
    $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf';
      encrypt = @{ userPassword = 'reader'; ownerPassword = 'owner'; '256bit' = @{} } }
    $options = @('--encrypt', 'reader', 'owner', '256', '--')
    if ($mode.StartsWith('json-')) {
      $code = Invoke-Oracle $case @('inputs/a.pdf', '--json-output=2', '--json-stream-data=inline', '--decode-level=none')
      if ($code -ne 0) { throw "$name raw source export failed" }
      $json = Get-Content -LiteralPath "$case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
      $stream = $json.qpdf[1]['obj:4 0 R'].stream
      if ($stream.dict['/Filter'][0] -cne '/Crypt' -or $stream.dict['/DecodeParms'][0]['/Name'] -cne '/Identity') {
        throw "$name source export lost the Identity stage"
      }
      $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$case/inputs/source.json" -Encoding utf8NoBOM
      if ($mode -eq 'json-create') {
        $fields.inputFile = 'inputs/source.json'; $fields.jsonInput = ''; $options += '--json-input'
      } else { $fields.updateFromJson = 'inputs/source.json'; $options += '--update-from-json=inputs/source.json' }
    }
    switch ($mode) {
      'qdf' { $fields.qdf = ''; $options += '--qdf' }
      'objects' { $fields.objectStreams = 'generate'; $options += '--object-streams=generate' }
      'linearized' { $fields.linearize = ''; $fields.objectStreams = 'generate'; $options += @('--linearize', '--object-streams=generate') }
    }
    $route = if ($mode -in @('objects', 'linearized', 'json-update')) { 'job' } else { 'argv' }
    $mapping = @('--out', "outputs/a.pdf=$case/lib.pdf")
    if ($route -eq 'argv') { $command = @('emit', '--workdir', $case) + $mapping + @('--', $fields.inputFile, $fields.outputFile) + $options }
    else {
      $fields | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
      $command = @('emit-job', '--workdir', $case, '--job', "$case/job.json") + $mapping
    }
    $managed = Invoke-SecurityManaged $case $command
    if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0) { throw "$name managed encryption failed" }
    $code = Invoke-Oracle $case (@($fields.inputFile, "$case/ora.pdf") + $options)
    if ($code -ne 0) { throw "$name native encryption failed" }
    Assert-SecurityProtection $case $name 'reader' 'owner' 6 256 -4 $true
    Assert-Trio $name "$case/lib-plain.pdf" "$case/ora-plain.pdf" 1
    $left = Read-ReferenceContent $QpdfExe "$case/lib-plain.pdf"
    $right = Read-ReferenceContent $QpdfExe "$case/ora-plain.pdf"
    if ($left.Signature -cne $right.Signature -or $left.PageText -cne "q Q`n") { throw "$name decoded payload differs" }
    foreach ($side in @('lib', 'ora')) {
      $decoded = Get-Content -LiteralPath "$case/$side-plain.pdf.reference.json" -Raw | ConvertFrom-Json -AsHashtable
      foreach ($entry in $decoded.qpdf[1].Values) {
        if ($entry.stream -and '/Crypt' -in @($entry.stream.dict['/Filter'])) { throw "$name retained an active Crypt stage" }
      }
    }
  }
  Write-Output '[security-identity-crypt] Identity stages normalize before protected PDF and JSON publication'
}
