<# Explicit native byte/default/ordering lane; no runtime dependency on this tool. #>
param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll,
  [Parameter(Mandatory = $true)] [string] $WorkRoot
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
$payload = [byte[]] @(0,1,2,127,128,255,13,10,65)
$attachment = [byte[]] @(66,0,255,10)

function New-BytesPdf([string] $Mode, [bool] $Recovery) {
  [byte[]] $data = if ($Mode -eq 'empty') { @() } elseif ($Mode -eq 'content') { [Text.Encoding]::ASCII.GetBytes("q`rQ  `r`n  ") } else { $payload }
  $compressed = $Mode -eq 'flate'
  if ($compressed) {
    $encoded = [IO.MemoryStream]::new()
    $zlib = [IO.Compression.ZLibStream]::new($encoded,[IO.Compression.CompressionLevel]::Optimal,$true)
    try { $zlib.Write($data,0,$data.Length) } finally { $zlib.Dispose() }
    $data = $encoded.ToArray(); $encoded.Dispose()
  }
  $filter = if ($compressed) { ' /Filter /FlateDecode' } else { '' }
  $objects = @(
    @{D='<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(payload) 6 0 R] >> >> >>'},
    @{D='<< /Type /Pages /Kids [3 0 R] /Count 1 >>'},
    @{D='<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>'},
    @{D='<< /Length 0 >>';B=[byte[]]@()},
    @{D="<< /Length $($data.Length)$filter >>";B=$data},
    @{D='<< /Type /Filespec /F (p.bin) /EF << /F 7 0 R >> >>'},
    @{D='<< /Type /EmbeddedFile /Length 4 >>';B=$attachment}
  )
  if ($Mode -in @('fatal','unused-fatal')) {
    $objects += @{D='<< /Length 7 /Filter /FlateDecode >>';B=[Text.Encoding]::ASCII.GetBytes('invalid')}
    if ($Mode -eq 'fatal') { $objects[2].D = '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 8 0 R >>' }
  }
  $pdf = [IO.MemoryStream]::new()
  try {
    $write = { param([string] $s) $pdf.Write([Text.Encoding]::ASCII.GetBytes($s)) }
    & $write "%PDF-1.4`n"
    $offsets = [Collections.Generic.List[long]]::new()
    for ($i=0; $i -lt $objects.Count; $i++) {
      $offsets.Add($pdf.Position); & $write "$($i+1) 0 obj`n$($objects[$i].D)`n"
      if ($objects[$i].ContainsKey('B')) {
        & $write "stream`n"
        if ($objects[$i].B.Length -gt 0) { $pdf.Write([byte[]]$objects[$i].B,0,$objects[$i].B.Length) }
        & $write "`nendstream`n"
      }
      & $write "endobj`n"
    }
    $xref = if ($Recovery) { 999999999L } else { $pdf.Position }
    & $write "xref`n0 $($objects.Count+1)`n0000000000 65535 f `n"
    foreach ($offset in $offsets) { & $write ($offset.ToString('D10',[Globalization.CultureInfo]::InvariantCulture)+" 00000 n `n") }
    & $write "trailer`n<< /Root 1 0 R /Size $($objects.Count+1) >>`nstartxref`n$xref`n%%EOF`n"
    return ,$pdf.ToArray()
  } finally { $pdf.Dispose() }
}

