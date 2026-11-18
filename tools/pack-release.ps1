[CmdletBinding()]
param([string] $LogPath)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ('artifacts/nuget/pack-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
if ([string]::IsNullOrWhiteSpace($LogPath)) { $LogPath = Join-Path $output 'pack.log' }

$project = Join-Path $repo 'src/Lokad.Qpdf/Lokad.Qpdf.csproj'
dotnet pack $project -c Release --no-build --no-restore --output $output 2>&1 |
    Tee-Object -FilePath $LogPath | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Release pack failed ($LASTEXITCODE). See $LogPath." }

$packages = @(Get-ChildItem -LiteralPath $output -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) {
    throw "Expected one package from the pack in '$output', found $($packages.Count). See $LogPath."
}
$packages[0].FullName
