<#
.SYNOPSIS
  Negative integration cases for fresh-artifact consumer verification.
.DESCRIPTION
  Requires a packed unsigned Lokad.qpdf artifact, PowerShell and the pinned SDK.
  Mutates copied consumer locks and package manifests under artifacts.
  The supplied package and tracked lock remain unchanged.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$packageHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
$testRoot = Join-Path $repoRoot ('artifacts/consumer-tests/' + [guid]::NewGuid().ToString('N'))
$consumerDirectory = Join-Path $testRoot 'tests/Lokad.Qpdf.PackageConsumer'
$toolDirectory = Join-Path $testRoot 'tools'
New-Item -ItemType Directory -Path $consumerDirectory, $toolDirectory | Out-Null
foreach ($name in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $testRoot
}
foreach ($name in @('Lokad.Qpdf.PackageConsumer.csproj', 'Program.cs', 'RecordingMemoryBudget.cs')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('tests/Lokad.Qpdf.PackageConsumer/' + $name)) -Destination $consumerDirectory
}
$seedPath = Join-Path $repoRoot 'tests/Lokad.Qpdf.PackageConsumer/packages.lock.json'
$seedText = Get-Content -LiteralPath $seedPath -Raw
$seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
$testLockPath = Join-Path $consumerDirectory 'packages.lock.json'
$verifier = Join-Path $toolDirectory 'verify-package-consumer.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-package-consumer.ps1') -Destination $verifier

function Assert-Rejection {
    param([string] $Name, [string] $ExpectedPattern, [string] $ToolPath, [string] $ArtifactPath)
    $output = & pwsh -NoProfile -File $ToolPath -PackagePath $ArtifactPath 2>&1 | Out-String
    $code = $LASTEXITCODE
    [System.IO.File]::WriteAllText((Join-Path $testRoot ($Name + '.log')), $output)
    if ($code -eq 0 -or $output -notmatch $ExpectedPattern -or
        $output.Contains('Fresh consumer evidence:') -or $output.Contains('CONSUMER SMOKE PASSED')) {
        throw "$Name did not reject as expected (exit $code). See its saved log."
    }
    Write-Output "PASS $Name (rejected before restore or consumer execution)"
}

# A stale version must not be made acceptable by changing the local hash.
$lock = $seedText | ConvertFrom-Json -AsHashtable
$lock.dependencies['net10.0']['Lokad.qpdf'].resolved = '0.0.0-invalid'
[System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
Assert-Rejection 'wrong-artifact-version' 'Artifact identity/version must match' $verifier $resolvedPackage

# A changed dependency graph must not silently introduce a codec package.
$lock = $seedText | ConvertFrom-Json -AsHashtable
$lock.dependencies['net10.0']['Unexpected.Codec'] = @{ type = 'Transitive'; resolved = '1.0.0'; contentHash = 'invalid' }
[System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
Assert-Rejection 'unexpected-consumer-dependency' 'Consumer must depend only on' $verifier $resolvedPackage

# NuGet accepts either grouped or flat dependencies. Exercise actual artifacts,
# independently of the copied-lock controls above, against both manifest guards.
[System.IO.File]::WriteAllText($testLockPath, $seedText)
foreach ($layout in @('grouped', 'ungrouped')) {
    $directory = Join-Path $testRoot $layout
    New-Item -ItemType Directory -Path $directory | Out-Null
    $artifact = Join-Path $directory ([IO.Path]::GetFileName($resolvedPackage))
    Copy-Item -LiteralPath $resolvedPackage -Destination $artifact
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')) `
        -Destination ([IO.Path]::ChangeExtension($artifact, '.snupkg'))
    $archive = [IO.Compression.ZipFile]::Open($artifact, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('Lokad.qpdf.nuspec')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $dependencies = $manifest.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="dependencies"]')
        $dependencies.RemoveAll()
        $parent = $dependencies
        if ($layout -eq 'grouped') {
            $parent = $manifest.CreateElement('group', $dependencies.NamespaceURI)
            $parent.SetAttribute('targetFramework', 'net10.0')
            $null = $dependencies.AppendChild($parent)
        }
        $dependency = $manifest.CreateElement('dependency', $dependencies.NamespaceURI)
        $dependency.SetAttribute('id', 'Unexpected.Codec')
        $dependency.SetAttribute('version', '[1.0.0]')
        $null = $parent.AppendChild($dependency)
        $entry.Delete()
        $stream = $archive.CreateEntry('Lokad.qpdf.nuspec').Open()
        try { $manifest.Save($stream) } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    Assert-Rejection "$layout-package-dependency" 'The core package must have no runtime package dependencies' `
        (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
    Assert-Rejection "$layout-consumer-dependency" 'The core package must have no runtime package dependencies' `
        $verifier $artifact
}

foreach ($case in @(
    @{ Name='packaged-link'; Readme='[license](LICENSE.txt)'; Reject=$false },
    @{ Name='packaged-fragment'; Readme='[license](./LICENSE.txt#text)'; Reject=$false },
    @{ Name='packaged-reference'; Readme="[license][text]`n[text]: LICENSE.txt"; Reject=$false },
    @{ Name='external-link'; Readme='[docs](https://example.invalid/not-fetched)'; Reject=$false },
    @{ Name='local-anchor'; Readme='[section](#section)'; Reject=$false },
    @{ Name='missing-inline-link'; Readme='[docs](docs/missing.md)'; Reject=$true },
    @{ Name='missing-reference-link'; Readme="[docs][text]`n[text]: docs/missing.md"; Reject=$true }
)) {
    $directory = Join-Path $testRoot $case.Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $artifact = Join-Path $directory ([IO.Path]::GetFileName($resolvedPackage))
    Copy-Item -LiteralPath $resolvedPackage -Destination $artifact
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')) `
        -Destination ([IO.Path]::ChangeExtension($artifact, '.snupkg'))
    $archive = [IO.Compression.ZipFile]::Open($artifact, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry('README.md').Delete()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('README.md').Open())
        try { $writer.WriteLine($case.Readme) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    if ($case.Reject) {
        Assert-Rejection $case.Name 'README link target is missing from package' (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
    } else {
        $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $artifact 2>&1 | Out-String
        [IO.File]::WriteAllText((Join-Path $testRoot ($case.Name + '.log')), $output)
        if ($LASTEXITCODE -ne 0) { throw "$($case.Name) rejected a valid package link. See $testRoot." }
        Write-Output "PASS $($case.Name)"
    }
}

if ((Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash -cne $seedHash) {
    throw 'Negative verification changed the tracked consumer lock.'
}
if ((Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash -cne $packageHash) {
    throw 'Negative verification changed the supplied package.'
}
Write-Output "Consumer verification negative cases passed; evidence: $testRoot"
# Expected child failures leave LASTEXITCODE nonzero in a calling pwsh step.
exit 0
