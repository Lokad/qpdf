<#
.SYNOPSIS
  Restore and run the public consumer against exactly the supplied local package.
.DESCRIPTION
  Uses a new consumer directory, feed and package cache for every run. The copied
  lock binds only Lokad.qpdf's content hash to the supplied unsigned artifact;
  all versions and dependency constraints stay locked. Successful runs keep
  logs and hashes. Use -KeepWorkspace to keep the full workspace on success.
.LINK
  https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping
.LINK
  https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [switch] $KeepWorkspace
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageArchive.ps1')
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$consumerSource = Join-Path $repoRoot 'tests/Lokad.Qpdf.PackageConsumer'
$sourceProject = Join-Path $consumerSource 'Lokad.Qpdf.PackageConsumer.csproj'
$fixtureSource = Join-Path $repoRoot 'tests/Fixtures'
$seedPath = Join-Path $consumerSource 'packages.lock.json'
$seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
$lock = Get-Content -LiteralPath $seedPath -Raw | ConvertFrom-Json -AsHashtable
$frameworks = @('net10.0', 'net11.0')
if ($lock.version -ne 2 -or $lock.dependencies.Count -ne $frameworks.Count) {
    throw 'Expected the reviewed net10.0/net11.0 consumer lock.'
}
foreach ($framework in $frameworks) {
    if (-not $lock.dependencies.Contains($framework)) { throw 'Expected the reviewed net10.0/net11.0 consumer lock.' }
    $dependencies = $lock.dependencies[$framework]
    if ($dependencies.Count -ne 1 -or -not $dependencies.Contains('Lokad.qpdf')) {
        throw 'Consumer must depend only on the core Lokad.qpdf package.'
    }
}
$expectedLibraryHashes = @{}

$package = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    Assert-UniquePackageMembers $package
    if ($null -ne $package.GetEntry('.signature.p7s')) {
        throw 'The local artifact must be unsigned for direct SHA-512 lock binding.'
    }
    $metadata = Read-PackageMetadata $package
    if ($metadata.id -cne 'Lokad.qpdf' -or
        $metadata.version -cne $lock.dependencies['net10.0']['Lokad.qpdf'].resolved) {
        throw 'Artifact identity/version must match the reviewed consumer lock.'
    }
    Assert-DependencyFreePackage $metadata
    foreach ($framework in $frameworks) {
        if ($metadata.version -cne $lock.dependencies[$framework]['Lokad.qpdf'].resolved) {
            throw 'Artifact identity/version must match the reviewed consumer lock.'
        }
        $library = $package.GetEntry("lib/$framework/Lokad.Qpdf.dll")
        if ($null -eq $library) { throw "Missing $framework library assembly." }
        $libraryStream = $library.Open()
        $libraryHasher = [System.Security.Cryptography.SHA256]::Create()
        try { $expectedLibraryHashes[$framework] = [Convert]::ToHexString($libraryHasher.ComputeHash($libraryStream)) }
        finally { $libraryHasher.Dispose(); $libraryStream.Dispose() }
    }
}
finally { $package.Dispose() }

