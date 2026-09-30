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

    $nuspec = $package.GetEntry('Lokad.qpdf.nuspec')
    if ($null -eq $nuspec) { throw 'Missing package manifest.' }
    $reader = [System.IO.StreamReader]::new($nuspec.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.id -cne 'Lokad.qpdf') { throw 'Incorrect package ID.' }
    $dependencies = @($metadata.SelectNodes('*[local-name()="dependencies"]/*[local-name()="group"]/*[local-name()="dependency"]'))
    if ($dependencies.Count -ne 1 -or $dependencies[0].id -cne 'SixLabors.ImageSharp' -or $dependencies[0].version -cne '[3.1.12]') {
        throw 'The only runtime dependency must be exactly SixLabors.ImageSharp [3.1.12].'
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

Write-Output 'Package and symbols contain only approved files; runtime dependency is pinned to ImageSharp 3.1.12.'
