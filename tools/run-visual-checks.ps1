<#
.SYNOPSIS
  Seven finite development-only visual comparisons, using existing authored
  fixtures/builders, pinned qpdf and an explicitly identified pdftoppm renderer.
#>
param(
    [Parameter(Mandatory = $true)] [string] $QpdfExe,
    [Parameter(Mandatory = $true)] [string] $RendererExe,
    [Parameter(Mandatory = $true)] [string] $WorkRoot
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$QpdfExe = (Resolve-Path -LiteralPath $QpdfExe).Path
$RendererExe = (Resolve-Path -LiteralPath $RendererExe).Path
$root = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $root) { throw "Visual workspace already exists: $root" }
if ([Environment]::Version.Major -lt 10) { throw 'Fixture reuse requires PowerShell running on .NET 10.' }
$probe = Join-Path $repo 'tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll'
$bin = Join-Path $repo 'tests/Lokad.Qpdf.Tests/bin/Release/net10.0'
foreach ($file in @($probe, (Join-Path $bin 'Lokad.Qpdf.Tests.dll'))) {
    if (!(Test-Path -LiteralPath $file)) { throw 'Build the Release solution before visual checks.' }
}
& (Join-Path $PSScriptRoot 'bootstrap-qpdf.ps1') -QpdfExe $QpdfExe | Out-Host
$rendererVersion = & $RendererExe -v 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $rendererVersion -notmatch 'pdftoppm version') { throw 'Renderer identification failed.' }
New-Item -ItemType Directory -Path (Join-Path $root 'inputs') | Out-Null
foreach ($name in @('xunit.assert.dll','xunit.core.dll','Lokad.Qpdf.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $bin $name)) | Out-Null
}
$tests = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Lokad.Qpdf.Tests.dll'))
$members = [Reflection.BindingFlags]'Static,NonPublic,Public'
$images = $tests.GetType('Lokad.Qpdf.Tests.Content.ImageOptimizeTests')
foreach ($color in @('gray','rgb')) {
    $noise = $images.GetMethod($(if ($color -eq 'gray') { 'NoiseGray' } else { 'NoiseRgb' }), $members)
    $samples = $noise.Invoke($null, [object[]]@(200,200))
    $space = if ($color -eq 'gray') { 'DeviceGray' } else { 'DeviceRGB' }
    [byte[]]$pdf = $images.GetMethod('BuildImagePage', $members).Invoke($null, [object[]]@(200,200,$space,8,'',$samples))
    [IO.File]::WriteAllBytes((Join-Path $root ('inputs/' + $color + '.pdf')), $pdf)
}
foreach ($name in @('photo-1page.pdf','rotated-ap-1page.pdf','forms-1page.pdf')) {
    Copy-Item -LiteralPath (Join-Path $repo ('tests/Fixtures/' + $name)) -Destination (Join-Path $root ('inputs/' + $name))
}
$appearances = $tests.GetType('Lokad.Qpdf.Tests.Content.GenerateAppearancesTests')
$extras = [Collections.Generic.List[ValueTuple[string,byte[]]]]::new()
$extras.Add([ValueTuple[string,byte[]]]::new('<< /Length 0 >>', [byte[]]@()))
$extras.Add([ValueTuple[string,byte[]]]::new('<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /T (Check) /V /Yes /AS /Off /AP << /N << /Off 6 0 R /Yes 7 0 R >> >> >>', $null))
foreach ($operator in @('S','f')) {
    $paint = [Text.Encoding]::ASCII.GetBytes('0 w 0 0 10 10 re ' + $operator)
    $extras.Add([ValueTuple[string,byte[]]]::new(('<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ' + $paint.Length + ' >>'), $paint))
}
[byte[]]$checkbox = $appearances.GetMethod('BuildPdf', $members).Invoke($null, [object[]]@(' /AcroForm << /Fields [5 0 R] /NeedAppearances true >>',' /Contents 4 0 R /Resources << >> /Annots [5 0 R]',$extras))
[IO.File]::WriteAllBytes((Join-Path $root 'inputs/checkbox.pdf'), $checkbox)

# Fixed before any managed results: 144 dpi, useful content crops. Lossless
# placement/text allows 0.5% mean error and 1% pixels differing by >8/255.
# Native/managed JPEG allows 4% mean and 10% pixels differing by >32/255;
# original/noise-to-lossy allows 20% mean and 30% pixels differing by >64/255.
$cases = @(
    @{Name='overlay'; Input='photo-1page.pdf'; Flags=@('--overlay','inputs/gray.pdf','--'); Crop=@(0,1264,320,320); Jpeg=$false},
    @{Name='underlay'; Input='photo-1page.pdf'; Flags=@('--underlay','inputs/gray.pdf','--'); Crop=@(0,1264,320,320); Jpeg=$false},
    @{Name='rotation'; Input='rotated-ap-1page.pdf'; Flags=@('--flatten-rotation'); Crop=@(1180,180,300,450); Jpeg=$false},
    @{Name='text'; Input='forms-1page.pdf'; Flags=@('--generate-appearances','--flatten-annotations=all'); Crop=@(200,344,400,40); Jpeg=$false},
    @{Name='checkbox'; Input='checkbox.pdf'; Flags=@('--generate-appearances','--flatten-annotations=all'); Crop=@(0,1560,24,24); Jpeg=$false},
    @{Name='gray'; Input='gray.pdf'; Flags=@('--optimize-images','--jpeg-quality=75'); Crop=@(0,1184,400,400); Jpeg=$true},
    @{Name='rgb'; Input='rgb.pdf'; Flags=@('--optimize-images','--jpeg-quality=75'); Crop=@(0,1184,400,400); Jpeg=$true}
)
$results = [Collections.Generic.List[object]]::new()
function Render([string] $Pdf, [string] $Prefix, [int[]] $Crop) {
    & $RendererExe -f 1 -l 1 -singlefile -r 144 -x $Crop[0] -y $Crop[1] -W $Crop[2] -H $Crop[3] $Pdf $Prefix *> ($Prefix + '.log')
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath ($Prefix + '.ppm'))) { throw 'Rendering failed.' }
}
function Compare-Raster([string] $Left, [string] $Right, [int[]] $Crop, [string] $Mean, [string] $Fraction, [string] $Threshold, [string] $Log) {
    & dotnet $probe compare-raster --left $Left --right $Right --expected-width $Crop[2] --expected-height $Crop[3] --max-mean-error $Mean --max-changed-fraction $Fraction --channel-threshold $Threshold *> $Log
    return $LASTEXITCODE
}
function Check-Jpeg([string] $Pdf, [string] $Color, [string] $Log) {
    $json = & $QpdfExe --json --json-key=pages $Pdf 2> ($Log + '.stderr')
    if ($LASTEXITCODE -ne 0) { throw 'Image inspection failed.' }
    $json | Set-Content -LiteralPath $Log -Encoding utf8
    $pages = ($json -join "`n" | ConvertFrom-Json).pages
    if (@($pages).Count -ne 1 -or @($pages[0].images).Count -ne 1) { throw 'Expected one page with one optimized image.' }
    $image = $pages[0].images[0]
    $space = if ($Color -eq 'gray') { '/DeviceGray' } else { '/DeviceRGB' }
    if ($image.width -ne 200 -or $image.height -ne 200 -or $image.bitspercomponent -ne 8 -or
        $image.colorspace -cne $space -or (@($image.filter) -join ',') -cne '/DCTDecode' -or
        $image.object -notmatch '^\d+ \d+ R$') { throw 'JPEG optimization did not preserve image dimensions/colorspace or produce DCT.' }
    $reference = ($image.object -replace ' R$','') -replace ' ',','
    $dictionary = & $QpdfExe "--show-object=$reference" $Pdf 2> ($Log + '.dictionary.stderr')
    if ($LASTEXITCODE -ne 0) { throw 'JPEG dictionary inspection failed.' }
    $dictionary | Set-Content -LiteralPath ($Log + '.dictionary') -Encoding utf8
    $length = [regex]::Match(($dictionary -join "`n"), '/Length (\d+)')
    $components = if ($Color -eq 'gray') { 1 } else { 3 }
    if (!$length.Success -or [long]$length.Groups[1].Value -ge 200 * 200 * $components) { throw 'JPEG replacement did not shrink the original raw image.' }
}
$watch = [Diagnostics.Stopwatch]::StartNew()
foreach ($case in $cases) {
    $dir = Join-Path $root $case.Name
    New-Item -ItemType Directory -Path $dir | Out-Null
    $managed = Join-Path $dir 'managed.pdf'; $native = Join-Path $dir 'native.pdf'
    $input = Join-Path $root ('inputs/' + $case.Input)
    $flags = @($case.Flags | ForEach-Object { if ($_ -like 'inputs/*') { Join-Path $root $_ } else { $_ } })
    & dotnet $probe emit --workdir $root --out ('output.pdf=' + $managed) -- ('inputs/' + $case.Input) @($case.Flags) output.pdf *> (Join-Path $dir 'managed.log')
    if ($LASTEXITCODE -ne 0) { throw "$($case.Name): managed operation failed." }
    & $QpdfExe $input @flags $native *> (Join-Path $dir 'native.log')
    if ($LASTEXITCODE -notin @(0,3)) { throw "$($case.Name): native operation failed." }
    foreach ($pair in @(@('managed',$managed),@('native',$native))) {
        & dotnet $probe compare --qpdf-exe $QpdfExe --input $pair[1] --expected-npages 1 *> (Join-Path $dir ($pair[0] + '-validity.log'))
        if ($LASTEXITCODE -ne 0) { throw "$($case.Name): output validity failed." }
        if ($case.Jpeg) { Check-Jpeg $pair[1] $case.Name (Join-Path $dir ($pair[0] + '-image.json')) }
        Render $pair[1] (Join-Path $dir $pair[0]) $case.Crop
    }
    $mean='0.005'; $fraction='0.01'; $threshold='8'
    if ($case.Jpeg) { $mean='0.04'; $fraction='0.10'; $threshold='32' }
    if ((Compare-Raster ($managed -replace '\.pdf$','.ppm') ($native -replace '\.pdf$','.ppm') $case.Crop $mean $fraction $threshold (Join-Path $dir 'comparison.log')) -ne 0) {
        throw "$($case.Name): visual mismatch; tolerances are unchanged."
    }
    if ($case.Jpeg) {
        Render $input (Join-Path $dir 'original') $case.Crop
        foreach ($kind in @('managed','native')) {
            if ((Compare-Raster (Join-Path $dir ($kind + '.ppm')) (Join-Path $dir 'original.ppm') $case.Crop '0.20' '0.30' '64' (Join-Path $dir ($kind + '-original.log'))) -ne 0) { throw "$kind lossy output exceeds declared tolerance." }
        }
    }
    $results.Add(@{Case=$case.Name; InputSHA256=(Get-FileHash -LiteralPath $input).Hash; Crop=$case.Crop; MeanLimit=$mean; ChangedLimit=$fraction; Threshold=$threshold; Comparison=(Get-Content -LiteralPath (Join-Path $dir 'comparison.log') -Raw).Trim(); Result='pass'})
    Write-Output "$($case.Name): pass"
}
# Valid PDF with a deliberately misplaced image, and stale flattened appearance.
& $QpdfExe (Join-Path $root 'overlay/native.pdf') --rotate=180 (Join-Path $root 'negative-image.pdf') *> (Join-Path $root 'negative-image.log')
if ($LASTEXITCODE -notin @(0,3)) { throw 'Image negative preparation failed.' }
Render (Join-Path $root 'negative-image.pdf') (Join-Path $root 'negative-image') $cases[0].Crop
if ((Compare-Raster (Join-Path $root 'negative-image.ppm') (Join-Path $root 'overlay/native.ppm') $cases[0].Crop '0.005' '0.01' '8' (Join-Path $root 'negative-image-comparison.log')) -ne 2) { throw 'Misplaced-image negative did not fail.' }
& $QpdfExe (Join-Path $root 'inputs/forms-1page.pdf') --flatten-annotations=all (Join-Path $root 'negative-text.pdf') *> (Join-Path $root 'negative-text.log')
if ($LASTEXITCODE -notin @(0,3)) { throw 'Appearance negative preparation failed.' }
Render (Join-Path $root 'negative-text.pdf') (Join-Path $root 'negative-text') $cases[3].Crop
if ((Compare-Raster (Join-Path $root 'negative-text.ppm') (Join-Path $root 'text/native.ppm') $cases[3].Crop '0.005' '0.01' '8' (Join-Path $root 'negative-text-comparison.log')) -ne 2) { throw 'Stale-appearance negative did not fail.' }
$sourceState = if (@(& git -C $repo status --porcelain -- src tools tests .github Directory.Build.props).Count -eq 0) { 'committed' } else { 'uncommitted changes' }
@{SourceCommit=(& git -C $repo rev-parse HEAD); SourceState=$sourceState; RendererVersion=$rendererVersion.Trim(); RendererSHA256=(Get-FileHash -LiteralPath $RendererExe).Hash; QpdfSHA256=(Get-FileHash -LiteralPath $QpdfExe).Hash; Cases=$results; OriginalJpegTolerance=@{MeanLimit='0.20'; ChangedLimit='0.30'; Threshold='64'}; NegativeControls='misplaced image and stale appearance reject'; WallSeconds=$watch.Elapsed.TotalSeconds} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'result.json') -Encoding utf8
