namespace Lokad.Qpdf.Runtime;

// Shared cores return values to both command and typed routes. Only a public
// typed boundary creates a heap wrapper from its prepaid capacity. Backing
// arrays, mappings, diagnostics and versions retain their separate audit scope.
internal readonly record struct PageSelectionOutcome(int SourcePageCount, IReadOnlyList<int> SourcePages,
    int PageCount, IReadOnlyList<QpdfDiagnostic> Warnings)
{
    internal PdfPageSelectionResult Present(ResourceGovernor governor)
    {
        PdfPageSelectionResult result = new(SourcePageCount, SourcePages, PageCount, Warnings, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}

internal readonly record struct MultiPageSelectionOutcome(int PageCount, IReadOnlyList<PdfPageSelectionMapping> Mapping,
    IReadOnlyList<int> SourcePageCounts, IReadOnlyList<QpdfPath> OutputPaths, IReadOnlyList<QpdfDiagnostic> Warnings)
{
    internal PdfMultiPageSelectionResult Present(ResourceGovernor governor)
    {
        PdfMultiPageSelectionResult result = new(PageCount, Mapping, SourcePageCounts, OutputPaths, Warnings, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}

internal readonly record struct AttachmentExtractionOutcome(string Key, long ByteCount, IReadOnlyList<QpdfDiagnostic> Warnings)
{
    internal QpdfAttachmentExtraction Present(ResourceGovernor governor)
    {
        QpdfAttachmentExtraction result = new(Key, ByteCount, Warnings, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}

internal readonly record struct JsonInspectionOutcome(string Json, IReadOnlyList<QpdfDiagnostic> Warnings)
{
    internal QpdfJsonResult Present(ResourceGovernor governor)
    {
        QpdfJsonResult result = new(Json, Warnings, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}

internal readonly record struct JsonImportOutcome(int PageCount, IReadOnlyList<QpdfDiagnostic> Warnings)
{
    internal QpdfJsonImportResult Present(ResourceGovernor governor)
    {
        QpdfJsonImportResult result = new(PageCount, Warnings, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}

internal readonly record struct TypedCheckOutcome(int PageCount, PdfVersion Version, IReadOnlyList<QpdfCheckOutcome> Checks,
    IReadOnlyList<QpdfDiagnostic> Diagnostics)
{
    internal QpdfCheckResult Present(ResourceGovernor governor)
    {
        QpdfCheckResult result = new(PageCount, Version, Checks, Diagnostics, governor.TypedCounters, true);
        governor.AcceptTypedResult();
        return result;
    }
}
