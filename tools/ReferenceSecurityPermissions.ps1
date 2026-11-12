# Permission words and setter order through both public command adapters.
function Invoke-SecurityPermissionCase {
  param([string] $Name, [string] $Route, [string[]] $Options, [System.Collections.IDictionary] $Permissions,
    [int] $Expected, [bool] $DecryptLast, [bool] $MixedJob)
  $case = New-Case $Name
  Copy-Item -LiteralPath "$fixtureDir/meta-1page.pdf" -Destination "$case/inputs/a.pdf"
  $encrypt = @('--encrypt', 'reader', 'owner', '256') + $Options + @('--')
  if ($Route -eq 'argv') {
    if ($DecryptLast) { $encrypt += '--decrypt' }
    elseif ($MixedJob) { $encrypt = @('--decrypt') + $encrypt }
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf', 'outputs/a.pdf') + $encrypt)
    $native = Invoke-Oracle $case (@('inputs/a.pdf', "$case/ora.pdf") + $encrypt)
  }
  else {
    $job = [ordered]@{ inputFile = 'inputs/a.pdf'; outputFile = 'outputs/a.pdf';
      encrypt = @{ userPassword = 'reader'; ownerPassword = 'owner'; '256bit' = $Permissions } }
    # Native applies lexical keys: decrypt precedes encrypt; form precedes modify.
    if ($MixedJob) { $job.decrypt = '' }
    $job | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$case/job.json" -Encoding utf8NoBOM
    $managed = Invoke-SecurityManaged $case @('emit-job', '--workdir', $case, '--job', "$case/job.json", '--out', "outputs/a.pdf=$case/lib.pdf")
    $job.outputFile = "$case/ora.pdf"
    $job | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$case/native-job.json" -Encoding utf8NoBOM
    $native = Invoke-Oracle $case @('--job-json-file=native-job.json')
  }
  if ($managed.Status -cne 'Completed' -or $managed.ExitCode -ne 0 -or $native -ne 0) { throw "$Name permission command failed" }
  $revision = if ($DecryptLast) { 0 } else { 6 }
  $bits = if ($DecryptLast) { 0 } else { 256 }
  Assert-SecurityPair $case $Name 'reader' 'owner' $revision $bits $Expected $true
  Write-Output "[$Name] permission word $Expected and authenticated output agree"
}

function Invoke-ReferenceSecurityPermissions {
  foreach ($choice in @(
    @('modify', 'all', -4), @('modify', 'annotate', -12), @('modify', 'form', -44),
    @('modify', 'assembly', -300), @('modify', 'none', -1324),
    @('print', 'none', -2056), @('print', 'low', -2052), @('print', 'full', -4),
    @('accessibility', 'n', -4), @('accessibility', 'y', -4),
    @('extract', 'n', -20), @('extract', 'y', -4), @('annotate', 'n', -36), @('annotate', 'y', -4),
    @('assemble', 'n', -1028), @('assemble', 'y', -4), @('form', 'n', -260), @('form', 'y', -4),
    @('modify-other', 'n', -12), @('modify-other', 'y', -4)
  )) {
    $key = if ($choice[0] -eq 'modify-other') { 'modifyOther' } else { $choice[0] }
    foreach ($route in @('argv', 'job')) {
      Invoke-SecurityPermissionCase "security-permission-$($choice[0])-$($choice[1])-$route" $route @("--$($choice[0])=$($choice[1])") @{ $key = $choice[1] } $choice[2] $false $false
    }
  }
  foreach ($order in @(
    @('--modify=none', '--form=y', -1068), @('--form=y', '--modify=none', -1324),
    @('--print=none', '--print=low', -2052)
  )) {
    Invoke-SecurityPermissionCase "security-permission-order-$($order[2])" 'argv' @($order[0], $order[1]) @{} $order[2] $false $false
  }
  Invoke-SecurityPermissionCase 'security-policy-last-decrypt' 'argv' @() @{} 0 $true $false
  Invoke-SecurityPermissionCase 'security-policy-last-encrypt' 'argv' @() @{} -4 $false $true
  foreach ($reverse in @($false, $true)) {
    $permissions = if ($reverse) { [ordered]@{ modifyOther = 'y'; form = 'y'; modify = 'none' } }
      else { [ordered]@{ modify = 'none'; form = 'y'; modifyOther = 'y' } }
    Invoke-SecurityPermissionCase "security-permission-lexical-$reverse" 'job' @() $permissions -1316 $false $true
  }
}
