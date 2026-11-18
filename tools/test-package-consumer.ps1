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
foreach ($name in @('Lokad.Qpdf.PackageConsumer.csproj', 'Program.cs', 'RecordingMemoryBudget.cs', 'PageCountChecks.cs')) {
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

# Both framework graphs must reject stale identities and unexpected dependencies.
foreach ($framework in @('net10.0', 'net11.0')) {
    $lock = $seedText | ConvertFrom-Json -AsHashtable
    $lock.dependencies[$framework]['Lokad.qpdf'].resolved = '0.0.0-invalid'
    [System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
    Assert-Rejection "wrong-$framework-artifact-version" 'Artifact identity/version must match' $verifier $resolvedPackage

    $lock = $seedText | ConvertFrom-Json -AsHashtable
    $lock.dependencies[$framework]['Unexpected.Codec'] = @{ type = 'Transitive'; resolved = '1.0.0'; contentHash = 'invalid' }
    [System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
    Assert-Rejection "unexpected-$framework-consumer-dependency" 'Consumer must depend only on' $verifier $resolvedPackage
}

$lock = $seedText | ConvertFrom-Json -AsHashtable
$lock.dependencies.Remove('net11.0')
[System.IO.File]::WriteAllText($testLockPath, ($lock | ConvertTo-Json -Depth 30) + "`n")
Assert-Rejection 'missing-framework-lock' 'Expected the reviewed net10.0/net11.0 consumer lock' $verifier $resolvedPackage

# A valid package boundary must not conceal the previous release's false description.
foreach ($field in @('description', 'releaseNotes')) {
    $directory = Join-Path $testRoot "stale-$field"
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
        $node = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='$field']")
        $node.InnerText = if ($field -eq 'description') { 'qpdf non-cryptographic subset' } else { 'Password encryption remains unsupported.' }
        $entry.Delete()
        $stream = $archive.CreateEntry('Lokad.qpdf.nuspec').Open()
        try { $manifest.Save($stream) } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    Assert-Rejection "stale-$field" 'Package metadata must describe supported password security' `
        (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
}

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

foreach ($framework in @('net10.0', 'net11.0')) {
    foreach ($kind in @('library', 'symbols')) {
        $directory = Join-Path $testRoot "missing-$framework-$kind"
        New-Item -ItemType Directory -Path $directory | Out-Null
        $artifact = Join-Path $directory ([IO.Path]::GetFileName($resolvedPackage))
        Copy-Item -LiteralPath $resolvedPackage -Destination $artifact
        $symbols = [IO.Path]::ChangeExtension($artifact, '.snupkg')
        Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')) -Destination $symbols
        $changed = if ($kind -eq 'library') { $artifact } else { $symbols }
        $extension = if ($kind -eq 'library') { 'dll' } else { 'pdb' }
        $archive = [IO.Compression.ZipFile]::Open($changed, [IO.Compression.ZipArchiveMode]::Update)
        try { $archive.GetEntry("lib/$framework/Lokad.Qpdf.$extension").Delete() }
        finally { $archive.Dispose() }
        $pattern = if ($kind -eq 'library') { 'Missing package member' } else { 'Missing portable symbols' }
        Assert-Rejection "missing-$framework-$kind" $pattern (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
        if ($kind -eq 'library') {
            Assert-Rejection "missing-$framework-consumer-library" "Missing $framework library assembly" $verifier $artifact
        }
    }
}

foreach ($case in @(
    @{ Name='packaged-link'; Readme='[license](LICENSE.txt)'; Reject=$false },
    @{ Name='packaged-fragment'; Readme='[license](./LICENSE.txt#text)'; Reject=$false },
    @{ Name='packaged-reference'; Readme="[license][text]`n[text]: LICENSE.txt"; Reject=$false },
    @{ Name='external-link'; Readme='[docs](https://example.invalid/not-fetched)'; Reject=$false },
    @{ Name='local-anchor'; Readme=('## Section' + [Environment]::NewLine + '[section](#section)'); Reject=$false },
    @{ Name='guide-parent-link'; Member='docs/SUPPORTED-PROFILE.md'; Readme='[license](../LICENSE.txt)'; Reject=$false },
    @{ Name='guide-heading'; Member='docs/SUPPORTED-PROFILE.md'; Readme='[input](MEMORY.md#input-leases)'; Reject=$false },
    @{ Name='duplicate-guide-heading'; Member='docs/MEMORY.md'; Readme=(@('## Example heading', '', 'Example heading', '---', '', '[section](#example-heading-1)') -join [Environment]::NewLine); Reject=$false },
    @{ Name='fenced-markdown'; Member='docs/SUPPORTED-PROFILE.md'; Readme=(@('~~~md', '[example](missing.md)', '~~~') -join [Environment]::NewLine); Reject=$false },
    @{ Name='missing-readme-heading'; Readme='[section](#missing-heading)'; Reject=$true; Heading=$true },
    @{ Name='missing-guide-link'; Member='docs/SUPPORTED-PROFILE.md'; Readme='[guide](missing.md)'; Reject=$true },
    @{ Name='missing-guide-heading'; Member='docs/SUPPORTED-PROFILE.md'; Readme='[section](MEMORY.md#missing-heading)'; Reject=$true; Heading=$true },
    @{ Name='path-outside-package'; Member='docs/SUPPORTED-PROFILE.md'; Readme='[license](../../LICENSE.txt)'; Reject=$true },
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
        $member = if ($case.Member) { $case.Member } else { 'README.md' }
        $entry = $archive.GetEntry($member)
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $original = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $entry.Delete()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($member).Open())
        try { $writer.WriteLine($original); $writer.WriteLine($case.Readme) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    if ($case.Reject) {
        $issue = if ($case.Heading) { 'Package Markdown heading is missing' } else { 'Package Markdown link target is missing' }
        Assert-Rejection $case.Name $issue (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
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
