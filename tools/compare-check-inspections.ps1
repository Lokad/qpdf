<# Explicit development lane for checker/inspection combinations. #>
param(
  [Parameter(Mandatory = $true)] [string] $QpdfExe,
  [Parameter(Mandatory = $true)] [string] $ProbeDll,
  [Parameter(Mandatory = $true)] [string] $WorkRoot,
  [Parameter(Mandatory = $true)] [string] $FixtureDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$FixtureDirectory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {

function New-AuthoredPdf([bool] $Attachment) {
  $catalog = if ($Attachment) { '<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(payload) 5 0 R] >> >> >>' } else { '<< /Type /Catalog /Pages 2 0 R >>' }
  $data = if ($Attachment) { [byte[]] @() } else { [Text.Encoding]::ASCII.GetBytes('invalid-flate') }
  $streamDict = if ($Attachment) { '<< /Length 0 >>' } else { "<< /Length $($data.Length) /Filter /FlateDecode >>" }
  $objects = @(
    @{ Dict=$catalog }, @{ Dict='<< /Type /Pages /Kids [3 0 R] /Count 1 >>' },
    @{ Dict='<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>' },
    @{ Dict=$streamDict; Data=$data }
  )
  if ($Attachment) {
    $objects += @{ Dict='<< /Type /Filespec /F (p.bin) /EF << /F 6 0 R >> >>' }
    $objects += @{ Dict='<< /Type /EmbeddedFile /Length 3 >>'; Data=[byte[]] @(0,127,255) }
  }
  $bytes = [IO.MemoryStream]::new()
  try {
    $write = { param([string] $s) $bytes.Write([Text.Encoding]::ASCII.GetBytes($s)) }
    & $write "%PDF-1.4`n"
    $offsets = [Collections.Generic.List[long]]::new()
    for ($i = 0; $i -lt $objects.Count; $i++) {
      $offsets.Add($bytes.Position)
      & $write "$($i+1) 0 obj`n$($objects[$i].Dict)`n"
      if ($objects[$i].ContainsKey('Data')) {
        & $write "stream`n"
        if ($objects[$i].Data.Length -gt 0) { $bytes.Write([byte[]] $objects[$i].Data, 0, $objects[$i].Data.Length) }
        & $write "`nendstream`n"
      }
      & $write "endobj`n"
    }
    $xref = $bytes.Position
    & $write "xref`n0 $($objects.Count+1)`n0000000000 65535 f `n"
    foreach ($offset in $offsets) { & $write ($offset.ToString('D10',[Globalization.CultureInfo]::InvariantCulture) + " 00000 n `n") }
    & $write "trailer`n<< /Root 1 0 R /Size $($objects.Count+1) >>`nstartxref`n$xref`n%%EOF`n"
    return ,$bytes.ToArray()
  } finally { $bytes.Dispose() }
}

$cases = @(
  @{ Name='all'; Fields=@{showNpages='';showPages='';showXref='';showObject='1';listAttachments='';showLinearization=''}; Flags=@('--show-npages','--show-pages','--show-xref','--show-object=1','--list-attachments','--show-linearization'); Minimum=16; Line='page 2: 4 0 R' },
  @{ Name='declared'; Fields=@{showNpages='';showPages=''}; Flags=@('--show-npages','--show-pages'); Mutation='count'; Minimum=7; Line='9' },
  @{ Name='recovery'; Fields=@{showNpages='';showPages='';showObject='1';showXref=''}; Flags=@('--show-npages','--show-pages','--show-object=1','--show-xref'); Mutation='xref'; Exit=3; Minimum=15; Line='page 2: 4 0 R' },
  @{ Name='warning-zero'; Fields=@{showPages='';showObject='1';warningExit0=''}; Flags=@('--show-pages','--show-object=1','--warning-exit-0'); Mutation='xref'; Minimum=7; Line='page 2: 4 0 R' },
  @{ Name='no-warn'; Fields=@{showPages='';showObject='1';noWarn=''}; Flags=@('--show-pages','--show-object=1','--no-warn'); Mutation='xref'; Exit=3; Minimum=7; Line='page 2: 4 0 R' },
  @{ Name='fatal-stream'; Fields=@{showNpages='';showPages='';showXref='';showObject='1';listAttachments=''}; Flags=@('--show-npages','--show-pages','--show-xref','--show-object=1','--list-attachments'); Authored='fatal'; Exit=2 },
  @{ Name='images'; Fields=@{showPages='';withImages=''}; Flags=@('--show-pages','--with-images'); Fixture='images-1page.pdf'; Minimum=5; Line='    /Im0: 5 0 R, 2 x 2' },
  @{ Name='attachments'; Fields=@{listAttachments=''}; Flags=@('--list-attachments'); Authored='attachment'; Minimum=1; Line='payload -> 6,0' },
  @{ Name='qdf'; Fields=@{qdf='';showObject='3';showLinearization=''}; Flags=@('--qdf','--show-object=3','--show-linearization'); Minimum=2; Line='inputs/a.pdf is not linearized' },
  @{ Name='linearize'; Fields=@{linearize='';showObject='3';showLinearization=''}; Flags=@('--linearize','--show-object=3','--show-linearization'); Minimum=2; Line='inputs/a.pdf is not linearized' },
  @{ Name='trailer'; Fields=@{showObject='trailer'}; Flags=@('--show-object=trailer'); Minimum=1; Line='<< /Root 1 0 R /Size 8 >>' },
  @{ Name='absent'; Fields=@{showObject='999'}; Flags=@('--show-object=999'); Minimum=1; Line='null' },
  @{ Name='stale-generation'; Fields=@{showObject='3,1'}; Flags=@('--show-object=3,1'); Minimum=1; Line='null' },
  @{ Name='output'; Fields=@{outputFile='outputs/prior.pdf';showPages=''}; Flags=@('--show-pages','outputs/prior.pdf'); Exit=2 },
  @{ Name='json'; Fields=@{json='2'}; Flags=@('--json=2'); Exit=2 },
  @{ Name='disabled-selector'; Fields=@{showObject='x';showNpages=''}; Flags=@('--show-object=x','--show-npages'); Minimum=1; Line='2' },
  @{ Name='prefix-selector'; Fields=@{showObject='1junk'}; Flags=@('--show-object=1junk'); Minimum=1; Line='<< /Pages 2 0 R /Type /Catalog >>' },
  @{ Name='empty-selector'; Fields=@{showObject='';showNpages=''}; Flags=@('--show-object=','--show-npages'); Minimum=1; Line='2' },
  @{ Name='generation-carry'; Fields=@{showObject='1'}; Flags=@('--show-object=1'); InitialSelector='3,1'; Minimum=1; Line='null' },
  @{ Name='overflow-selector'; Fields=@{showObject='999999999999999999999999999'}; Flags=@('--show-object=999999999999999999999999999'); Exit=2 }
)
$records = foreach ($item in $cases) {
  foreach ($route in @('job','argv','mixed')) {
    $name = "check-inspection-$($item.Name)-$route"
    $case = Join-Path $WorkRoot $name
    if (Test-Path -LiteralPath $case) { throw "Existing case output: $case" }
    New-Item -ItemType Directory -Path "$case/inputs","$case/outputs" -Force | Out-Null
    $fixture = if ($item.Fixture) { $item.Fixture } else { 'minimal-2page.pdf' }
    $pdf = if ($item.Authored) { New-AuthoredPdf ($item.Authored -eq 'attachment') } else { [IO.File]::ReadAllBytes((Join-Path $FixtureDirectory $fixture)) }
    if ($item.Mutation) {
      $text = [Text.Encoding]::ASCII.GetString($pdf)
      if ($item.Mutation -eq 'count') { $text = $text.Replace('/Count 2','/Count 9') }
      else { $text = [regex]::Replace($text,'(?<=startxref\n)\d+','999999999') }
      $pdf = [Text.Encoding]::ASCII.GetBytes($text)
    }
    [IO.File]::WriteAllBytes("$case/inputs/a.pdf",[byte[]] $pdf)
    [IO.File]::WriteAllBytes("$case/outputs/prior.pdf",[Text.Encoding]::ASCII.GetBytes('prior'))
    $before = (Get-FileHash "$case/outputs/prior.pdf").Hash
    $job = @{ inputFile='inputs/a.pdf';check='' }
    foreach ($key in $item.Fields.Keys) { $job[$key] = $item.Fields[$key] }
    if ($item.InitialSelector) {
      $job.jobJsonFile = 'inputs/first.json'
      [IO.File]::WriteAllText("$case/inputs/first.json",(@{showObject=$item.InitialSelector} | ConvertTo-Json -Compress),$utf8)
    }
    $json = $job | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText("$case/inputs/job.json",$json,$utf8)
    [IO.File]::WriteAllText("$case/inputs/settings.json",($item.Fields | ConvertTo-Json -Depth 10 -Compress),$utf8)
    [string[]] $commandArgs = @(if ($route -eq 'job') { '--job-json-file=inputs/job.json' } elseif ($route -eq 'argv') { @('inputs/a.pdf','--check') + $item.Flags } else { @('inputs/a.pdf','--check','--job-json-file=inputs/settings.json') })
    if ($item.InitialSelector -and $route -ne 'job') {
      [string[]] $commandArgs = @('inputs/a.pdf','--check',('--show-object=' + $item.InitialSelector)) + $(if ($route -eq 'argv') { $item.Flags } else { @('--job-json-file=inputs/settings.json') })
    }
    $emit = @('emit','--workdir',$case)
    if ($route -eq 'job') { $emit = @('emit-job','--workdir',$case,'--job',"$case/inputs/job.json") } else { $emit += @('--') + $commandArgs }
    Push-Location $case
    try {
      $managed = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$emit) 2>&1 | ForEach-Object { $_.ToString() }); $managedExit = $LASTEXITCODE
      & $QpdfExe @commandArgs 1> "$case/native.txt" 2> "$case/native-error.txt"; $nativeExit = $LASTEXITCODE
    } finally { Pop-Location }
    [IO.File]::WriteAllText("$case/managed.txt",($managed -join "`n") + "`n",$utf8)
    $expected = if ($item.ContainsKey('Exit')) { $item.Exit } else { 0 }
    $status = if ($expected -eq 2) { 'Failed' } else { 'Completed' }
    $expectedProcess = if ($expected -eq 2) { 2 } else { 0 }
    if ($nativeExit -ne $expected -or $managedExit -ne $expectedProcess -or ($managed -join "`n") -notmatch "emit: status=$status exit=$expected\b") { throw "$name exits differ: native $nativeExit / managed process $managedExit; expected reported $expected and process $expectedProcess. See $case" }
    $native = @(Get-Content "$case/native.txt" | Where-Object { $_ -match '^(\d+$|inputs/a\.pdf is not linearized$|\d+/\d+: |<< .* >>$|null$|page \d+:|  (content|images):$|    |payload -> )' })
    $left = @($managed | ForEach-Object {
      if ($_ -match '^emit: diagnostic (PageCount|LinearizationReport|XrefListing|ObjectListing|PageListing|AttachmentListing) Info (.*)$') {
        $message = $Matches[2]
        if ($Matches[1] -eq 'PageCount') { $message = $message -replace '^inputs/a\.pdf: ','' }
        if ($message -notmatch 'has no embedded files$|^  (filename|description|mime type|creation date|modification date|checksum|size):') { $message }
      }
    })
    if (($left -join "`n") -cne ($native -join "`n")) { throw "$name semantic listings differ. See $case" }
    if ($expected -ne 2 -and ($left.Count -lt $item.Minimum -or $left -cnotcontains $item.Line)) { throw "$name missing actual inspection effect" }
    if ($expected -eq 2 -and $left.Count -ne 0) { throw "$name performed inspection after rejection/fatal checking" }
    if ($item.Authored -eq 'fatal' -and [IO.File]::ReadAllText("$case/native-error.txt") -match 'reconstruct|damaged|xref') { throw "$name fatal fixture also requires xref recovery" }
    if ((Get-FileHash "$case/outputs/prior.pdf").Hash -ne $before) { throw "$name changed prior output" }
    [pscustomobject]@{ Name=$name; Route=$route; NativeExit=$nativeExit; ManagedReportedExit=$expected; ManagedProcessExit=$managedExit; ListingLines=$left.Count; InputSHA256=(Get-FileHash "$case/inputs/a.pdf").Hash; PriorOutputPreserved=$true }
    Write-Host "[$name] exit $expected; original identities and ordered listings agree; prior output preserved"
  }
}
[IO.File]::WriteAllText((Join-Path $WorkRoot 'results.json'),($records | ConvertTo-Json -Depth 5) + "`n",$utf8)
Write-Host "Checker-inspection lane passed: $($records.Count) comparisons. Binary extraction and selection/transformation contexts remain open."

} finally { Complete-ReferenceProbe }
