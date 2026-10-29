param(
    [Parameter(Mandatory = $true)] [string] $ProbeDll,
    [Parameter(Mandatory = $true)] [string] $WorkRoot,
    [Parameter(Mandatory = $true)] [string] $OracleFixture
)
$ErrorActionPreference = 'Stop'
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$OracleFixture = (Resolve-Path -LiteralPath $OracleFixture).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $WorkRoot) { throw 'Probe test workspace already exists.' }
New-Item -ItemType Directory -Path (Join-Path $WorkRoot 'inputs') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../tests/Fixtures/minimal-1page.pdf') -Destination (Join-Path $WorkRoot 'inputs/a.pdf')
$unicodeInput="inputs/$([char]0xC9)$([char]0x65E5)$([char]0x672C).pdf"
Copy-Item -LiteralPath (Join-Path $WorkRoot 'inputs/a.pdf') -Destination (Join-Path $WorkRoot $unicodeInput)
[IO.File]::WriteAllText((Join-Path $WorkRoot 'job.json'), '{"inputFile":"./inputs/a.pdf","outputFile":"outputs/a.pdf"}')

function Invoke-ColdProbe {
    param([string[]] $Arguments, [string] $InputText)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $WorkRoot
    $start.ArgumentList.Add($ProbeDll)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Cold probe did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText); $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); $null=$process.WaitForExit(30000); throw 'Cold probe timed out.' }
        return @{ExitCode=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}

# Vary only the header terminator; calculate xref offsets for each actual layout.
foreach ($ending in @(@{Name='lf'; Text="`n"}, @{Name='crlf'; Text="`r`n"}, @{Name='cr'; Text="`r"})) {
    $pdf = [Text.StringBuilder]::new('%PDF-1.4' + $ending.Text)
    $offsets = [Collections.Generic.List[int]]::new()
    foreach ($body in @('<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Count 1 /Kids [3 0 R] >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>')) {
        $offsets.Add($pdf.Length)
        $null = $pdf.Append(($offsets.Count.ToString([Globalization.CultureInfo]::InvariantCulture)) + " 0 obj`n" + $body + "`nendobj`n")
    }
    $xref = $pdf.Length
    $null = $pdf.Append("xref`n0 4`n0000000000 65535 f `n")
    foreach ($offset in $offsets) { $null = $pdf.Append($offset.ToString('D10', [Globalization.CultureInfo]::InvariantCulture) + " 00000 n `n") }
    $null = $pdf.Append("trailer`n<< /Size 4 /Root 1 0 R >>`nstartxref`n" + $xref.ToString([Globalization.CultureInfo]::InvariantCulture) + "`n%%EOF`n")
    $file = Join-Path $WorkRoot ('header-' + $ending.Name + '.pdf')
    [IO.File]::WriteAllBytes($file, [Text.Encoding]::ASCII.GetBytes($pdf.ToString()))
    foreach ($arguments in @(@('validate','--input',$file), @('compare','--qpdf-exe',$OracleFixture,'--input',$file,'--expected-npages','1'))) {
        $headerResult = Invoke-ColdProbe -Arguments $arguments -InputText ''
        if ($headerResult.ExitCode -ne 0) { throw "Header $($ending.Name) failed $($arguments[0]): $($headerResult.Error)" }
        if ($arguments[0] -eq 'validate' -and $headerResult.Output -notmatch 'independent: header-version=1\.4\r?\n') { throw 'Header version reporting changed.' }
    }
}
foreach ($invalid in @(
    @{Name='short'; Text='%PDF'; Message='too short'},
    @{Name='marker'; Text="not-a-pdf`n"; Message='does not start with %PDF-'},
    @{Name='version'; Text="%PDF-`r1 0 obj`n"; Message='Unrecognized PDF header version'},
    @{Name='truncated'; Text="%PDF-1.4`r"; Message='Independent reader'}
)) {
    $file = Join-Path $WorkRoot ('header-' + $invalid.Name + '.pdf')
    [IO.File]::WriteAllBytes($file, [Text.Encoding]::ASCII.GetBytes($invalid.Text))
    $headerResult = Invoke-ColdProbe -Arguments @('validate','--input',$file) -InputText ''
    if ($headerResult.ExitCode -ne 1 -or $headerResult.Error -notmatch [regex]::Escape($invalid.Message)) { throw "Malformed header control failed: $($invalid.Name)" }
}
Write-Output 'Header controls passed: LF/CRLF/CR validate and compare, plus four malformed/truncated inputs.'

. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
    $cases = @(
        @('emit','--workdir',$WorkRoot,'--','inputs/a.pdf','--show-npages'),
        @('emit','--workdir',$WorkRoot,'--',$unicodeInput,'--show-npages'),
        @('emit','--workdir',$WorkRoot,'--','./inputs/a.pdf','--show-object=1'),
        @('emit','--workdir',$WorkRoot,'--','inputs/missing.pdf','--show-npages'),
        @('emit','--workdir',$WorkRoot,'--','inputs/a.pdf','--bogus-flag'),
        @('emit','--workdir',$WorkRoot,'--','inputs/a.pdf','--show-npages'),
        @('validate','--input',(Join-Path $PSScriptRoot '../tests/Fixtures/truncated-1page.pdf')),
        @('--help')
    )
    foreach ($arguments in $cases) {
        $cold = Invoke-ColdProbe -Arguments $arguments -InputText ''
        $warm = Invoke-ReferenceProbeRequest -Arguments $arguments -WorkingDirectory $WorkRoot
        $warmOutput = [IO.File]::ReadAllText($warm.StandardOutput)
        [IO.File]::WriteAllText($warm.StandardOutput+'.cold',$cold.Output)
        [IO.File]::WriteAllText($warm.StandardError+'.cold',$cold.Error)
        if ($cold.ExitCode -ne $warm.ExitCode -or $cold.Output -cne $warmOutput -or
            $cold.Error -cne [IO.File]::ReadAllText($warm.StandardError)) { throw "Cold/batch result differs: $($arguments -join ' ')" }
    }
    $coldPdf = Join-Path $WorkRoot 'cold.pdf'; $warmPdf = Join-Path $WorkRoot 'warm.pdf'
    $cold = Invoke-ColdProbe -Arguments @('emit-job','--workdir',$WorkRoot,'--job','job.json','--out',"outputs/a.pdf=$coldPdf") -InputText ''
    $warm = Invoke-ReferenceProbeRequest -Arguments @('emit-job','--workdir',$WorkRoot,'--job','job.json','--out',"outputs/a.pdf=$warmPdf") -WorkingDirectory $WorkRoot
    if ($cold.ExitCode -ne 0 -or $warm.ExitCode -ne 0 -or
        (Get-FileHash $coldPdf).Hash -cne (Get-FileHash $warmPdf).Hash) { throw 'Relative job/alias rewrite differs.' }
    $coldLabels=Join-Path $WorkRoot 'cold-labels.pdf'; $warmLabels=Join-Path $WorkRoot 'warm-labels.pdf'
    $label="1:D/1/$([char]0xC9)$([char]0x65E5)$([char]0x672C)"
    $tail=@('--','inputs/a.pdf','--set-page-labels',$label,'--','outputs/labels.pdf')
    $cold=Invoke-ColdProbe -Arguments (@('emit','--workdir',$WorkRoot,'--out',"outputs/labels.pdf=$coldLabels")+$tail) -InputText ''
    $warm=Invoke-ReferenceProbeRequest -Arguments (@('emit','--workdir',$WorkRoot,'--out',"outputs/labels.pdf=$warmLabels")+$tail) -WorkingDirectory $WorkRoot
    if ($cold.ExitCode -ne 0 -or $warm.ExitCode -ne 0 -or (Get-FileHash $coldLabels).Hash -cne (Get-FileHash $warmLabels).Hash) { throw 'Unicode label bytes differ.' }
    $validation=Invoke-ReferenceProbeRequest -Arguments @('validate','--input',$warmPdf) -WorkingDirectory $WorkRoot
    if ($validation.ExitCode -ne 0 -or [IO.File]::ReadAllText($validation.StandardOutput) -notmatch 'independent: npages=1') { throw 'Fresh rewrite does not validate.' }
    $absent=Join-Path $WorkRoot 'absent.pdf'
    $warm=Invoke-ReferenceProbeRequest -Arguments @('emit','--workdir',$WorkRoot,'--out',"outputs/a.pdf=$absent",'--','inputs/a.pdf','--show-npages') -WorkingDirectory $WorkRoot
    if ($warm.ExitCode -ne 2 -or (Test-Path $absent)) { throw 'A completed host leaked outputs into the next request.' }
    $locked=Join-Path $WorkRoot 'inputs/unrelated.pdf'
    $lock=[IO.File]::Open($locked,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $warm=Invoke-ReferenceProbeRequest -Arguments $cases[0] -WorkingDirectory $WorkRoot
        if ($warm.ExitCode -ne 0) { throw 'Probe opened an unrelated input.' }
    } finally { $lock.Dispose() }
    foreach ($i in 0..69) {
        $arguments = if (($i % 3) -eq 1) { $cases[3] } else { $cases[0] }
        $expected = if (($i % 3) -eq 1) { 2 } else { 0 }
        $warm=Invoke-ReferenceProbeRequest -Arguments $arguments -WorkingDirectory $WorkRoot
        if ($warm.ExitCode -ne $expected) { throw "Worker rollover/state isolation failed at $i." }
    }
    foreach ($index in @(0,3)) {
        $expected = if ($index -eq 0) { 0 } else { 2 }
        $lines = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$cases[$index]) 2>&1)
        if ($LASTEXITCODE -ne $expected -or ($lines -join "`n") -notmatch "emit: status=\w+ exit=$expected") { throw 'Pipeline transport changed output or native exit status.' }
        $output=Join-Path $WorkRoot "redirect-$index.txt"; $redirectError=Join-Path $WorkRoot "redirect-$index-error.txt"
        $process=Start-ReferenceProcess -FilePath dotnet -ArgumentList (@($ProbeDll)+$cases[$index]) -WorkingDirectory $WorkRoot -Wait -PassThru -NoNewWindow -RedirectStandardOutput $output -RedirectStandardError $redirectError
        if ($process.ExitCode -ne $expected -or [IO.File]::ReadAllText($output) -notmatch "emit: status=\w+ exit=$expected" -or -not (Test-Path $redirectError)) { throw 'Process/redirect transport changed output or exit status.' }
    }
} finally { Complete-ReferenceProbe }
$transport=Get-Content (Join-Path $WorkRoot 'managed-calls/results.json') -Raw | ConvertFrom-Json
if ($transport.Workers -ne 2 -or $transport.Requests.Count -ne 87) { throw 'Bounded worker/request coverage changed.' }
$bad=Invoke-ColdProbe -Arguments @('batch') -InputText "{bad}`n"
if ($bad.ExitCode -ne 1 -or $bad.Error -notmatch 'Invalid batch JSON') { throw 'Malformed protocol did not fail explicitly.' }
$large=Invoke-ColdProbe -Arguments @('batch') -InputText ((' ' * (4*1024*1024+1))+"`n")
if ($large.ExitCode -ne 1 -or $large.Error -notmatch 'four MiB') { throw 'Protocol request bound did not reject.' }
# Bounded raster controls: meaningful content, expected crop, blank reference and
# malformed payload. The first pixel byte is whitespace and must not be skipped.
$rasterHeader = [Text.Encoding]::ASCII.GetBytes("P6`n1 1`n255`n")
$ink = Join-Path $WorkRoot 'ink.ppm'; $blank = Join-Path $WorkRoot 'blank.ppm'; $broken = Join-Path $WorkRoot 'broken.ppm'
[IO.File]::WriteAllBytes($ink, [byte[]]($rasterHeader + [byte[]]@(10,20,30)))
[IO.File]::WriteAllBytes($blank, [byte[]]($rasterHeader + [byte[]]@(255,255,255)))
[IO.File]::WriteAllBytes($broken, [byte[]]($rasterHeader + [byte[]]@(10,20)))
$rasterOptions = @('--expected-width','1','--expected-height','1','--max-mean-error','0','--max-changed-fraction','0','--channel-threshold','8')
foreach ($rasterCase in @(
    @{Name='same'; Left=$ink; Right=$ink; Options=$rasterOptions; Exit=0; Message='mean-error=0.000000'},
    @{Name='mismatch'; Left=$blank; Right=$ink; Options=$rasterOptions; Exit=2; Message='changed-fraction=1.000000'},
    @{Name='blank'; Left=$blank; Right=$blank; Options=$rasterOptions; Exit=1; Message='Reference crop is blank'},
    @{Name='wrong-crop'; Left=$ink; Right=$ink; Options=@('--expected-width','2') + $rasterOptions[2..9]; Exit=2; Message='dimensions differ'},
    @{Name='broken'; Left=$broken; Right=$ink; Options=$rasterOptions; Exit=1; Message='payload length mismatch'}
)) {
    $rasterResult = Invoke-ColdProbe -Arguments (@('compare-raster','--left',$rasterCase.Left,'--right',$rasterCase.Right) + $rasterCase.Options) -InputText ''
    if ($rasterResult.ExitCode -ne $rasterCase.Exit -or ($rasterResult.Output + $rasterResult.Error) -notmatch [regex]::Escape($rasterCase.Message)) { throw "Raster control failed: $($rasterCase.Name)" }
}
Write-Output 'Reference probe controls passed: eight cold/batch semantic pairs including Unicode filenames, checked argv/job/Unicode-label bytes, fresh hosts, lazy inputs, both pipeline/process wrappers, 87 requests/two bounded workers and both protocol negatives.'
Write-Output 'Raster controls passed: exact nonblank match, mismatch, blank crop, wrong expected dimensions and truncated payload.'

