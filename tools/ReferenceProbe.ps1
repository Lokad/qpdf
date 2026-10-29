# Developer-only sequential transport. Each request uses the existing probe
# command and a fresh host/job; workers retire after 64 requests without GC forcing.
$script:referenceProbeState = $null

function Initialize-ReferenceProbe {
    param([string] $ProbeDll, [string] $WorkRoot)
    $root = Join-Path $WorkRoot 'managed-calls'
    New-Item -ItemType Directory -Path $root | Out-Null
    $script:referenceProbeState = @{
        Probe = [IO.Path]::GetFullPath($ProbeDll); Root = $root; Process = $null;
        ErrorTask = $null; Index = 0; Workers = 0;
        Calls = [Collections.Generic.List[object]]::new()
    }
}

function Stop-ReferenceProbeWorker {
    $state = $script:referenceProbeState
    if ($null -eq $state -or $null -eq $state.Process) { return }
    $process = $state.Process
    try {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(30000)
            throw 'Managed probe worker did not retire within 30 seconds.'
        }
        $errorText = $state.ErrorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Managed probe worker exited $($process.ExitCode): $errorText" }
    } finally {
        $process.Dispose()
        $state.Process = $null
    }
}

function Complete-ReferenceProbe {
    $state = $script:referenceProbeState
    if ($null -eq $state) { return }
    try { Stop-ReferenceProbeWorker }
    finally {
        @{
            ProbeSHA256 = (Get-FileHash -LiteralPath $state.Probe).Hash
            LibrarySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $state.Probe) 'Lokad.Qpdf.dll')).Hash
            Workers = $state.Workers; MaximumRequestsPerWorker = 64; Sequential = $true
            Requests = $state.Calls
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state.Root 'results.json') -Encoding utf8NoBOM
        $script:referenceProbeState = $null
    }
}

