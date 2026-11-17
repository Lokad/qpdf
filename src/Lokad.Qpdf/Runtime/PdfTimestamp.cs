namespace Lokad.Qpdf.Runtime;

// Full timestamps used by qpdf-style authoring. Existing PDF metadata remains
// readable even when it does not use this grammar; calendar ranges are separate.
internal static class PdfTimestamp
{
    internal static bool HasFullSyntax(ReadOnlySpan<char> value)
    {
        if (value.Length is not (16 or 17 or 23) || !value.StartsWith("D:", StringComparison.Ordinal)) { return false; }
        for (int i = 2; i < 16; i++) { if (value[i] is < '0' or > '9') { return false; } }
        if (value.Length == 16) { return true; }
        if (value.Length == 17) { return value[16] == 'Z'; }
        if (value[16] is not ('+' or '-') || value[19] != '\'' || value[22] != '\'') { return false; }
        for (int i = 17; i < 22; i++) { if (i != 19 && value[i] is < '0' or > '9') { return false; } }
        return true;
    }

    internal static void RequireAuthored(string value, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        if (!HasFullSyntax(value.AsSpan()))
        { throw new QpdfParseException("Attachment date must be a full PDF timestamp (D:YYYYMMDDHHmmSS with an optional timezone).", null, null); }
        governor.CheckCancellation();
    }
}
