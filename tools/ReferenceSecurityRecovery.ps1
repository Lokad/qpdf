# Recorded native single-byte password fixtures exercise the shared input reader.
function Invoke-ReferenceSecurityRecovery {
  $vectors = Get-Content -LiteralPath "$fixtureDir/security-recovery-vectors.json" -Raw | ConvertFrom-Json
  foreach ($vector in $vectors) {
    $name = [IO.Path]::GetFileNameWithoutExtension($vector.file)
    $case = New-Case $name
    Copy-Item -LiteralPath "$fixtureDir/$($vector.file)" -Destination "$case/inputs/a.pdf"
    foreach ($text in @($vector.user, $vector.owner)) {
      Assert-SecurityJson $case $name @("--password=$text") @{ password = $text }
    }
    [IO.File]::WriteAllText("$case/inputs/password.txt", $vector.user + "`n", [Text.UTF8Encoding]::new($false))
    Assert-SecurityJson $case "$name/file" @('--password-file=inputs/password.txt') @{ passwordFile = 'inputs/password.txt' }
    $utf8 = [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($vector.user))
    Assert-SecurityJson $case "$name/hex" @('--password-mode=hex-bytes', "--password=$utf8") @{ passwordMode = 'hex-bytes'; password = $utf8 }
    $native = Invoke-Oracle $case @('inputs/a.pdf', '--show-npages', "--password=$($vector.user)", '--suppress-password-recovery')
    if ($native -ne 2) { throw "$name native suppression unexpectedly authenticated" }
    foreach ($route in @('argv', 'job')) {
      if ($route -eq 'argv') {
        $command = @('emit', '--workdir', $case, '--', 'inputs/a.pdf', '--show-npages', "--password=$($vector.user)", '--suppress-password-recovery')
      }
      else {
        @{ inputFile = 'inputs/a.pdf'; showNpages = ''; password = $vector.user; suppressPasswordRecovery = '' } |
          ConvertTo-Json | Set-Content -LiteralPath "$case/suppressed.json" -Encoding utf8NoBOM
        $command = @('emit-job', '--workdir', $case, '--job', "$case/suppressed.json")
      }
      $managed = Invoke-SecurityManaged $case $command
      if ($managed.Status -cne 'Failed' -or $managed.ExitCode -ne 2) { throw "$name/$route ignored password recovery suppression" }
    }
    if ($vector.name -eq 'macroman') {
      foreach ($route in @('argv', 'job')) {
        $outputCase = New-Case "$name-preserve-$route"
        Copy-Item -LiteralPath "$fixtureDir/$($vector.file)" -Destination "$outputCase/inputs/a.pdf"
        Invoke-SecurityPolicyCase $outputCase "$name-preserve-$route" $route @{
          inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf'; password = $vector.user
        } @("--password=$($vector.user)") $vector.user $vector.owner $vector.revision $(if ($vector.revision -eq 4) { 128 } else { 256 }) -4 $true
      }
    }
    Write-Output "[$name] argv/job/text/file/hex recovery and suppression agree with native"
  }
  Invoke-ReferenceReverseSecurityRecovery
}

function Invoke-ReferenceReverseSecurityRecovery {
  $vectors = Get-Content -LiteralPath "$fixtureDir/security-reverse-vectors.json" -Raw | ConvertFrom-Json
  foreach ($vector in $vectors) {
    $revision = if ($vector.file.Contains('-r4-')) { 'r4' } else { 'r6' }
    $name = "security-reverse-$revision-$($vector.name)"
    $case = New-Case $name
    Copy-Item -LiteralPath "$fixtureDir/$($vector.file)" -Destination "$case/inputs/a.pdf"
    $options = @('--password-mode=hex-bytes', "--password=$($vector.suppliedHex)")
    $native = Invoke-Oracle $case (@('inputs/a.pdf', '--requires-password') + $options)
    $expected = if ($vector.nativeMatched) { 3 } else { 0 }
    if ($native -ne $expected) { throw "$name native recovery outcome changed" }
    # WinAnsi 0x98 is U+02DC; pinned qpdf maps it to U+0303 on reverse recovery.
    # Compare the managed correction against the native exact UTF-8 credential.
    $nativeOptions = if ($vector.nativeMatched) { @() } else {
      @('--password-mode=hex-bytes', "--password=$($vector.exactHex)", '--suppress-password-recovery')
    }
    Assert-SecurityJson $case $name $options @{ passwordMode = 'hex-bytes'; password = $vector.suppliedHex } $nativeOptions
    [IO.File]::WriteAllBytes("$case/inputs/password.bin", [byte[]](@([Convert]::FromHexString($vector.suppliedHex)) + @(10)))
    Assert-SecurityJson $case "$name/file" @('--password-file=inputs/password.bin') @{ passwordFile = 'inputs/password.bin' } $nativeOptions
    $native = Invoke-Oracle $case (@('inputs/a.pdf', '--requires-password', '--suppress-password-recovery') + $options)
    if ($native -ne 0) { throw "$name native suppression authenticated a converted credential" }
    foreach ($route in @('argv', 'job')) {
      if ($route -eq 'argv') {
        $command = @('emit', '--workdir', $case, '--', 'inputs/a.pdf', '--requires-password', '--suppress-password-recovery') + $options
      }
      else {
        @{ inputFile = 'inputs/a.pdf'; requiresPassword = ''; passwordMode = 'hex-bytes'; password = $vector.suppliedHex; suppressPasswordRecovery = '' } |
          ConvertTo-Json | Set-Content -LiteralPath "$case/suppressed.json" -Encoding utf8NoBOM
        $command = @('emit-job', '--workdir', $case, '--job', "$case/suppressed.json")
      }
      $managed = Invoke-SecurityManaged $case $command
      if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0) { throw "$name/$route suppression differs" }
    }
    Write-Output "[$name] recovered facts and suppression pass; native recovery matched=$($vector.nativeMatched)"
  }
  foreach ($hex in @('FEFFD8010152', 'FEFF0152FF')) {
    $case = New-Case "security-reverse-malformed-$hex"
    Copy-Item -LiteralPath "$fixtureDir/security-recovery-r6-ligatures.pdf" -Destination "$case/inputs/a.pdf"
    $options = @('inputs/a.pdf', '--password-mode=hex-bytes', "--password=$hex", '--requires-password')
    $native = Invoke-Oracle $case $options
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--') + $options)
    if ($native -ne 3 -or $managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0) {
      throw 'Malformed UTF-16 control changed: native drops bytes; managed refuses recovery'
    }
    Write-Output '[security-reverse/malformed] strict managed UTF-16 and native byte-discarding difference verified'
  }
}
