# Classic framing for authored inspection fixtures. Callers supply object
# bodies, stream payloads and trailer entries independently of any PDF writer.
function New-ReferenceClassicPdf {
    param([hashtable[]] $Objects, [string] $Trailer, [Nullable[long]] $StartXref)

    $pdf = [IO.MemoryStream]::new()
    try {
        $write = { param([string] $Text) $pdf.Write([Text.Encoding]::ASCII.GetBytes($Text)) }
        $offsets = [long[]]::new($Objects.Count)
        $culture = [Globalization.CultureInfo]::InvariantCulture
        & $write "%PDF-1.4`n"
        for ($index = 0; $index -lt $Objects.Count; $index++) {
            $object = $Objects[$index]
            $offsets[$index] = $pdf.Position
            & $write "$($index+1) $([int]$object['Generation']) obj`n$($object['Body'])`n"
            if ($object.ContainsKey('Data')) {
                $data = [byte[]]$object['Data']
                & $write "stream`n"
                if ($data.Length -gt 0) { $pdf.Write($data, 0, $data.Length) }
                & $write "`nendstream`n"
            }
            & $write "endobj`n"
        }
        $xref = if ($null -eq $StartXref) { $pdf.Position } else { $StartXref }
        & $write "xref`n0 $($Objects.Count+1)`n0000000000 65535 f `n"
        for ($index = 0; $index -lt $Objects.Count; $index++) {
            $offset = $offsets[$index].ToString('D10', $culture)
            $generation = ([int]$Objects[$index]['Generation']).ToString('D5', $culture)
            & $write "$offset $generation n `n"
        }
        & $write "trailer`n<< $Trailer >>`nstartxref`n$xref`n%%EOF`n"
        return ,$pdf.ToArray()
    } finally {
        $pdf.Dispose()
    }
}
