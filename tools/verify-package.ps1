param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$package = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $names = @($package.Entries | ForEach-Object { $_.FullName })
    $allowed = '^(\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|Lokad\.qpdf\.nuspec|README\.md|LICENSE\.txt|CHANGELOG\.md|icon\.png|lib/net10\.0/Lokad\.Qpdf\.(dll|xml))$'
    foreach ($name in $names) {
        if ($name -notmatch $allowed) { throw "Unexpected package member: $name" }
    }
    foreach ($required in @('README.md', 'LICENSE.txt', 'CHANGELOG.md', 'icon.png', 'lib/net10.0/Lokad.Qpdf.dll', 'lib/net10.0/Lokad.Qpdf.xml')) {
        if ($required -notin $names) { throw "Missing package member: $required" }
    }

    $reader = [IO.StreamReader]::new($package.GetEntry('README.md').Open())
    try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
    # Check inline and reference-style destinations without fetching external URLs.
    $links = @([regex]::Matches($readme, '\]\(\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^\s)]+))')) +
        @([regex]::Matches($readme, '(?m)^\s{0,3}\[[^\]]+\]:\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^\s]+))'))
    foreach ($link in $links) {
        $target = $link.Groups['target'].Value
        if ($target -match '^(?:[a-z][a-z0-9+.-]*:|//|#)') { continue }
        $relative = [Uri]::UnescapeDataString(($target -split '[?#]', 2)[0]).Replace('\', '/')
        if ($relative.StartsWith('./', [StringComparison]::Ordinal)) { $relative = $relative.Substring(2) }
        if ($relative -cnotin $names) { throw "README link target is missing from package: $target" }
    }

    $nuspec = $package.GetEntry('Lokad.qpdf.nuspec')
    if ($null -eq $nuspec) { throw 'Missing package manifest.' }
    $reader = [System.IO.StreamReader]::new($nuspec.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.id -cne 'Lokad.qpdf') { throw 'Incorrect package ID.' }
    $dependencies = @($metadata.SelectNodes('*[local-name()="dependencies"]//*[local-name()="dependency"]'))
    if ($dependencies.Count -ne 0) {
        throw 'The core package must have no runtime package dependencies.'
    }
}
finally { $package.Dispose() }

$symbolPath = [System.IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')
$symbols = [System.IO.Compression.ZipFile]::OpenRead($symbolPath)
try {
    $foundPdb = $false
    foreach ($entry in $symbols.Entries) {
        if ($entry.FullName -eq 'lib/net10.0/Lokad.Qpdf.pdb') { $foundPdb = $true }
        if ($entry.FullName -notmatch '^(\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|Lokad\.qpdf\.nuspec|lib/net10\.0/Lokad\.Qpdf\.pdb)$') {
            throw "Unexpected symbol package member: $($entry.FullName)"
        }
    }
    if (-not $foundPdb) { throw 'Missing portable symbols.' }
}
finally { $symbols.Dispose() }

Write-Output 'Package and symbols contain only approved files, README file links resolve, and the core package has no runtime package dependencies.'