$runRoot = Join-Path $repoRoot ('artifacts/package-consumer/' + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path $runRoot 'work'
$projectDirectory = Join-Path $workspace 'consumer'
$feedDirectory = Join-Path $workspace 'feed'
$packagesDirectory = Join-Path $workspace 'packages'
New-Item -ItemType Directory -Path $projectDirectory, $feedDirectory, $packagesDirectory | Out-Null
Write-Output "Fresh consumer evidence: $runRoot"

$feedPackage = Join-Path $feedDirectory ("Lokad.qpdf." + $metadata.version + '.nupkg')
Copy-Item -LiteralPath $resolvedPackage -Destination $feedPackage
$packageStream = [System.IO.File]::OpenRead($feedPackage)
$packageHasher = [System.Security.Cryptography.SHA512]::Create()
try { $artifactHash = [Convert]::ToBase64String($packageHasher.ComputeHash($packageStream)) }
finally { $packageHasher.Dispose(); $packageStream.Dispose() }

# Only the locally rebuilt artifact's hash changes; the reviewed graph stays locked.
foreach ($framework in $frameworks) { $lock.dependencies[$framework]['Lokad.qpdf'].contentHash = $artifactHash }
$copiedLock = Join-Path $projectDirectory 'packages.lock.json'
[System.IO.File]::WriteAllText($copiedLock, ($lock | ConvertTo-Json -Depth 30) + "`n")
$copiedLockHash = (Get-FileHash -LiteralPath $copiedLock -Algorithm SHA256).Hash
$project = Join-Path $projectDirectory 'Consumer.csproj'
Copy-Item -LiteralPath $sourceProject -Destination $project
$fixtureDirectory = Join-Path $workspace 'Fixtures'
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null

function Get-ConsumerItems([string] $ProjectPath, [string] $Framework) {
    $evaluation = & dotnet msbuild $ProjectPath -nologo -p:Configuration=Release "-p:TargetFramework=$Framework" `
        '-getItem:Compile,EmbeddedResource,ProjectReference' | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Consumer input evaluation failed for $Framework ($LASTEXITCODE): $evaluation"
    }
    $items = (ConvertFrom-Json -InputObject $evaluation).Items
    if ($items.ProjectReference.Count -ne 0) { throw 'The fresh package consumer must use the packed library.' }
    return $items
}

function Get-ConsumerRelativePath([string] $Root, [string] $InputFile) {
    $relative = [IO.Path]::GetRelativePath($Root, $InputFile)
    $separator = [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith("..$separator", [StringComparison]::Ordinal)) {
        throw "Consumer input is outside its reviewed directory: $InputFile"
    }
    $path = $Root
    if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) {
        throw "Consumer input uses a filesystem link: $path"
    }
    foreach ($part in $relative.Split($separator)) {
        $path = Join-Path $path $part
        if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) {
            throw "Consumer input uses a filesystem link: $path"
        }
    }
    return $relative
}

function Copy-ConsumerInputs($Items, [string] $Source, [string] $Destination) {
    foreach ($item in $Items) {
        $relative = Get-ConsumerRelativePath $Source $item.FullPath
        $target = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) { continue }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $item.FullPath -Destination $target
    }
}

foreach ($framework in $frameworks) {
    $items = Get-ConsumerItems $sourceProject $framework
    Copy-ConsumerInputs $items.Compile $consumerSource $projectDirectory
    Copy-ConsumerInputs $items.EmbeddedResource $fixtureSource $fixtureDirectory
}
foreach ($framework in $frameworks) {
    $items = Get-ConsumerItems $project $framework
    foreach ($item in $items.Compile) { $null = Get-ConsumerRelativePath $projectDirectory $item.FullPath }
    foreach ($item in $items.EmbeddedResource) { $null = Get-ConsumerRelativePath $fixtureDirectory $item.FullPath }
}

$configPath = Join-Path $workspace 'nuget.config'
$escapedFeed = [System.Security.SecurityElement]::Escape($feedDirectory)
$config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="artifact" value="$escapedFeed" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="artifact"><package pattern="Lokad.qpdf" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
[System.IO.File]::WriteAllText($configPath, $config + "`n")
& dotnet restore $project --locked-mode --packages $packagesDirectory --configfile $configPath --no-http-cache 2>&1 |
    Tee-Object -FilePath (Join-Path $runRoot 'restore.log')
if ($LASTEXITCODE -ne 0) { throw "Fresh locked consumer restore failed ($LASTEXITCODE)." }
if ((Get-FileHash -LiteralPath $copiedLock -Algorithm SHA256).Hash -cne $copiedLockHash) {
    throw 'Locked restore changed the prepared consumer lock.'
}
& dotnet build $project -c Release --no-restore --nologo -v quiet 2>&1 |
    Tee-Object -FilePath (Join-Path $runRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Fresh consumer build failed ($LASTEXITCODE)." }
$buildHashes = [ordered]@{}
Write-Output "Artifact SHA-512: $artifactHash"
foreach ($framework in $frameworks) {
    $restoredLibrary = Join-Path $projectDirectory "bin/Release/$framework/Lokad.Qpdf.dll"
    if ((Get-FileHash -LiteralPath $restoredLibrary -Algorithm SHA256).Hash -cne $expectedLibraryHashes[$framework]) {
        throw "Consumer assembly differs from the supplied $framework package assembly."
    }
    Write-Output "Consumer $framework library SHA-256: $($expectedLibraryHashes[$framework])"
    & dotnet run --project $project -f $framework -c Release --no-build --no-restore 2>&1 |
        Tee-Object -FilePath (Join-Path $runRoot "$framework-consumer.log")
    if ($LASTEXITCODE -ne 0) { throw "Fresh $framework consumer smoke failed ($LASTEXITCODE)." }
    $consumerDll = Join-Path $projectDirectory "bin/Release/$framework/Lokad.Qpdf.PackageConsumer.dll"
    $buildHashes[$framework] = [ordered]@{
        librarySha256 = $expectedLibraryHashes[$framework]
        consumerSha256 = (Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash
    }
    foreach ($mode in @('quiet-input', 'raw-input', 'engine-input', 'quiet-output', 'raw-output', 'engine-output')) {
        $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Environment['DOTNET_GCHeapHardLimit'] = '0x2000000'
        $start.ArgumentList.Add($consumerDll)
        $start.ArgumentList.Add('--lease-allocation')
        $start.ArgumentList.Add($mode)
        $child = [System.Diagnostics.Process]::Start($start)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync()
            $stderr = $child.StandardError.ReadToEndAsync()
            $exited = $child.WaitForExit(30000)
            if (-not $exited) {
                $child.Kill($true)
                $child.WaitForExit()
            }
            $output = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $runRoot "$framework-$mode.log"), $output + $errorText)
            Write-Output $output
            if (-not $exited) { throw "Capped lease check timed out: $mode." }
            if ($child.ExitCode -ne 0) { throw "Capped lease check failed: $mode. $errorText" }
        }
        finally {
            if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
            $child.Dispose()
        }
    }
}
if ((Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash -cne $seedHash) {
    throw 'The tracked consumer lock changed during verification.'
}
[ordered]@{
    packageSha256 = (Get-FileHash -LiteralPath $feedPackage -Algorithm SHA256).Hash
    packageSha512 = $artifactHash
    reviewedLockSha256 = $seedHash
    stagedLockSha256 = $copiedLockHash
    frameworks = $buildHashes
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'receipt.json') -Encoding utf8
if (!$KeepWorkspace) { Remove-PackageCheckWorkspace $runRoot }
Write-Output 'Fresh artifact consumer passed; dependency graph stayed locked and the tracked lock is unchanged.'