# Exercise both real version guards through their existing command-line paths.
$previousVersion = $env:QPDF_TEST_ORACLE_VERSION
try {
    foreach ($case in @(
        @{Name='pinned'; Banner="qpdf version 12.4.2`nRun qpdf --copyright to see copyright and license information."; Exit=0},
        @{Name='pinned-crlf'; Banner="qpdf version 12.4.2 `r`nCopyright information"; Exit=0},
        @{Name='prefix'; Banner='qpdf version 112.4.2'; Exit=1},
        @{Name='patch'; Banner='qpdf version 12.4.20'; Exit=1},
        @{Name='other'; Banner='qpdf version 12.4.3'; Exit=1},
        @{Name='qualified'; Banner='qpdf version 12.4.2-custom'; Exit=1},
        @{Name='wrong-line'; Banner="qpdf version 12.4.3`nBuild reference 12.4.2"; Exit=1},
        @{Name='missing'; Banner='Copyright 12.4.2'; Exit=1}
    )) {
        $env:QPDF_TEST_ORACLE_VERSION = $case.Banner
        $bootstrap = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $OracleFixture 2>&1 | Out-String
        $bootstrapExit = $LASTEXITCODE
        $probe = Invoke-ColdProbe -Arguments @('probe-oracle','--qpdf-exe',$OracleFixture,'--input',(Join-Path $WorkRoot 'inputs/a.pdf')) -InputText ''
        [IO.File]::WriteAllText((Join-Path $WorkRoot ("version-$($case.Name)-bootstrap.log")), $bootstrap)
        [IO.File]::WriteAllText((Join-Path $WorkRoot ("version-$($case.Name)-probe.log")), $probe.Output + $probe.Error)
        if ($bootstrapExit -ne $case.Exit -or $probe.ExitCode -ne $case.Exit -or
            ($case.Exit -ne 0 -and ($bootstrap -notmatch 'qpdf version mismatch' -or $probe.Error -notmatch 'qpdf version mismatch'))) {
            throw "Oracle version guard failed: $($case.Name) (bootstrap=$bootstrapExit probe=$($probe.ExitCode))."
        }
    }
} finally { $env:QPDF_TEST_ORACLE_VERSION = $previousVersion }
Write-Output 'Oracle version controls passed: pinned LF/CRLF banners accepted; prefix, patch, other, qualified, wrong-line and missing versions reject in both guards.'

