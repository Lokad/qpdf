[CmdletBinding()]
param([string] $LogPath)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$output = Join-Path $artifacts 'nuget'
$staging = Join-Path $artifacts ('pack-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $staging | Out-Null
if ([string]::IsNullOrWhiteSpace($LogPath)) { $LogPath = Join-Path $artifacts 'pack.log' }

$project = Join-Path $repo 'src/Lokad.Qpdf/Lokad.Qpdf.csproj'
dotnet pack $project -c Release --no-build --no-restore --output $staging 2>&1 |
    Tee-Object -FilePath $LogPath | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Release pack failed ($LASTEXITCODE). See $LogPath." }

$packages = @(Get-ChildItem -LiteralPath $staging -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) {
    throw "Expected one package from the pack in '$staging', found $($packages.Count). See $LogPath."
}
$package = $packages[0]
$symbols = [IO.Path]::ChangeExtension($package.FullName, '.snupkg')
if (!(Test-Path -LiteralPath $symbols -PathType Leaf)) {
    throw "Missing symbol package '$symbols'. See $LogPath."
}

$destination = Join-Path $output $package.Name
$symbolDestination = [IO.Path]::ChangeExtension($destination, '.snupkg')
$previousSymbols = Join-Path $staging 'previous.snupkg'
$key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($destination.ToUpperInvariant())))
$publication = [Threading.Mutex]::new($false, "Lokad.Qpdf.Pack.$key")
$locked = $false
try {
    try { $locked = $publication.WaitOne([TimeSpan]::FromSeconds(30)) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw "Timed out publishing '$destination'. See $LogPath." }

    if ([IO.File]::Exists($symbolDestination)) {
        [IO.File]::Replace($symbols, $symbolDestination, $previousSymbols)
    } else {
        [IO.File]::Move($symbols, $symbolDestination)
    }
    try {
        [IO.File]::Move($package.FullName, $destination, $true)
    } catch {
        if ([IO.File]::Exists($previousSymbols)) {
            [IO.File]::Move($previousSymbols, $symbolDestination, $true)
        } else {
            [IO.File]::Delete($symbolDestination)
        }
        throw
    }
    [IO.File]::Delete($previousSymbols)
} finally {
    if ($locked) { $publication.ReleaseMutex() }
    $publication.Dispose()
}
[IO.Directory]::Delete($staging)
$destination
