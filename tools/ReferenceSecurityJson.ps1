# Full encrypted JSON exports carry plaintext payloads, then explicit output policy.
function Read-SecurityJsonPayload {
  param([string] $Case, [hashtable] $Stream)
  if ($Stream.ContainsKey('data')) { return $Stream.data }
  $name = $Stream.datafile
  if ($name -notmatch '^inputs/(managed|native)-stream-[0-9]+$') { throw 'Unexpected security JSON sidecar path.' }
  return [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $Case $name)))
}

function Assert-SecurityJsonPayloads {
  param([string] $Case, [hashtable] $Managed, [hashtable] $Native)
  $objects = $Native.qpdf[1]
  $page = @($objects.Values | Where-Object { $_.value -and $_.value['/Type'] -eq '/Page' })
  if ($page.Count -ne 1) { throw 'Expected one page in the security JSON fixture.' }
  $catalog = $objects['obj:' + $objects.trailer.value['/Root']].value
  foreach ($reference in @($page[0].value['/Contents'], $catalog['/Metadata'])) {
    $key = 'obj:' + $reference
    $expected = Read-SecurityJsonPayload $Case $objects[$key].stream
    $actual = Read-SecurityJsonPayload $Case $Managed.qpdf[1][$key].stream
    if ($actual -cne $expected) { throw "Decrypted JSON stream differs: $key" }
  }
  $info = 'obj:' + $objects.trailer.value['/Info']
  if ($Managed.qpdf[1][$info].value['/Title'] -cne 'u:TitleText') { throw 'Decrypted JSON title differs.' }
}

function Invoke-ReferenceSecurityJson {
  foreach ($profile in @(
    @('r2', 2, 40, -64), @('r3', 3, 128, -3388), @('r4', 4, 128, -3388),
    @('r4-rc4', 4, 128, -3388), @('r5', 5, 256, -3388), @('r6', 6, 256, -3388)
  )) {
    foreach ($route in @('argv', 'job')) {
      foreach ($mode in @('inline', 'file')) {
        $name = "security-json-$($profile[0])-$route-$mode"
        $case = New-Case $name
        Copy-Item -LiteralPath "$fixtureDir/security-$($profile[0])-ascii.pdf" -Destination "$case/inputs/a.pdf"
        $nativeOptions = @('--password=reader', '--json-output=2', "--json-stream-data=$mode")
        if ($mode -eq 'file') { $nativeOptions += '--json-stream-prefix=inputs/native-stream' }
        $native = Invoke-Oracle $case (@('inputs/a.pdf', "$case/native.json") + $nativeOptions)
        if ($native -ne 0) { throw "$name native export failed ($native)" }
        $reference = Get-Content -LiteralPath "$case/native.json" -Raw | ConvertFrom-Json -AsHashtable
        $fields = @{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/full.json'; password = 'reader'; jsonOutput = '2'; jsonStreamData = $mode }
        $options = @('--password=reader', '--json-output=2', "--json-stream-data=$mode")
        $mapping = @('--out', "outputs/full.json=$case/inputs/full.json")
        if ($mode -eq 'file') {
          $fields.jsonStreamPrefix = 'inputs/managed-stream'; $options += '--json-stream-prefix=inputs/managed-stream'
          foreach ($entry in $reference.qpdf[1].GetEnumerator()) {
            if (-not $entry.Value.ContainsKey('stream')) { continue }
            $path = $entry.Value.stream.datafile
            if ($path -notmatch '^inputs/native-stream-([0-9]+)$') { throw 'Unexpected native JSON sidecar.' }
            $logical = 'inputs/managed-stream-' + $Matches[1]
            $mapping += @('--out', "$logical=$case/$logical")
          }
        }
        if ($route -eq 'argv') { $command = @('emit', '--workdir', $case) + $mapping + @('--', 'inputs/a.pdf', 'outputs/full.json') + $options }
        else {
          $fields | ConvertTo-Json | Set-Content -LiteralPath "$case/export-job.json" -Encoding utf8NoBOM
          $command = @('emit-job', '--workdir', $case, '--job', "$case/export-job.json") + $mapping
        }
        $result = Invoke-SecurityManaged $case $command
        if ($result.Status -cne 'Completed' -or $result.ExitCode -ne 0) { throw "$name managed export failed" }
        $json = Get-Content -LiteralPath "$case/inputs/full.json" -Raw | ConvertFrom-Json -AsHashtable
        Assert-SecurityJsonPayloads $case $json $reference
        # A changed plaintext string proves the update, while unmodified stream
        # payloads exercise encrypted inline/sidecar import through both writers.
        $info = 'obj:' + $json.qpdf[1].trailer.value['/Info']
        $json.qpdf[1][$info].value['/Title'] = 'u:Updated title'
        $json | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath "$case/inputs/full.json" -Encoding utf8NoBOM
        $operations = @('update', 'create')
        if ($profile[0] -in @('r4', 'r6') -and $mode -eq 'inline') { $operations += @('update-remove', 'create-remove', 'update-new', 'create-new') }
        foreach ($operation in $operations) {
          $fields = @{ outputFile = 'outputs/a.pdf' }
          if ($operation.StartsWith('update')) {
            $fields.inputFile = 'inputs/a.pdf'; $fields.password = 'reader'; $fields.updateFromJson = 'inputs/full.json'
            $options = @('--password=reader', '--update-from-json=inputs/full.json')
          } else {
            $fields.inputFile = 'inputs/full.json'; $fields.jsonInput = ''; $options = @('--json-input')
            if ($operation -eq 'create') {
              $fields.copyEncryption = 'inputs/a.pdf'; $fields.encryptionFilePassword = 'fixture-owner'
              $options += @('--copy-encryption=inputs/a.pdf', '--encryption-file-password=fixture-owner')
            }
          }
          $user = 'reader'; $owner = 'fixture-owner'; $revision = $profile[1]; $bits = $profile[2]; $permissions = $profile[3]; $metadata = $true
          if ($operation.EndsWith('-remove')) { $fields.decrypt = ''; $options += '--decrypt'; $revision = 0 }
          if ($operation.EndsWith('-new')) {
            $user = 'new-reader'; $owner = 'new-owner'; $revision = 6; $bits = 256; $permissions = -4; $metadata = $false
            $fields.encrypt = @{ userPassword = $user; ownerPassword = $owner; '256bit' = @{ cleartextMetadata = '' } }
            $options += @('--encrypt', $user, $owner, '256', '--cleartext-metadata', '--')
          }
          Invoke-SecurityPolicyCase $case "$name-$operation" $route $fields $options $user $owner $revision $bits $permissions $metadata
          foreach ($side in @('lib', 'ora')) {
            $read = Get-Content -LiteralPath "$case/$side-plain.pdf.reference.json" -Raw | ConvertFrom-Json -AsHashtable
            $info = 'obj:' + $read.qpdf[1].trailer.value['/Info']
            if ($read.qpdf[1][$info].value['/Title'] -cne 'u:Updated title') { throw "$name-$operation/$side did not retain the changed title" }
          }
          # Subsequent output must be a fresh destination for native qpdf.
          Move-Item -LiteralPath "$case/lib.pdf" -Destination "$case/lib-$operation.pdf"
          Move-Item -LiteralPath "$case/ora.pdf" -Destination "$case/ora-$operation.pdf"
          Move-Item -LiteralPath "$case/lib-plain.pdf" -Destination "$case/lib-$operation-plain.pdf"
          Move-Item -LiteralPath "$case/ora-plain.pdf" -Destination "$case/ora-$operation-plain.pdf"
        }
      }
    }
  }
}
