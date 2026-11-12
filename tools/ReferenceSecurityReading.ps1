# Non-secret reporting and credential representations over recorded native inputs.
function ConvertTo-SecurityFacts {
  param([hashtable] $Encryption)
  $facts = [ordered]@{}
  foreach ($name in @('encrypted', 'ownerpasswordmatched', 'userpasswordmatched')) { $facts[$name] = $Encryption[$name] }
  foreach ($section in @('capabilities', 'parameters')) {
    $facts[$section] = [ordered]@{}
    foreach ($name in @($Encryption[$section].Keys | Sort-Object)) { $facts[$section][$name] = $Encryption[$section][$name] }
  }
  return ConvertTo-Json -InputObject $facts -Depth 8 -Compress
}

function Assert-SecurityJson {
  param([string] $Case, [string] $Name, [string[]] $Options, [hashtable] $Fields, [string[]] $NativeOptions)
  $oracleOptions = if ($null -eq $NativeOptions -or $NativeOptions.Count -eq 0) { $Options } else { $NativeOptions }
  $native = Invoke-Oracle $Case (@('inputs/a.pdf', '--json', '--json-key=encrypt') + $oracleOptions)
  if ($native -ne 0) { throw "$Name native JSON inspection failed ($native)" }
  $reference = Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
  $expected = ConvertTo-SecurityFacts $reference.encrypt
  foreach ($route in @('argv', 'job')) {
    if ($route -eq 'argv') { $command = @('emit', '--workdir', $Case, '--', 'inputs/a.pdf', '--json', '--json-key=encrypt') + $Options }
    else {
      $job = @{ inputFile = 'inputs/a.pdf'; json = '2'; jsonKey = @('encrypt') }
      foreach ($key in $Fields.Keys) { $job[$key] = $Fields[$key] }
      $job | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Case/job.json" -Encoding utf8NoBOM
      $command = @('emit-job', '--workdir', $Case, '--job', "$Case/job.json")
    }
    $result = Invoke-SecurityManaged $Case $command
    if ($result.ExitCode -ne 0 -or $result.Status -cne 'Completed') { throw "$Name/$route managed JSON inspection failed" }
    $json = $result.Text | ConvertFrom-Json -AsHashtable
    if ($null -ne $json.encrypt.recovereduserpassword -or $null -ne $json.encrypt.parameters.key) { throw "$Name/$route disclosed password material" }
    if ((ConvertTo-SecurityFacts $json.encrypt) -cne $expected) { throw "$Name/$route non-secret encryption facts differ" }
  }
}

function Invoke-ReferenceCryptoProvider {
  $case = New-Case 'security-provider'
  [IO.File]::WriteAllText("$case/inputs/args", "--show-crypto`n", [Text.UTF8Encoding]::new($false))
  foreach ($option in @('--show-crypto', '@inputs/args')) {
    $native = Invoke-Oracle $case @($option)
    $providers = @(Get-Content -LiteralPath "$case/ora-stdout.txt" | Where-Object { $_ -match '^[a-zA-Z0-9_-]+$' })
    if ($native -ne 0 -or $providers.Count -eq 0) { throw 'Native crypto provider query failed' }
    $managed = Invoke-SecurityManaged $case @('emit', '--workdir', $case, '--', $option)
    if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0 -or $managed.Identity.Count -ne 1 -or $managed.Identity[0] -cne 'managed') {
      throw 'Managed crypto implementation query failed'
    }
  }
  foreach ($options in @(
    @('--show-crypto=value'), @('--show-crypto', 'missing.pdf', 'out.pdf'),
    @('missing.pdf', '--show-crypto', 'out.pdf'), @('--version', '--show-crypto'),
    @('--show-crypto', '--help'), @('--show-crypto', '--show-crypto')
  )) {
    $native = Invoke-Oracle $case $options
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--') + $options)
    if ($native -ne 2 -or $managed.Status -cne 'Failed' -or $managed.ExitCode -ne 2 -or (Test-Path -LiteralPath "$case/out.pdf")) {
      throw 'Invalid crypto provider query did not reject without publication'
    }
  }
  Write-Output '[security-provider] standalone/response-file queries and invalid forms agree; implementation names are build-specific'
}

function Invoke-ReferenceSecurityReading {
  Invoke-ReferenceCryptoProvider
  foreach ($prefix in @('r2', 'r3', 'r4', 'r4-rc4', 'r5', 'r6')) {
    $vectors = Get-Content -LiteralPath "$fixtureDir/security-$prefix-vectors.json" -Raw | ConvertFrom-Json
    foreach ($vector in $vectors) {
      $name = 'security-encoding-' + [IO.Path]::GetFileNameWithoutExtension($vector.file)
      $case = New-Case $name
      Copy-Item -LiteralPath "$fixtureDir/$($vector.file)" -Destination "$case/inputs/a.pdf"
      foreach ($hex in @($vector.userHex, $vector.ownerHex)) {
        Assert-SecurityJson $case $name @('--password-mode=hex-bytes', "--password=$hex") @{ passwordMode = 'hex-bytes'; password = $hex }
      }
      if ($vector.file.EndsWith('-pdfdoc.pdf')) {
        Assert-SecurityJson $case $name @('--password=café•') @{ password = 'café•' }
      }
      if ($vector.file.EndsWith('-ascii.pdf')) {
        # The shared host reads only the first line; its remaining bytes are inert.
        [IO.File]::WriteAllText("$case/inputs/password.txt", "reader`r`nignored-private-tail", [Text.UTF8Encoding]::new($false))
        Assert-SecurityJson $case $name @('--password-file=inputs/password.txt') @{ passwordFile = 'inputs/password.txt' }
      }
      Write-Output "[$name] argv/job encryption facts match native with both raw credentials"
    }
    foreach ($kind in @('ascii', 'empty')) {
      $name = "security-report-$prefix-$kind"
      $case = New-Case $name
      Copy-Item -LiteralPath "$fixtureDir/security-$prefix-$kind.pdf" -Destination "$case/inputs/a.pdf"
      foreach ($credential in @($null, 'reader', 'fixture-owner', 'deliberately-wrong')) {
        $options = if ($null -eq $credential) { @() } else { @("--password=$credential") }
        $native = Invoke-Oracle $case (@('inputs/a.pdf', '--show-encryption') + $options)
        if ($native -ne 0) { throw "$name native report failed ($native)" }
        # Native can recover the old user password. The public managed report
        # intentionally omits that line, while retaining all non-secret facts.
        $expected = @(Get-Content -LiteralPath "$case/ora-stdout.txt" | Where-Object { $_ -notmatch '^User password = ' }) -join "`n"
        foreach ($route in @('argv', 'job')) {
          if ($route -eq 'argv') { $command = @('emit', '--workdir', $case, '--', 'inputs/a.pdf', '--show-encryption') + $options }
          else {
            $fields = @{ inputFile = 'inputs/a.pdf'; showEncryption = '' }
            if ($null -ne $credential) { $fields.password = $credential }
            $fields | ConvertTo-Json | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
            $command = @('emit-job', '--workdir', $case, '--job', "$case/job.json")
          }
          $managed = Invoke-SecurityManaged $case $command
          if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0 -or ($managed.Report -join "`n") -cne $expected) {
            throw "$name/$route non-secret encryption report differs"
          }
        }
      }
      Write-Output "[$name] argv/job declared settings and authentication reports match native"
    }
  }
}