$cases = @(
  @{N='raw';F=@{showObject='5';rawStreamData=''};Hex='0001027F80FF0D0A41'},
  @{N='filtered';F=@{showObject='5';filteredStreamData=''};Mode='flate';Hex='0001027F80FF0D0A41'},
  @{N='attachment';F=@{showAttachment='payload'};Hex='4200FF0A'},
  @{N='empty';F=@{showObject='5';rawStreamData=''};Mode='empty';Hex=''},
  @{N='check-attachment';F=@{check='';showAttachment='payload'};Hex='4200FF0A';Channel='stderr'},
  @{N='check-both';F=@{check='';showObject='5';rawStreamData='';showAttachment='payload'};Hex='0001027F80FF0D0A414200FF0A';Channel='stderr'},
  @{N='check-raw';F=@{check='';showObject='5';rawStreamData=''};Exit=2;Conflict=$true;Channel='stdout'},
  @{N='check-filtered';F=@{check='';showObject='5';filteredStreamData=''};Mode='flate';Exit=2;Conflict=$true;Channel='stdout'},
  @{N='check-both-flags';F=@{check='';showObject='5';rawStreamData='';filteredStreamData=''};Mode='flate';Exit=2;Conflict=$true;Channel='stdout'},
  @{N='decode-none';F=@{check='';showObject='5';filteredStreamData='';decodeLevel='none'};Mode='flate';Exit=2;Conflict=$true;Channel='stdout'},
  @{N='decode-all';F=@{check='';showObject='5';filteredStreamData='';decodeLevel='all'};Mode='flate';Exit=2;Conflict=$true;Channel='stdout'},
  @{N='nonstream';F=@{check='';showObject='3';rawStreamData=''};Line='/Contents 4 0 R';Channel='stdout'},
  @{N='absent';F=@{showObject='999';rawStreamData=''};Line='null'},
  @{N='stale';F=@{showObject='5,1';filteredStreamData=''};Line='null'},
  @{N='missing';F=@{check='';showAttachment='missing'};Exit=2;Missing=$true;Channel='stderr'},
  @{N='missing-after-raw';F=@{check='';showObject='5';rawStreamData='';showAttachment='missing'};Exit=2;Missing=$true;Channel='stderr';NativePrefix='0001027F80FF0D0A41'},
  @{N='output';F=@{check='';showObject='5';rawStreamData='';outputFile='outputs/prior.pdf'};Exit=2;BeforeCheck=$true},
  @{N='fatal';F=@{check='';showAttachment='payload'};Mode='fatal';Exit=2;Fatal=$true},
  @{N='unused-fatal';F=@{check='';showAttachment='payload'};Mode='unused-fatal';Hex='4200FF0A';Channel='stderr'},
  @{N='recovery';F=@{check='';showAttachment='payload'};Recovery=$true;Exit=3;Hex='4200FF0A';Channel='stderr'},
  @{N='warning-zero';F=@{check='';showAttachment='payload';warningExit0=''};Recovery=$true;Hex='4200FF0A';Channel='stderr'},
  @{N='no-warn';F=@{check='';showAttachment='payload';noWarn=''};Recovery=$true;Exit=3;Hex='4200FF0A';Channel='stderr'},
  @{N='unused-raw-check';F=@{check='';rawStreamData=''};Channel='stdout'},
  @{N='unused-filtered-check';F=@{check='';filteredStreamData=''};Channel='stdout'},
  @{N='unused-raw-attachment';F=@{check='';showAttachment='payload';rawStreamData=''};Hex='4200FF0A';Channel='stderr'},
  @{N='disabled-no-output';F=@{showObject='x';rawStreamData=''}},
  @{N='disabled-count';F=@{showObject='0';rawStreamData='';showNpages=''};Line='1'},
  @{N='disabled-write';F=@{showObject='0';rawStreamData='';outputFile='outputs/written.pdf'};Exit=2;BeforeCheck=$true},
  @{N='unused-raw-write';F=@{rawStreamData='';outputFile='outputs/written.pdf'};Rewrite=$true}
  @{N='normalize-default';F=@{showObject='5';filteredStreamData=''};Mode='content';Hex='710D5120200D0A2020'},
  @{N='normalize-yes';F=@{showObject='5';filteredStreamData='';normalizeContent='y'};Mode='content';Hex='710A5120200A2020'},
  @{N='normalize-no';F=@{showObject='5';filteredStreamData='';normalizeContent='n'};Mode='content';Hex='710D5120200D0A2020'},
  @{N='normalize-raw';F=@{showObject='5';rawStreamData='';normalizeContent='y'};Mode='content';Hex='710D5120200D0A2020'},
  @{N='normalize-qdf';F=@{showObject='5';filteredStreamData='';qdf=''};Mode='content';Hex='710A5120200A2020'},
  @{N='normalize-qdf-no';F=@{showObject='5';filteredStreamData='';qdf='';normalizeContent='n'};Mode='content';Hex='710D5120200D0A2020'},
  @{N='normalize-checked-attachment';F=@{check='';showObject='5';filteredStreamData='';normalizeContent='y';showAttachment='payload'};Mode='content';Hex='710A5120200A20204200FF0A';Channel='stderr'}
)
$names = @{check='check';showObject='show-object';rawStreamData='raw-stream-data';filteredStreamData='filtered-stream-data';showAttachment='show-attachment';decodeLevel='decode-level';warningExit0='warning-exit-0';noWarn='no-warn';showNpages='show-npages';normalizeContent='normalize-content';qdf='qdf'}
$records = foreach ($item in $cases) {
  foreach ($route in @('job','argv','mixed')) {
    $name = "binary-$($item.N)-$route"; $case = Join-Path $WorkRoot $name
    if (Test-Path -LiteralPath $case) { throw "Existing case: $case" }
    New-Item -ItemType Directory -Path "$case/inputs","$case/outputs" -Force | Out-Null
    $mode = if ($item.Mode) { $item.Mode } else { 'plain' }
    [IO.File]::WriteAllBytes("$case/inputs/a.pdf",(New-BytesPdf $mode ([bool]$item.Recovery)))
    [IO.File]::WriteAllText("$case/outputs/prior.pdf",'prior',$utf8)
    $prior = (Get-FileHash "$case/outputs/prior.pdf").Hash
    $job = @{inputFile='inputs/a.pdf'}
    foreach ($key in $item.F.Keys) { $job[$key]=$item.F[$key] }
    [IO.File]::WriteAllText("$case/inputs/job.json",($job|ConvertTo-Json -Compress),$utf8)
    [IO.File]::WriteAllText("$case/inputs/settings.json",($item.F|ConvertTo-Json -Compress),$utf8)
    [string[]]$argv = @('inputs/a.pdf')
    foreach ($key in $item.F.Keys | Sort-Object) {
      if ($key -eq 'outputFile') { continue }
      $value = $item.F[$key]; $flag = '--'+$names[$key]
      $argv += if ($key -in @('showObject','showAttachment','decodeLevel','normalizeContent')) { "$flag=$value" } else { $flag }
    }
    if ($item.F.ContainsKey('outputFile')) { $argv += $item.F.outputFile }
    [string[]]$nativeArgs = if ($route -eq 'job') { @('--job-json-file=inputs/job.json') } elseif ($route -eq 'mixed') { @('inputs/a.pdf','--job-json-file=inputs/settings.json') } else { $argv }
    $emit = if ($route -eq 'job') { @('emit-job','--workdir',$case,'--job',"$case/inputs/job.json") } else { @('emit','--workdir',$case) }
    if ($item.Rewrite) { $emit += @('--out',"outputs/written.pdf=$case/outputs/managed-written.pdf") }
    if ($route -ne 'job') { $emit += @('--')+$nativeArgs }
    Push-Location $case
    try {
      & $QpdfExe @nativeArgs 1> "$case/native.bin" 2> "$case/native-error.txt"; $nativeExit=$LASTEXITCODE
      $managed = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$emit) 2>&1 | ForEach-Object {$_.ToString()}); $managedExit=$LASTEXITCODE
    } finally { Pop-Location }
    [IO.File]::WriteAllText("$case/managed.txt",($managed -join "`n")+"`n",$utf8)
    $expected = if ($item.ContainsKey('Exit')) { $item.Exit } else { 0 }
    $expectedProcess = if ($expected -eq 2) { 2 } else { 0 }
    if ($nativeExit -ne $expected -or $managedExit -ne $expectedProcess -or ($managed -join "`n") -notmatch "emit: status=(Completed|Failed) exit=$expected\b") { throw "$name exits differ: native $nativeExit / managed $managedExit. See $case" }
    $stdout = [IO.File]::ReadAllBytes("$case/native.bin"); $stderr = [IO.File]::ReadAllText("$case/native-error.txt")
    $text = [Text.Encoding]::ASCII.GetString($stdout)
    $hexLines = @($managed | Where-Object {$_ -match '^emit: bytes '})
    if ($item.ContainsKey('Hex')) {
      if ([Convert]::ToHexString($stdout) -cne $item.Hex -or $hexLines.Count -ne 1 -or $hexLines[0].Substring(12) -cne $item.Hex) { throw "$name actual byte payload differs. See $case" }
    } elseif ($hexLines.Count -ne 0) { throw "$name returned binary data unexpectedly" }
    if ($item.Channel) {
      $info = if ($item.Channel -eq 'stdout') {$text} else {$stderr}
      if ($item.Recovery) {
        if ($info -notmatch 'checking inputs/a.pdf' -or $info -notmatch 'File is not linearized') { throw "$name checking did not run on the expected channel" }
      } elseif ($info -notmatch 'errors that qpdf cannot detect') { throw "$name checking did not complete on the expected channel" }
    }
    if ($item.Conflict -and ($stderr -notmatch 'QPDFLogger: called setSave' -or ($managed -join "`n") -notmatch 'Binary stream output cannot follow informational output')) { throw "$name did not fail after checking" }
    if ($item.Missing -and ($stderr -notmatch 'attachment missing not found' -or ($managed -join "`n") -notmatch 'requested attachment was not found')) { throw "$name did not reach attachment lookup" }
    if ($item.NativePrefix -and [Convert]::ToHexString($stdout) -cne $item.NativePrefix) { throw "$name native partial-output prefix differs" }
    if ($item.BeforeCheck -and ($text+$stderr) -match 'checking inputs/a.pdf') { throw "$name opened/checked before rejecting its configuration" }
    if ($item.Fatal -and (($managed -join "`n") -notmatch 'diagnostic CheckFailed Error' -or $stderr -match 'reconstruct|damaged|xref')) { throw "$name fatal checking proof differs" }
    if ($item.Line -and ($text -notmatch [regex]::Escape($item.Line) -or ($managed -join "`n") -notmatch [regex]::Escape($item.Line))) { throw "$name actual object/count inspection differs" }
    if ($item.Rewrite) {
      foreach ($file in @('written.pdf','managed-written.pdf')) {
        & $QpdfExe --check "$case/outputs/$file" *> "$case/$file-check.txt"
        if ($LASTEXITCODE -ne 0) { throw "$name rewritten output is invalid" }
        $count = Invoke-ReferenceProbe -Arguments (@($ProbeDll)+@('compare')+@('--qpdf-exe')+@($QpdfExe)+@('--input')+@("$case/outputs/$file")+@('--expected-npages')+@(1)) 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$name independent rewritten-output validation failed: $count" }
      }
    }
    if ((Get-FileHash "$case/outputs/prior.pdf").Hash -cne $prior) { throw "$name changed prior target" }
    [pscustomobject]@{Name=$name;Route=$route;NativeExit=$nativeExit;ManagedReportedExit=$expected;ManagedProcessExit=$managedExit;PayloadHex=$item.Hex;CheckChannel=$item.Channel;NativePrefix=$item.NativePrefix;StagedFailureDifference=[bool]$item.NativePrefix;RewriteValidated=[bool]$item.Rewrite;PriorPreserved=$true;InputSHA256=(Get-FileHash "$case/inputs/a.pdf").Hash}
    Write-Host "[$name] byte/default/ordering verdict passed"
  }
}
[IO.File]::WriteAllText((Join-Path $WorkRoot 'results.json'),($records|ConvertTo-Json -Depth 5)+"`n",$utf8)
Write-Host "Binary-inspection lane passed: $($records.Count) comparisons; $(@($records|Where-Object RewriteValidated).Count) independently checked rewrites. Failed/canceled caller payloads remain staged; native streaming prefixes are an explicit host difference."

} finally { Complete-ReferenceProbe }
