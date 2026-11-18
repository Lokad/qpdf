function Assert-UniquePackageMembers([IO.Compression.ZipArchive] $archive) {
    # Use one path rule on every platform where NuGet may extract the package.
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (!$names.Add($entry.FullName)) { throw "Duplicate package member: $($entry.FullName)" }
    }
}

function Read-PackageMetadata([IO.Compression.ZipArchive] $archive) {
    $nuspec = $archive.GetEntry('Lokad.qpdf.nuspec')
    if ($null -eq $nuspec) { throw 'Missing Lokad.qpdf package manifest.' }
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($null -eq $metadata) { throw 'Missing Lokad.qpdf package metadata.' }
    return ,$metadata
}

function Assert-DependencyFreePackage([Xml.XmlElement] $metadata) {
    if (@($metadata.SelectNodes('*[local-name()="dependencies"]//*[local-name()="dependency"]')).Count -ne 0) {
        throw 'The core package must have no runtime package dependencies.'
    }
}
