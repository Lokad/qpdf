param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageArchive.ps1')

function Get-MarkdownProse([string] $text) {
    $visible = [Text.StringBuilder]::new()
    $fence = ''
    foreach ($line in $text -split '\r?\n') {
        $marker = [regex]::Match($line, '^ {0,3}(?<fence>\x60{3,}|~{3,})(?<tail>.*)$')
        if ($fence) {
            if ($marker.Success -and $marker.Groups['fence'].Value.StartsWith($fence, [StringComparison]::Ordinal) -and
                !$marker.Groups['tail'].Value.Trim()) { $fence = '' }
        } elseif ($marker.Success) {
            $fence = $marker.Groups['fence'].Value
        } else {
            [void] $visible.AppendLine($line)
        }
    }
    return $visible.ToString()
}

function Get-MarkdownHeadings([string] $prose) {
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $headingPattern = '(?m)^ {0,3}(?:#{1,6}[ \t]+(?<title>[^\r\n]+)|(?<title>[^\s#>][^\r\n]*)\r?\n {0,3}(?:=+|-+)[ \t]*(?=\r?$))'
    foreach ($heading in [regex]::Matches($prose, $headingPattern)) {
        $title = $heading.Groups['title'].Value.Trim() -replace '[ \t]+#+$', ''
        $title = $title -replace '\[([^\]]+)\]\([^)]+\)', '$1'
        $stem = ($title.ToLowerInvariant() -replace '[^\p{L}\p{N}\p{M}_ \t-]', '') -replace '[ \t]', '-'
        $id = $stem
        for ($number = 1; !$ids.Add($id); $number++) { $id = "$stem-$number" }
    }
    return ,$ids
}

function Resolve-PackageMember([string] $source, [string] $path) {
    if (!$path) { return $source }
    $relative = [Uri]::UnescapeDataString($path).Replace('\', '/')
    $parts = [Collections.Generic.List[string]]::new()
    $directoryEnd = $source.LastIndexOf('/')
    if ($directoryEnd -ge 0 -and !$relative.StartsWith('/', [StringComparison]::Ordinal)) {
        $parts.AddRange($source.Substring(0, $directoryEnd).Split('/'))
    }
    foreach ($segment in $relative.Split('/')) {
        if (!$segment -or $segment -ceq '.') { continue }
        if ($segment -ceq '..') {
            if ($parts.Count -eq 0) { return '' }
            $parts.RemoveAt($parts.Count - 1)
        } else {
            $parts.Add($segment)
        }
    }
    return [string]::Join('/', $parts)
}

function Assert-PackageMarkdownLinks($archive, [string[]] $names) {
    $prose = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $headings = [Collections.Generic.Dictionary[string,Collections.Generic.HashSet[string]]]::new([StringComparer]::Ordinal)
    foreach ($name in $names) {
        if (!$name.EndsWith('.md', [StringComparison]::Ordinal)) { continue }
        $reader = [IO.StreamReader]::new($archive.GetEntry($name).Open())
        try { $prose.Add($name, (Get-MarkdownProse ($reader.ReadToEnd()))) } finally { $reader.Dispose() }
        $headings.Add($name, (Get-MarkdownHeadings $prose[$name]))
    }
    foreach ($document in $prose.GetEnumerator()) {
        # Resolve destinations from the containing package member. Code and
        # external links need no package member or network access.
        $visible = $document.Value -replace '(?<ticks>\x60+)[^\r\n]*?\k<ticks>', ''
        $links = @([regex]::Matches($visible, '\]\(\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^\s)]+))')) +
            @([regex]::Matches($visible, '(?m)^ {0,3}\[[^\]]+\]:[ \t]*(?:<(?<target>[^>\r\n]+)>|(?<target>[^\s]+))'))
        foreach ($link in $links) {
            $target = $link.Groups['target'].Value
            if ($target -match '^(?:[a-z][a-z0-9+.-]*:|//)') { continue }
            $resource, $fragment = $target -split '#', 2
            $member = Resolve-PackageMember $document.Key (($resource -split '\?', 2)[0])
            if ($member -cnotin $names) {
                throw "Package Markdown link target is missing from package ($($document.Key)): $target"
            }
            if ($fragment -and $headings.ContainsKey($member) -and
                !$headings[$member].Contains([Uri]::UnescapeDataString($fragment))) {
                throw "Package Markdown heading is missing ($($document.Key)): $target"
            }
        }
    }
}

function Read-PackageMemberStream([IO.Compression.ZipArchiveEntry] $member) {
    $source = $member.Open()
    try {
        $buffer = [IO.MemoryStream]::new()
        try {
            $source.CopyTo($buffer)
            $buffer.Position = 0
            return ,$buffer
        } catch { $buffer.Dispose(); throw }
    } finally { $source.Dispose() }
}

function Assert-PortableSymbolPair([IO.Compression.ZipArchiveEntry] $library,
    [IO.Compression.ZipArchiveEntry] $symbols, [string] $framework) {
    $assembly = Read-PackageMemberStream $library
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($assembly)
        try {
            $codeView = @($pe.ReadDebugDirectory() | Where-Object IsPortableCodeView)
            if ($codeView.Count -ne 1) { throw "Expected one portable CodeView record for $framework." }
            $expected = $pe.ReadCodeViewDebugDirectoryData($codeView[0])
        } finally { $pe.Dispose() }
    } finally { $assembly.Dispose() }

    $pdb = Read-PackageMemberStream $symbols
    try {
        $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdb)
        try { $actual = [System.Reflection.Metadata.BlobContentId]::new($provider.GetMetadataReader().DebugMetadataHeader.Id) }
        finally { $provider.Dispose() }
    } catch { throw "Cannot read portable symbols for $framework. $($_.Exception.Message)" }
    finally { $pdb.Dispose() }

    if ($actual.Guid -ne $expected.Guid -or $actual.Stamp -ne $codeView[0].Stamp) {
        throw "Portable symbol identity differs for $framework."
    }
}

