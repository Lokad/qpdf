using System.Text.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Json;

internal static partial class JsonImport
{
    private readonly record struct Metadata(PdfVersion? Version, bool CalledAllPages, bool PushedInherited);

    // Creation validates preparation declarations but does not execute them.
    // Update actions are monotonic: a later false cannot undo an earlier true.
    private static Metadata ReadMetadata(JsonElement meta, bool requireVersion, ResourceGovernor governor)
    {
        PdfVersion? version = null;
        bool sawJsonVersion = false, calledAllPages = false, pushedInherited = false;
        foreach (JsonProperty property in meta.EnumerateObject())
        {
            governor.CheckCancellation();
            string name = ReadPropertyName(property);
            switch (name)
            {
                case "jsonversion":
                    sawJsonVersion = true;
                    if (property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out int number) || number != 2)
                    { throw new QpdfParseException("qpdf JSON version must be numeric value 2.", 0L, null); }
                    break;
                case "pdfversion":
                    if (property.Value.ValueKind != JsonValueKind.String || ReadString(property.Value) is not string text)
                    { throw new QpdfParseException("qpdf JSON pdfversion must be a string.", 0L, null); }
                    version = ParsePdfVersion(text, governor);
                    break;
                case "calledgetallpages":
                case "pushedinheritedpageresources":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    { throw new QpdfParseException("qpdf JSON " + name + " must be a boolean.", 0L, null); }
                    if (property.Value.ValueKind == JsonValueKind.True)
                    {
                        if (name == "calledgetallpages") { calledAllPages = true; }
                        else { pushedInherited = true; }
                    }
                    break;
            }
        }
        if (!sawJsonVersion)
        { throw new QpdfParseException("qpdf JSON jsonversion was not seen.", 0L, null); }
        if (requireVersion && version is null)
        { throw new QpdfParseException("qpdf JSON pdfversion was not seen.", 0L, null); }
        return new(version, calledAllPages, pushedInherited);
    }
}
