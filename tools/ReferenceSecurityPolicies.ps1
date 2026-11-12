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