function Assert-SymbolPackage([IO.Compression.ZipArchive] $package, [string] $symbolPath, [Xml.XmlElement] $metadata) {
    $symbols = [IO.Compression.ZipFile]::OpenRead($symbolPath)
    try {
        Assert-UniquePackageMembers $symbols
        $allowed = '^(\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|Lokad\.qpdf\.nuspec|lib/net(?:10|11)\.0/Lokad\.Qpdf\.pdb)$'
        foreach ($entry in $symbols.Entries) {
            if ($entry.FullName -cnotmatch $allowed) { throw "Unexpected symbol package member: $($entry.FullName)" }
        }
        $symbolMetadata = Read-PackageMetadata $symbols
        if ($symbolMetadata.id -cne $metadata.id -or $symbolMetadata.version -cne $metadata.version) {
            throw 'Symbol package identity/version must match the library package.'
        }
        foreach ($framework in @('net10.0', 'net11.0')) {
            $member = $symbols.GetEntry("lib/$framework/Lokad.Qpdf.pdb")
            if ($null -eq $member) { throw "Missing portable symbols for $framework." }
            Assert-PortableSymbolPair ($package.GetEntry("lib/$framework/Lokad.Qpdf.dll")) $member $framework
        }
    } finally { $symbols.Dispose() }
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$package = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    Assert-UniquePackageMembers $package
    $names = @($package.Entries | ForEach-Object { $_.FullName })
    $allowed = '^(\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|Lokad\.qpdf\.nuspec|README\.md|LICENSE\.txt|CHANGELOG\.md|icon\.png|docs/(?:ARCHITECTURE|BENCHMARKS|COMPATIBILITY|MEMORY|SUPPORTED-PROFILE)\.md|tests/Compatibility/options\.json|lib/net(?:10|11)\.0/Lokad\.Qpdf\.(dll|xml))$'
    foreach ($name in $names) {
        if ($name -cnotmatch $allowed) { throw "Unexpected package member: $name" }
    }
    foreach ($required in @('README.md', 'LICENSE.txt', 'CHANGELOG.md', 'icon.png', 'docs/ARCHITECTURE.md', 'docs/BENCHMARKS.md', 'docs/COMPATIBILITY.md', 'docs/MEMORY.md', 'docs/SUPPORTED-PROFILE.md', 'tests/Compatibility/options.json', 'lib/net10.0/Lokad.Qpdf.dll', 'lib/net10.0/Lokad.Qpdf.xml', 'lib/net11.0/Lokad.Qpdf.dll', 'lib/net11.0/Lokad.Qpdf.xml')) {
        if ($required -cnotin $names) { throw "Missing package member: $required" }
    }

    Assert-PackageMarkdownLinks $package $names

    $metadata = Read-PackageMetadata $package
    if ($metadata.id -cne 'Lokad.qpdf') { throw 'Incorrect package ID.' }
    if ($metadata.description -notmatch 'R2.R6' -or $metadata.releaseNotes -notmatch 'AES-256 R6' -or
        ($metadata.description + ' ' + $metadata.releaseNotes) -match 'non-cryptographic|Password encryption remains unsupported') {
        throw 'Package metadata must describe supported password security.'
    }
    Assert-DependencyFreePackage $metadata
    Assert-SymbolPackage $package ([IO.Path]::ChangeExtension($resolvedPackage, '.snupkg')) $metadata
}
finally { $package.Dispose() }

Write-Output 'Package members are approved and unique, portable symbols match both assemblies, Markdown links resolve, and the core package has no runtime package dependencies.'
