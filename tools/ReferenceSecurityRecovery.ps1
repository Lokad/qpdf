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
}
