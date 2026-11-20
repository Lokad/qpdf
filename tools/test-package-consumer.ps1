<#
.SYNOPSIS
  Negative integration cases for fresh-artifact consumer verification.
.DESCRIPTION
  Requires a packed unsigned Lokad.qpdf artifact, PowerShell and the pinned SDK.
  Mutates copied consumer locks and package manifests under artifacts.
  The supplied package and tracked lock remain unchanged.
  Successful runs keep logs and hashes. Use -KeepWorkspace to retain the
  copied cases on success.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [switch] $CheckReleaseBuild,
    [string] $ArtifactsPath,
    [switch] $KeepWorkspace
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageArchive.ps1')
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$packageHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
$originalSymbols = [IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')
$symbolHash = (Get-FileHash -LiteralPath $originalSymbols -Algorithm SHA256).Hash
$testRoot = Join-Path $repoRoot ('artifacts/consumer-tests/' + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path $testRoot 'work'
$consumerDirectory = Join-Path $workspace 'tests/Lokad.Qpdf.PackageConsumer'
$toolDirectory = Join-Path $workspace 'tools'
New-Item -ItemType Directory -Path $consumerDirectory, $toolDirectory | Out-Null
Write-Output "Consumer control evidence: $testRoot"
$seedPath = Join-Path $repoRoot 'tests/Lokad.Qpdf.PackageConsumer/packages.lock.json'
$seedText = Get-Content -LiteralPath $seedPath -Raw
$seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
$testLockPath = Join-Path $consumerDirectory 'packages.lock.json'
$verifier = Join-Path $toolDirectory 'verify-package-consumer.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-package-consumer.ps1') -Destination $verifier
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageArchive.ps1') -Destination $toolDirectory
$caseReceipts = [ordered]@{}

function Copy-PackageCase([string] $name) {
    $directory = Join-Path $workspace $name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $artifact = Join-Path $directory ([IO.Path]::GetFileName($resolvedPackage))
    Copy-Item -LiteralPath $resolvedPackage -Destination $artifact
    Copy-Item -LiteralPath $originalSymbols -Destination ([IO.Path]::ChangeExtension($artifact, '.snupkg'))
    return $artifact
}

function Invoke-PackageControl {
    param([string] $Name, [string] $ToolPath, [string] $ArtifactPath, [string[]] $ExtraArguments)
    $output = & pwsh -NoProfile -File $ToolPath -PackagePath $ArtifactPath @ExtraArguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $log = Join-Path $testRoot ($Name + '.log')
    [IO.File]::WriteAllText($log, $output)
    $caseReceipt = [ordered]@{
        exitCode = $code
        logSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash
        packageSha256 = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash
        symbolsSha256 = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($ArtifactPath, '.snupkg')) -Algorithm SHA256).Hash
    }
    if ($ToolPath -ceq $verifier) {
        $caseReceipt.lockSha256 = (Get-FileHash -LiteralPath $testLockPath -Algorithm SHA256).Hash
    }
    $caseReceipts[$Name] = $caseReceipt
    return [pscustomobject]@{ Output = $output; ExitCode = $code }
}