$nativeRoot = Join-Path $WorkRoot "native paths $([char]0xC9)$([char]0x65E5)$([char]0x672C)"
$nativeBin = Join-Path $nativeRoot 'executable files'
New-Item -ItemType Directory -Path $nativeBin | Out-Null
Get-ChildItem -LiteralPath (Split-Path $OracleFixture) -File | Copy-Item -Destination $nativeBin
$nativeFixture = Join-Path $nativeBin ([IO.Path]::GetFileName($OracleFixture))
$nativeOut = Join-Path $nativeRoot 'stdout.bin'; $nativeErr = Join-Path $nativeRoot 'stderr.bin'
$nativeArguments = @('one value', '', 'quote"inside', 'trailing\', $unicodeInput, '--looks-like-option')
$process = Start-ReferenceProcess -FilePath $nativeFixture -ArgumentList (@('--describe-arguments') + $nativeArguments) `
    -WorkingDirectory $nativeRoot -Wait -PassThru -NoNewWindow -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
$description = [IO.File]::ReadAllText($nativeOut) | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $description.WorkingDirectory -cne $nativeRoot -or
    (ConvertTo-Json -InputObject @($description.Arguments) -Compress) -cne (ConvertTo-Json -InputObject $nativeArguments -Compress)) {
    throw 'Native launch changed argument boundaries or working directory.'
}
$nativeInput = Join-Path $nativeRoot 'input bytes.bin'
[byte[]] $payload = @(0..255) * 512
[IO.File]::WriteAllBytes($nativeInput, $payload)
$process = Start-ReferenceProcess -FilePath $nativeFixture -ArgumentList @('--copy-stdin') -WorkingDirectory $nativeRoot `
    -Wait -PassThru -NoNewWindow -RedirectStandardInput $nativeInput -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
if ($process.ExitCode -ne 7 -or [Convert]::ToHexString([IO.File]::ReadAllBytes($nativeOut)) -cne [Convert]::ToHexString($payload) -or
    [Convert]::ToHexString([IO.File]::ReadAllBytes($nativeErr)) -cne '0080FEFF0A') { throw 'Native stdin/EOF, binary capture or exit status changed.' }
$process = Start-ReferenceProcess -FilePath $nativeFixture -ArgumentList @('--close-stdin', '9') -Wait -PassThru -NoNewWindow `
    -RedirectStandardInput $nativeInput -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
if ($process.ExitCode -ne 9 -or [IO.File]::ReadAllText($nativeErr).Trim() -cne 'input rejected') {
    throw 'Early native rejection lost its exit status or diagnostic.'
}
$inputFailed = $false
try {
    $null = Start-ReferenceProcess -FilePath $nativeFixture -ArgumentList @('--close-stdin', '0') -Wait -PassThru -NoNewWindow `
        -RedirectStandardInput $nativeInput -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
} catch [IO.IOException] { $inputFailed = $true }
if (-not $inputFailed) { throw 'Native success hid an incomplete stdin transfer.' }
try {
    $null = Start-ReferenceProcess -FilePath (Join-Path $nativeBin 'missing-executable') -ArgumentList @() -Wait -PassThru -NoNewWindow `
        -RedirectStandardInput $nativeInput -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
    throw 'Missing native executable unexpectedly started.'
} catch { if ($_.Exception.Message -eq 'Missing native executable unexpectedly started.') { throw } }
foreach ($path in @($nativeInput, $nativeOut, $nativeErr)) {
    $exclusive = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $exclusive.Dispose()
}
Write-Output 'Native launch controls passed: argument boundaries, spaced/Unicode executable paths, working directory, binary stdin/EOF, both output streams, nonzero exit and start-failure cleanup.'

foreach ($timeoutRoute in @('parser', 'native')) {
    # Both launch routes must retire their child and preserve partial output.
    $pidFile = Join-Path $WorkRoot "$timeoutRoute-oracle.pid"
    $parserRoot = Join-Path $WorkRoot "$timeoutRoute-timeout"
    $timeoutFixture = if ($timeoutRoute -eq 'parser') { $OracleFixture } else { $nativeFixture }
    $previousPidFile = $env:QPDF_TEST_ORACLE_PID_FILE
    try {
        $env:QPDF_TEST_ORACLE_PID_FILE = $pidFile
        if ($timeoutRoute -eq 'parser') {
            $timeoutOutput = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-parser-checks.ps1') `
                -QpdfExe $OracleFixture -ProbeDll $ProbeDll -StreamRecoveryOnly -WorkRoot $parserRoot 2>&1 | Out-String
            $timeoutExit = $LASTEXITCODE
            [IO.File]::WriteAllText((Join-Path $WorkRoot 'parser-timeout.log'), $timeoutOutput)
            if ($timeoutExit -eq 0 -or $timeoutOutput -notmatch 'Oracle process exceeded 30 seconds') { throw 'Parser timeout control did not reach its oracle deadline.' }
        } else {
            New-Item -ItemType Directory -Path $parserRoot | Out-Null
            try {
                $null = Start-ReferenceProcess -FilePath $nativeFixture -ArgumentList @('--version') -Wait -PassThru -NoNewWindow `
                    -RedirectStandardOutput (Join-Path $parserRoot 'native-stdout.bin') -RedirectStandardError (Join-Path $parserRoot 'native-stderr.bin')
                throw 'Native timeout control did not reach its oracle deadline.'
            } catch {
                if ($_.Exception.Message -notmatch 'Native reference process exceeded 30 seconds') { throw }
                [IO.File]::WriteAllText((Join-Path $WorkRoot 'native-timeout.log'), $_.Exception.ToString())
            }
        }
        $child = Get-Process -Id ([int][IO.File]::ReadAllText($pidFile)) -ErrorAction SilentlyContinue
        if ($null -ne $child) {
            try {
                if (-not $child.HasExited -and $child.Path -ceq $timeoutFixture) { throw 'Timed-out oracle is still running.' }
            } finally { $child.Dispose() }
        }
        foreach ($channel in @('stdout', 'stderr')) {
            $capture = @(Get-ChildItem -LiteralPath $parserRoot -Filter "*-$channel.bin")
            if ($capture.Count -ne 1 -or [IO.File]::ReadAllText($capture[0].FullName).Trim() -cne "oracle $channel before timeout") { throw "Parser timeout lost $channel diagnostics." }
        }
    } finally {
        $env:QPDF_TEST_ORACLE_PID_FILE = $previousPidFile
        if (Test-Path -LiteralPath $pidFile) {
            $child = Get-Process -Id ([int][IO.File]::ReadAllText($pidFile)) -ErrorAction SilentlyContinue
            if ($null -ne $child) {
                try {
                    if (-not $child.HasExited -and $child.Path -ceq $timeoutFixture) {
                        $child.Kill($true)
                        if (-not $child.WaitForExit(5000)) { throw 'Oracle fixture did not stop during test cleanup.' }
                    }
                } finally { $child.Dispose() }
            }
        }
    }
    Write-Output "$timeoutRoute timeout control passed: owned oracle exited and both partial output streams survived."
}
exit 0
