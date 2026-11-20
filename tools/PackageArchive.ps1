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

function Remove-PackageCheckWorkspace([string] $EvidenceDirectory) {
    $evidence = Get-Item -LiteralPath $EvidenceDirectory -ErrorAction Stop
    $expected = [IO.Path]::GetFullPath((Join-Path $evidence.FullName 'work'))
    $workspace = Get-Item -LiteralPath $expected -ErrorAction Stop
    if ($workspace -isnot [IO.DirectoryInfo] -or $workspace.FullName -cne $expected -or
        $workspace.Parent.FullName -cne $evidence.FullName) {
        throw "Unexpected package check workspace: $expected"
    }
    foreach ($directory in @($workspace, $evidence, $evidence.Parent, $evidence.Parent.Parent)) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Package check workspace path contains a filesystem link: $($directory.FullName)"
        }
    }
    $link = Get-ChildItem -LiteralPath $workspace.FullName -Force -Recurse -Attributes ReparsePoint |
        Select-Object -First 1
    if ($null -ne $link) { throw "Package check workspace contains a filesystem link: $($link.FullName)" }
    Remove-Item -LiteralPath $workspace.FullName -Recurse -Force -ErrorAction Stop
}
