function Invoke-ReferenceJsonCheck {
  $case = New-Case 'json-check'
  Copy-Item -LiteralPath (Join-Path $fixtureDir 'minimal-1page.pdf') -Destination "$case/inputs/source.pdf"
  if ((Invoke-Oracle $case @('inputs/source.pdf','--json=2','--json-key=pages','--json-key=qpdf',
      '--json-stream-data=inline','inputs/report.json')) -ne 0) { throw 'JSON check fixture export failed.' }
  $report = Get-Content -LiteralPath "$case/inputs/report.json" -Raw | ConvertFrom-Json -AsHashtable
  $content = $report.pages[0].contents[0]
  $selector = ($content -replace ' R$','') -replace ' ', ','
  $page = $report.pages[0].object
  $key = "obj:$content"
  $body = [Text.Encoding]::ASCII.GetBytes("q`n20 20 24 29 re`nf`nQ`n")
  $stream = @{ dict=@{ '/ReviewMarker'='u:patched-graph' }; data=[Convert]::ToBase64String($body) }
  $report.qpdf[1][$key] = @{ stream=$stream }
  @{ qpdf=$report.qpdf } | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath "$case/inputs/model.json"
  $patch = @{ qpdf=@(@{ jsonversion=2 },@{ $key=@{ stream=$stream } }) }
  $patch | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/inputs/repair.json"
  $stream.dict['/Filter'] = '/FlateDecode'
  $patch | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/inputs/damage.json"
  if ((Invoke-Oracle $case @('inputs/source.pdf','--update-from-json=inputs/damage.json',
      '--stream-data=preserve','inputs/damaged.pdf')) -ne 0) { throw 'JSON check damaged fixture creation failed.' }
  if ((Invoke-Oracle $case @('inputs/source.pdf','--encrypt','check-reader','check-owner','256','--',
      'inputs/protected.pdf')) -ne 0) { throw 'JSON check protected fixture creation failed.' }
  # Use the authenticated source's actual stream pair after the native rewrite.
  if ((Invoke-Oracle $case @('inputs/protected.pdf','--password=check-reader','--json=2','--json-key=pages',
      'inputs/protected-pages.json')) -ne 0) { throw 'JSON check protected page lookup failed.' }
  $protected = Get-Content -LiteralPath "$case/inputs/protected-pages.json" -Raw | ConvertFrom-Json
  $protectedContent = $protected.pages[0].contents[0]
  $protectedSelector = ($protectedContent -replace ' R$','') -replace ' ', ','
  $stream.dict.Remove('/Filter')
  @{ qpdf=@(@{ jsonversion=2 },@{ "obj:$protectedContent"=@{ stream=$stream } }) } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$case/inputs/protected-patch.json"

  $inputs = @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash)
  foreach ($row in @(
    @{ Name='standalone'; Args=@('inputs/model.json','--json-input',"--show-object=$selector",'--show-pages','--show-npages');
       Code=0; Page=$page; Content=$content; Standalone=$true },
    @{ Name='create'; Args=@('inputs/model.json','--json-input','--check',"--show-object=$selector",'--show-pages');
       Code=0; Page=$page; Content=$content },
    @{ Name='damage'; Args=@('inputs/source.pdf','--update-from-json=inputs/damage.json','--check',"--show-object=$selector",'--show-pages');
       Code=2; NativeFailure='inflate'; ManagedFailure="CheckFailed Error .*stream $($content.Split(' ')[0])" },
    @{ Name='repair'; Job=@{ inputFile='inputs/damaged.pdf'; updateFromJson='inputs/repair.json'; check='';
         showObject=$selector; showPages=''; showNpages='' };
       Code=0; Page=$page; Content=$content },
    @{ Name='protected'; Args=@('inputs/protected.pdf','--password=check-reader','--update-from-json=inputs/protected-patch.json',
         '--check','--show-encryption',"--show-object=$protectedSelector"); Code=0; Encrypted=$true },
    @{ Name='wrong-password'; Args=@('inputs/protected.pdf','--password=wrong','--update-from-json=inputs/protected-patch.json',
         '--check',"--show-object=$protectedSelector",'--show-pages');
       Code=2; NativeFailure='invalid password'; ManagedFailure='InvalidPassword Error' }
  )) {
    $tag = "json-check/$($row.Name)"
    if ($row.Job) {
      $job = "$case/repair-job.json"
      $row.Job | ConvertTo-Json | Set-Content -LiteralPath $job
      $nativeArgs = @("--job-json-file=$job")
      $managedArgs = @('emit-job','--workdir',$case,'--job',$job)
    } else {
      $nativeArgs = $row.Args
      $managedArgs = @('emit','--workdir',$case,'--') + $row.Args
    }
    $nativeOut = "$case/$($row.Name)-native.txt"; $nativeErr = "$case/$($row.Name)-native-error.txt"
    $managedOut = "$case/$($row.Name)-managed.txt"; $managedErr = "$case/$($row.Name)-managed-error.txt"
    $process = @{ WorkingDirectory=$case; Wait=$true; PassThru=$true }
    $native = Start-ReferenceProcess @process -FilePath $QpdfExe -ArgumentList $nativeArgs -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
    $managed = Start-ReferenceProcess @process -FilePath 'dotnet' -ArgumentList (@($probeDll) + $managedArgs) -RedirectStandardOutput $managedOut -RedirectStandardError $managedErr
    $left = [IO.File]::ReadAllText($managedOut)
    $right = [IO.File]::ReadAllText($nativeOut)
    if ($native.ExitCode -ne $row.Code -or $managed.ExitCode -ne $row.Code -or $left -match '(?m)^emit: bytes ') {
      throw "$tag checker exits or payload differ."
    }
    if ($row.Code -eq 0) {
      if ($left -notmatch 'ObjectListing Info .*patched-graph' -or $right -notmatch 'patched-graph') {
        throw "$tag did not list the bound object."
      }
      if ($row.Standalone) {
        if ($left -match 'CheckSkipped|CheckFailed' -or $right -match 'No syntax or stream encoding errors found' -or
            $left -notmatch 'PageCount Info inputs/model\.json: 1' -or $right -notmatch '(?m)^1\r?$') {
          throw "$tag performed a check or reported the wrong page count."
        }
      } elseif ($left -notmatch 'CheckSkipped Info .*cross-reference offsets' -or
          $right -notmatch 'No syntax or stream encoding errors found') {
        throw "$tag did not check the bound graph."
      }
      if ($row.Page -and ($left -notmatch "PageListing Info page 1: $($row.Page)" -or
          $right -notmatch "page 1: $($row.Page)" -or $left -notmatch "PageListing Info     $($row.Content)" -or
          $right -notmatch "    $($row.Content)")) { throw "$tag page and content pairs differ." }
      if ($row.Job -and ($left -notmatch 'PageCount Info inputs/damaged\.pdf: 1' -or
          $right -notmatch '(?m)^1\r?$')) { throw "$tag page count differs." }
      if ($row.Encrypted -and ($left -notmatch 'EncryptionReport Info R = 6' -or $right -notmatch 'R = 6')) {
        throw "$tag authenticated revision differs."
      }
    } elseif ([IO.File]::ReadAllText($nativeErr) -notmatch $row.NativeFailure -or $left -notmatch $row.ManagedFailure -or
        $left -match 'ObjectListing|PageListing' -or $right -match 'patched-graph|page 1:') {
      throw "$tag checker failure or suppression differs."
    }
    Write-Output "[$tag] bound graph inspection agrees (exit $($row.Code))."
  }
  if (@(Get-ChildItem -LiteralPath $case -Filter '*.pdf' -File).Count -ne 0 -or
      (Compare-Object $inputs @(Get-ChildItem -LiteralPath "$case/inputs" -File | Get-FileHash) -Property Path,Hash)) {
    throw 'JSON checking changed a source file or produced a physical PDF.'
  }
  Invoke-ReferenceJsonAttachmentInspection
}