function Assert-Rejection {
    param([string] $Name, [string] $ExpectedPattern, [string] $ToolPath, [string] $ArtifactPath, [string[]] $ExtraArguments)
    $result = Invoke-PackageControl $Name $ToolPath $ArtifactPath $ExtraArguments
    $output = $result.Output
    $code = $result.ExitCode
    # PowerShell wraps exception text at the terminal width and adds a gutter.
    $message = $output -replace '\r?\n[ \t]*\|[ \t]*', ' '
    if ($code -eq 0 -or $message -notmatch $ExpectedPattern -or
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
    $artifact = Copy-PackageCase "stale-$field"
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
    $artifact = Copy-PackageCase $layout
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
        $artifact = Copy-PackageCase "missing-$framework-$kind"
        $symbols = [IO.Path]::ChangeExtension($artifact, '.snupkg')
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

$packageVerifier = Join-Path $PSScriptRoot 'verify-package.ps1'
$inventory = Copy-PackageCase 'development-inventory-member'
$archive = [IO.Compression.ZipFile]::Open($inventory, [IO.Compression.ZipArchiveMode]::Update)
try {
    $writer = [IO.StreamWriter]::new($archive.CreateEntry('tests/Compatibility/options.json').Open())
    try { $writer.Write('{}') } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
Assert-Rejection 'development-inventory-member' 'Unexpected package member: tests/Compatibility/options.json' `
    (Join-Path $PSScriptRoot 'verify-package.ps1') $inventory

foreach ($case in @(
    @{ Name='duplicate-library-member'; Member='lib/net10.0/Lokad.Qpdf.dll'; Symbols=$false },
    @{ Name='case-alias-library-member'; Member='readme.md'; Symbols=$false },
    @{ Name='duplicate-symbol-member'; Member='lib/net10.0/Lokad.Qpdf.pdb'; Symbols=$true }
)) {
    $artifact = Copy-PackageCase $case.Name
    $changed = if ($case.Symbols) { [IO.Path]::ChangeExtension($artifact, '.snupkg') } else { $artifact }
    $archive = [IO.Compression.ZipFile]::Open($changed, [IO.Compression.ZipArchiveMode]::Update)
    try { $null = $archive.CreateEntry($case.Member) } finally { $archive.Dispose() }
    Assert-Rejection $case.Name 'Duplicate package member' $packageVerifier $artifact
    if (!$case.Symbols) { Assert-Rejection "$($case.Name)-consumer" 'Duplicate package member' $verifier $artifact }
}

foreach ($kind in 'invalid', 'wrong-framework') {
    $name = "$kind-portable-symbols"
    $artifact = Copy-PackageCase $name
    $archive = [IO.Compression.ZipFile]::Open([IO.Path]::ChangeExtension($artifact, '.snupkg'), [IO.Compression.ZipArchiveMode]::Update)
    try {
        $member = 'lib/net10.0/Lokad.Qpdf.pdb'
        $archive.GetEntry($member).Delete()
        $stream = $archive.CreateEntry($member).Open()
        try {
            if ($kind -eq 'wrong-framework') {
                $other = $archive.GetEntry('lib/net11.0/Lokad.Qpdf.pdb').Open()
                try { $other.CopyTo($stream) } finally { $other.Dispose() }
            } else { $stream.Write([Text.Encoding]::UTF8.GetBytes('ordinary text')) }
        } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    $pattern = if ($kind -eq 'wrong-framework') { 'Portable symbol identity differs' } else { 'Cannot read portable symbols' }
    Assert-Rejection $name $pattern $packageVerifier $artifact
}

$artifact = Copy-PackageCase 'wrong-symbol-package-version'
$archive = [IO.Compression.ZipFile]::Open([IO.Path]::ChangeExtension($artifact, '.snupkg'), [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $archive.GetEntry('Lokad.qpdf.nuspec')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $manifest.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText = '0.0.1'
    $entry.Delete()
    $stream = $archive.CreateEntry('Lokad.qpdf.nuspec').Open()
    try { $manifest.Save($stream) } finally { $stream.Dispose() }
} finally { $archive.Dispose() }
Assert-Rejection 'wrong-symbol-package-version' 'Symbol package identity/version must match' $packageVerifier $artifact

if ($CheckReleaseBuild) {
    $name = 'other-matching-build-pair'
    $artifact = Copy-PackageCase $name
    foreach ($extension in @('dll','pdb')) {
        $path = if ($extension -eq 'dll') { $artifact } else { [IO.Path]::ChangeExtension($artifact, '.snupkg') }
        $member = "Lokad.Qpdf.$extension"
        $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $archive.GetEntry("lib/net10.0/$member").Delete()
            $stream = $archive.CreateEntry("lib/net10.0/$member").Open()
            try {
                $other = $archive.GetEntry("lib/net11.0/$member").Open()
                try { $other.CopyTo($stream) } finally { $other.Dispose() }
            } finally { $stream.Dispose() }
        } finally { $archive.Dispose() }
    }
    $paired = Invoke-PackageControl "$name-portable-match" $packageVerifier $artifact
    if ($paired.ExitCode -ne 0) { throw "Expected the substituted DLL and PDB to match: $($paired.Output)" }
    $receipt = Join-Path $workspace "$name.json"
    $extra = @('-ReleaseReceiptPath', $receipt)
    if ($ArtifactsPath) { $extra += @('-ArtifactsPath', $ArtifactsPath) }
    Assert-Rejection $name 'differs from the tested Release build for net10.0' $packageVerifier $artifact $extra
    if (Test-Path -LiteralPath $receipt) { throw 'A rejected package produced a Release receipt.' }
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
    @{ Name='branch-evidence-link'; Member='docs/COMPATIBILITY.md'; ReplaceCommit='master'; Reject=$true },
    @{ Name='wrong-evidence-revision'; Member='docs/SUPPORTED-PROFILE.md'; ReplaceCommit=('0' * 40); Reject=$true },
    @{ Name='missing-inline-link'; Readme='[docs](docs/missing.md)'; Reject=$true },
    @{ Name='missing-reference-link'; Readme="[docs][text]`n[text]: docs/missing.md"; Reject=$true }
)) {
    $artifact = Copy-PackageCase $case.Name
    $archive = [IO.Compression.ZipFile]::Open($artifact, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $member = if ($case.Member) { $case.Member } else { 'README.md' }
        $entry = $archive.GetEntry($member)
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $original = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($case.ReplaceCommit) {
            $changed = $original -creplace '(?<=https://github.com/Lokad/qpdf/blob/)[0-9a-f]{40}(?=/tests/Compatibility/options\.json)', $case.ReplaceCommit
            if ($changed -ceq $original) { throw "Could not modify the evidence link for $($case.Name)." }
            $original = $changed
        }
        $entry.Delete()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($member).Open())
        try { $writer.WriteLine($original); $writer.WriteLine($case.Readme) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    if ($case.Reject) {
        $issue = if ($case.ReplaceCommit) { 'must match the repository commit' }
            elseif ($case.Heading) { 'Package Markdown heading is missing' }
            else { 'Package Markdown link target is missing' }
        Assert-Rejection $case.Name $issue (Join-Path $PSScriptRoot 'verify-package.ps1') $artifact
    } else {
        $result = Invoke-PackageControl $case.Name $packageVerifier $artifact
        if ($result.ExitCode -ne 0) { throw "$($case.Name) rejected a valid package link. See $testRoot." }
        Write-Output "PASS $($case.Name)"
    }
}

if ((Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash -cne $seedHash) {
    throw 'Negative verification changed the tracked consumer lock.'
}
if ((Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash -cne $packageHash) {
    throw 'Negative verification changed the supplied package.'
}
if ((Get-FileHash -LiteralPath $originalSymbols -Algorithm SHA256).Hash -cne $symbolHash) {
    throw 'Negative verification changed the supplied symbols.'
}
[ordered]@{
    packageSha256 = $packageHash
    symbolsSha256 = $symbolHash
    reviewedLockSha256 = $seedHash
    releaseBuildChecked = $CheckReleaseBuild.IsPresent
    cases = $caseReceipts
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'receipt.json') -Encoding utf8
if (!$KeepWorkspace) { Remove-PackageCheckWorkspace $testRoot }
Write-Output "Consumer verification negative cases passed; evidence: $testRoot"
# Expected child failures leave LASTEXITCODE nonzero in a calling pwsh step.
exit 0