function Invoke-ReferenceProbeRequest {
    param([string[]] $Arguments, [string] $WorkingDirectory)
    $state = $script:referenceProbeState
    if ($null -eq $state) { throw 'Managed probe transport is not initialized.' }
    if ($null -ne $state.Process -and $state.Index -eq 64) { Stop-ReferenceProbeWorker }
    if ($null -eq $state.Process) {
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        $start.ArgumentList.Add($state.Probe)
        $start.ArgumentList.Add('batch')
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { $process.Dispose(); throw 'Managed probe worker did not start.' }
        $state.Process = $process
        $state.ErrorTask = $process.StandardError.ReadToEndAsync()
        $state.Index = 0
        $state.Workers++
    }

    $directory = Join-Path $state.Root $state.Calls.Count.ToString('D6')
    New-Item -ItemType Directory -Path $directory | Out-Null
    $stdout = Join-Path $directory 'stdout.txt'
    $stderr = Join-Path $directory 'stderr.txt'
    $request = @{
        Id = $state.Index; Arguments = $Arguments; WorkingDirectory = $WorkingDirectory
        StandardOutput = $stdout; StandardError = $stderr
    } | ConvertTo-Json -Depth 4 -Compress
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $exitCode = $null
    try {
        $state.Process.StandardInput.WriteLine($request)
        $state.Process.StandardInput.Flush()
        $read = $state.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(30000)) {
            $state.Process.Kill($true)
            $null = $state.Process.WaitForExit(30000)
            throw 'Managed probe request exceeded 30 seconds.'
        }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Managed probe worker closed before its response.' }
        $response = $line | ConvertFrom-Json
        if ($response.Id -ne $state.Index -or $null -eq $response.ExitCode) { throw 'Managed probe response is out of order or incomplete.' }
        $state.Index++
        $exitCode = [int]$response.ExitCode
        $result = @{ ExitCode = [int]$response.ExitCode; StandardOutput = $stdout; StandardError = $stderr }
        return $result
    } finally {
        $watch.Stop()
        $state.Calls.Add(@{
            Arguments = $Arguments; WorkingDirectory = $WorkingDirectory; Seconds = $watch.Elapsed.TotalSeconds
            ExitCode = $exitCode
            StandardOutput = [IO.Path]::GetRelativePath($state.Root, $stdout).Replace('\','/')
            StandardError = [IO.Path]::GetRelativePath($state.Root, $stderr).Replace('\','/')
        })
    }
}

function Invoke-ReferenceProbe {
    param([string[]] $Arguments)
    if ($Arguments.Length -eq 0 -or [IO.Path]::GetFullPath($Arguments[0]) -cne $script:referenceProbeState.Probe) {
        throw 'Managed probe invocation uses an unexpected assembly.'
    }
    [string[]] $command = @()
    if ($Arguments.Length -gt 1) { $command = $Arguments[1..($Arguments.Length - 1)] }
    $result = Invoke-ReferenceProbeRequest -Arguments $command -WorkingDirectory (Get-Location).ProviderPath
    Get-Content -LiteralPath $result.StandardOutput
    foreach ($line in Get-Content -LiteralPath $result.StandardError) { Write-Error -Message $line -ErrorAction Continue }
    $global:LASTEXITCODE = $result.ExitCode
}

function Start-ReferenceProcess {
    param(
        [string] $FilePath, [string[]] $ArgumentList, [string] $WorkingDirectory,
        [switch] $NoNewWindow, [switch] $Wait, [switch] $PassThru,
        [string] $RedirectStandardInput, [string] $RedirectStandardOutput, [string] $RedirectStandardError
    )
    if ($FilePath -ne 'dotnet') {
        if (-not $Wait -or -not $PassThru) { throw 'Unsupported native reference process invocation.' }
        $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        if ($WorkingDirectory) { $start.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory) }
        foreach ($argument in $ArgumentList) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        $cancellation = [Threading.CancellationTokenSource]::new(30000)
        $transfers = [Collections.Generic.List[Threading.Tasks.Task]]::new()
        $inputFile = $null; $outputFile = $null; $errorFile = $null
        $started = $false
        try {
            if ($RedirectStandardInput) { $inputFile = [IO.File]::OpenRead($RedirectStandardInput); $start.RedirectStandardInput = $true }
            if ($RedirectStandardOutput) { $outputFile = [IO.File]::Create($RedirectStandardOutput); $start.RedirectStandardOutput = $true }
            if ($RedirectStandardError) { $errorFile = [IO.File]::Create($RedirectStandardError); $start.RedirectStandardError = $true }
            $started = $process.Start()
            if (-not $started) { throw 'Native reference process did not start.' }
            if ($outputFile) { $transfers.Add($process.StandardOutput.BaseStream.CopyToAsync($outputFile, $cancellation.Token)) }
            if ($errorFile) { $transfers.Add($process.StandardError.BaseStream.CopyToAsync($errorFile, $cancellation.Token)) }
            $feedFailure = $null
            if ($inputFile) {
                $feed = $inputFile.CopyToAsync($process.StandardInput.BaseStream, $cancellation.Token)
                # Keep the IOException chain: its innermost Unix error may be a socket exception.
                try { $feed.GetAwaiter().GetResult() }
                catch [IO.IOException] { $feedFailure = $_.Exception }
                finally {
                    try { $process.StandardInput.Close() }
                    catch [IO.IOException] {
                        if ($null -eq $feedFailure) { $feedFailure = $_.Exception }
                    }
                }
            }
            $transfers.Add($process.WaitForExitAsync($cancellation.Token))
            [Threading.Tasks.Task]::WhenAll($transfers.ToArray()).GetAwaiter().GetResult()
            $exitCode = $process.ExitCode
            if ($null -ne $feedFailure -and $exitCode -eq 0) { throw $feedFailure }
        } catch [OperationCanceledException] {
            throw "Native reference process exceeded 30 seconds: $FilePath"
        } finally {
            try {
                if ($started -and -not $process.HasExited) {
                    $process.Kill($true)
                    if (-not $process.WaitForExit(5000)) { throw 'Native reference process did not stop after termination.' }
                }
            } finally {
                $cancellation.Cancel()
                try {
                    # Observe every transfer before closing its backing file,
                    # including captures still completing during failed cleanup.
                    if ($transfers.Count) {
                        try {
                            if (-not [Threading.Tasks.Task]::WhenAll($transfers.ToArray()).Wait(5000)) {
                                throw 'Native reference transfers did not stop.'
                            }
                        } catch [AggregateException] { }
                    }
                } finally {
                    foreach ($file in @($inputFile, $outputFile, $errorFile)) { if ($null -ne $file) { $file.Dispose() } }
                    $process.Dispose(); $cancellation.Dispose()
                }
            }
        }
        return [pscustomobject]@{ ExitCode = $exitCode }
    }
    if (-not $Wait -or -not $PassThru -or $RedirectStandardInput) { throw 'Unsupported managed probe process invocation.' }
    if ([IO.Path]::GetFullPath($ArgumentList[0]) -cne $script:referenceProbeState.Probe) { throw 'Unexpected managed probe assembly.' }
    $directory = if ($WorkingDirectory) { [IO.Path]::GetFullPath($WorkingDirectory) } else { (Get-Location).ProviderPath }
    $result = Invoke-ReferenceProbeRequest -Arguments $ArgumentList[1..($ArgumentList.Length - 1)] -WorkingDirectory $directory
    if ($RedirectStandardOutput) { Copy-Item -LiteralPath $result.StandardOutput -Destination $RedirectStandardOutput }
    else { foreach ($line in Get-Content -LiteralPath $result.StandardOutput) { Write-Host $line } }
    if ($RedirectStandardError) { Copy-Item -LiteralPath $result.StandardError -Destination $RedirectStandardError }
    else { foreach ($line in Get-Content -LiteralPath $result.StandardError) { Write-Host $line } }
    return [pscustomobject]@{ ExitCode = $result.ExitCode }
}