function Invoke-ReferenceJsonAttachmentInspection {
  $case = New-Case 'json-attachment-inspection'
  Copy-Item -LiteralPath (Join-Path $fixtureDir 'minimal-1page.pdf') -Destination "$case/inputs/source.pdf"
  $payload = [byte[]]@(0,254,128,10,255,32,26,1,127)
  $expectedHex = [Convert]::ToHexString($payload)
  [IO.File]::WriteAllBytes("$case/inputs/payload.bin", $payload)
  if ((Invoke-Oracle $case @('inputs/source.pdf','--add-attachment','inputs/payload.bin','--key=payload','--',
      'inputs/attached.pdf')) -ne 0 -or
      (Invoke-Oracle $case @('inputs/attached.pdf','--json-output','inputs/attached.json')) -ne 0) {
    throw 'Bound attachment fixture creation failed.'
  }

  $process = @{ WorkingDirectory=$case; Wait=$true; PassThru=$true }
  $native = Start-ReferenceProcess @process -FilePath $QpdfExe -ArgumentList @('inputs/attached.json','--json-input','--list-attachments') `
    -RedirectStandardOutput "$case/native-list.txt" -RedirectStandardError "$case/native-list-error.txt"
  $managed = Start-ReferenceProcess @process -FilePath 'dotnet' -ArgumentList @($probeDll,'emit','--workdir',$case,'--',
      'inputs/attached.json','--json-input','--list-attachments') `
    -RedirectStandardOutput "$case/managed-list.txt" -RedirectStandardError "$case/managed-list-error.txt"
  $match = [regex]::Match([IO.File]::ReadAllText("$case/native-list.txt"), '(?m)^payload -> \d+,\d+')
  if ($native.ExitCode -ne 0 -or $managed.ExitCode -ne 0 -or -not $match.Success -or
      -not [IO.File]::ReadAllText("$case/managed-list.txt").Contains("AttachmentListing Info $($match.Value)")) {
    throw 'Bound attachment listing lost the stream identity.'
  }

  $job = @{ inputFile='inputs/attached.json'; jsonInput=''; showAttachment='payload' }
  $job | ConvertTo-Json | Set-Content -LiteralPath "$case/job.json"
  $native = Start-ReferenceProcess @process -FilePath $QpdfExe -ArgumentList @('--job-json-file=job.json') `
    -RedirectStandardOutput "$case/native.bin" -RedirectStandardError "$case/native-binary-error.txt"
  $managed = Start-ReferenceProcess @process -FilePath 'dotnet' -ArgumentList @($probeDll,'emit-job','--workdir',$case,'--job',"$case/job.json") `
    -RedirectStandardOutput "$case/managed-binary.txt" -RedirectStandardError "$case/managed-binary-error.txt"
  if ($native.ExitCode -ne 0 -or $managed.ExitCode -ne 0 -or
      [Convert]::ToHexString([IO.File]::ReadAllBytes("$case/native.bin")) -cne $expectedHex -or
      -not [IO.File]::ReadAllText("$case/managed-binary.txt").Contains("emit: bytes $expectedHex")) {
    throw 'Bound attachment extraction changed an opaque payload.'
  }
  Write-Output '[json-attachment-inspection] attachment listing pair and extracted bytes agree.'
}
