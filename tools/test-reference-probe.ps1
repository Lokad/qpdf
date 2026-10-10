param(
    [Parameter(Mandatory = $true)] [string] $ProbeDll,
    [Parameter(Mandatory = $true)] [string] $WorkRoot
)
$ErrorActionPreference = 'Stop'
$ProbeDll = (Resolve-Path -LiteralPath $ProbeDll).Path
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $WorkRoot) { throw 'Probe test workspace already exists.' }
New-Item -ItemType Directory -Path (Join-Path $WorkRoot 'inputs') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../tests/Fixtures/minimal-1page.pdf') -Destination (Join-Path $WorkRoot 'inputs/a.pdf')
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

. (Join-Path $PSScriptRoot 'ReferenceProbe.ps1')
Initialize-ReferenceProbe -ProbeDll $ProbeDll -WorkRoot $WorkRoot
try {
    $cases = @(
        @('emit','--workdir',$WorkRoot,'--','inputs/a.pdf','--show-npages'),
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
        # Maintenance work may vary with ordinary CLR collection timing even
        # between two cold calls. Preserve the counter record, compare all
        # status/diagnostic bytes, and keep the original job limits unchanged.
        $counter = '(?m)^emit: counters work=\d+ decoded=\d+ peakmem=\d+\r?\n'
        if (@([regex]::Matches($cold.Output,$counter)).Count -ne @([regex]::Matches($warmOutput,$counter)).Count -or
            $cold.ExitCode -ne $warm.ExitCode -or
            [regex]::Replace($cold.Output,$counter,'') -cne [regex]::Replace($warmOutput,$counter,'') -or
            $cold.Error -cne [IO.File]::ReadAllText($warm.StandardError)) { throw "Cold/batch result differs: $($arguments -join ' ')" }
    }
    $coldPdf = Join-Path $WorkRoot 'cold.pdf'; $warmPdf = Join-Path $WorkRoot 'warm.pdf'
    $cold = Invoke-ColdProbe -Arguments @('emit-job','--workdir',$WorkRoot,'--job','job.json','--out',"outputs/a.pdf=$coldPdf") -InputText ''
    $warm = Invoke-ReferenceProbeRequest -Arguments @('emit-job','--workdir',$WorkRoot,'--job','job.json','--out',"outputs/a.pdf=$warmPdf") -WorkingDirectory $WorkRoot
    if ($cold.ExitCode -ne 0 -or $warm.ExitCode -ne 0 -or
        (Get-FileHash $coldPdf).Hash -cne (Get-FileHash $warmPdf).Hash) { throw 'Relative job/alias rewrite differs.' }
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
        $arguments = if (($i % 3) -eq 1) { $cases[2] } else { $cases[0] }
        $expected = if (($i % 3) -eq 1) { 2 } else { 0 }
        $warm=Invoke-ReferenceProbeRequest -Arguments $arguments -WorkingDirectory $WorkRoot
        if ($warm.ExitCode -ne $expected) { throw "Worker rollover/state isolation failed at $i." }
    }
    foreach ($index in @(0,2)) {
        $expected = if ($index -eq 0) { 0 } else { 2 }
        $lines = @(Invoke-ReferenceProbe -Arguments (@($ProbeDll)+$cases[$index]) 2>&1)
        if ($LASTEXITCODE -ne $expected -or ($lines -join "`n") -notmatch "emit: status=\w+ exit=$expected") { throw 'Pipeline transport changed output or native exit status.' }
        $output=Join-Path $WorkRoot "redirect-$index.txt"; $redirectError=Join-Path $WorkRoot "redirect-$index-error.txt"
        $process=Start-ReferenceProcess -FilePath dotnet -ArgumentList (@($ProbeDll)+$cases[$index]) -WorkingDirectory $WorkRoot -Wait -PassThru -NoNewWindow -RedirectStandardOutput $output -RedirectStandardError $redirectError
        if ($process.ExitCode -ne $expected -or [IO.File]::ReadAllText($output) -notmatch "emit: status=\w+ exit=$expected" -or -not (Test-Path $redirectError)) { throw 'Process/redirect transport changed output or exit status.' }
    }
} finally { Complete-ReferenceProbe }
$transport=Get-Content (Join-Path $WorkRoot 'managed-calls/results.json') -Raw | ConvertFrom-Json
if ($transport.Workers -ne 2 -or $transport.Requests.Count -ne 85) { throw 'Bounded worker/request coverage changed.' }
$bad=Invoke-ColdProbe -Arguments @('batch') -InputText "{bad}`n"
if ($bad.ExitCode -ne 1 -or $bad.Error -notmatch 'Invalid batch JSON') { throw 'Malformed protocol did not fail explicitly.' }
$large=Invoke-ColdProbe -Arguments @('batch') -InputText ((' ' * (4*1024*1024+1))+"`n")
if ($large.ExitCode -ne 1 -or $large.Error -notmatch 'four MiB') { throw 'Protocol request bound did not reject.' }
Write-Output 'Reference probe controls passed: seven cold/batch semantic pairs, checked argv/job bytes, fresh hosts, lazy inputs, both pipeline/process wrappers, 85 requests/two bounded workers and both protocol negatives.'
